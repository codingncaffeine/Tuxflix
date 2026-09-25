#!/bin/sh
# Launches Tuxflix inside a hardened transient systemd user unit.
#
# The application's own posture is strong — the token lives in the keyring, the server's
# answers are parsed strictly, nothing phones home — so this is confinement: if the process is
# ever made to misbehave, the unit decides what it can reach. It keeps the holes a media player
# is for: the display socket, the sound server, the session bus (keyring, media controls, the
# desktop portal), the network, and the graphics devices for hardware decoding.
#
# Installed as the /usr/bin launcher by the packages, the library path filled in at install
# time (@LIB@). Without a running systemd user manager, or with TUXFLIX_NO_SANDBOX=1 (the
# debugging escape), it execs the binary directly, so launching never breaks on an odd session.

TUXFLIX="${TUXFLIX_BINARY:-@LIB@/tuxflix}"

if [ "${TUXFLIX_NO_SANDBOX:-0}" = "1" ] || ! command -v systemd-run >/dev/null 2>&1 \
    || ! systemctl --user show-environment >/dev/null 2>&1; then
    exec "$TUXFLIX" "$@"
fi

DATA="${XDG_DATA_HOME:-$HOME/.local/share}/tuxflix"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/tuxflix"
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/tuxflix"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/tuxflix"

# The single-instance socket and lock: the runtime directory itself stays read-only to the unit
# (other applications' sockets, the session's own), this one folder in it is the application's.
RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/tuxflix"

# A write path must exist before the namespace is built: a ReadWritePaths entry that is missing
# is skipped (the "-" prefix below), and the application could then never create it.
mkdir -p "$DATA" "$CONFIG" "$STATE" "$CACHE" "$RUNTIME"

# Where a download may land besides the application's own data folder: the reader's own folders
# (the ones the desktop names: Videos, Music, Downloads and the rest) are writable, everything
# else under $HOME stays read-only. The file picker is the desktop's and knows nothing of the
# unit, so the application is told below exactly what was opened, and can name the folders that
# would have worked when a write is refused.
#
# xdg-user-dir answers with $HOME itself for a folder it has no entry for, and $HOME is the one
# thing that must not be opened, so that answer is dropped; a folder that does not exist is left out.
user_dir() {
    case "$1" in
        DESKTOP) fallback="$HOME/Desktop" ;;
        DOCUMENTS) fallback="$HOME/Documents" ;;
        DOWNLOAD) fallback="$HOME/Downloads" ;;
        MUSIC) fallback="$HOME/Music" ;;
        PICTURES) fallback="$HOME/Pictures" ;;
        VIDEOS) fallback="$HOME/Videos" ;;
        PUBLICSHARE) fallback="$HOME/Public" ;;
        TEMPLATES) fallback="$HOME/Templates" ;;
        *) fallback="" ;;
    esac
    dir="$(command -v xdg-user-dir >/dev/null 2>&1 && xdg-user-dir "$1")"
    case "$dir" in ""|"$HOME"|"$HOME/") dir="$fallback" ;; esac
    printf '%s' "$dir"
}

PLACES=""
for name in VIDEOS MUSIC DOWNLOAD DESKTOP DOCUMENTS PICTURES PUBLICSHARE TEMPLATES; do
    dir="$(user_dir "$name")"
    case "$dir" in ""|"$HOME"|"$HOME/") continue ;; esac
    [ -d "$dir" ] || continue
    PLACES="${PLACES}${dir}
"
done

# The transient unit takes the user manager's environment, not this shell's, so what matters
# travels explicitly. The display variables ALWAYS travel, empty when unset here: left out, the
# unit would take the session's real display from the user manager, and a run meant for another
# screen (a nested compositor) would open on the desktop instead.
set -- "$TUXFLIX" "$@"
for var in DISPLAY WAYLAND_DISPLAY; do
    eval "value=\${$var:-}"
    set -- "--setenv=$var=$value" "$@"
done
for var in XAUTHORITY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS \
    LANG LANGUAGE LC_ALL LC_MESSAGES XDG_DATA_HOME XDG_CONFIG_HOME XDG_STATE_HOME XDG_CACHE_HOME \
    PULSE_SERVER PIPEWIRE_REMOTE; do
    eval "value=\${$var+x}"
    [ -n "$value" ] && set -- "--setenv=$var" "$@"
done
for var in $(env | sed -n 's/^\(TUXFLIX_[A-Z0-9_]*\)=.*/\1/p'); do
    case "$var" in TUXFLIX_BINARY|TUXFLIX_UNIT) continue ;; esac
    set -- "--setenv=$var" "$@"
done

# The reader's folders, one ReadWritePaths each, and the same list handed to the application
# (colon-separated, as PATH is). Globbing is off for the walk: a folder name is a name, not a pattern.
WRITABLE=""
IFS_SAVED="$IFS"
IFS='
'
set -f
for dir in $PLACES; do
    set -- "--property=ReadWritePaths=-\"$dir\"" "$@"
    WRITABLE="${WRITABLE:+$WRITABLE:}$dir"
done
set +f
IFS="$IFS_SAVED"
set -- "--setenv=TUXFLIX_SANDBOX=1" "--setenv=TUXFLIX_SANDBOX_WRITABLE=$DATA${WRITABLE:+:$WRITABLE}" "$@"

# A test harness names the unit so that it can stop it: a timeout kills systemd-run, not the unit.
[ -n "${TUXFLIX_UNIT:-}" ] && set -- "--unit=$TUXFLIX_UNIT" "$@"

# MemoryDenyWriteExecute stays off: the .NET JIT needs pages that are written and then run.
# AF_NETLINK stays: device enumeration (udev, a game controller) goes over netlink.
# The graphics devices stay reachable (no PrivateDevices): hardware decoding opens the render
# node and the NVIDIA device files.
# mincore is admitted by name: Mesa's EGL loader probes its own mappings with it while the window's
# graphics come up, and @system-service does not carry it, so the filter killed the process with
# SIGSYS before the first frame. It is a read-only question about the process's own memory.
# /run is made read-only by name: ProtectSystem=strict should cover it, but in a user manager
# (systemd 261, measured) ProtectKernelTunables and ProtectControlGroups each leave the /run mount
# writable, and with it every drive the desktop mounts under /run/media. The runtime folder above
# is carved back out.
# A terminal launch gets a pty so Ctrl+C reaches the application; a desktop launch takes the pipe.
IO=--pipe
[ -t 0 ] && IO=--pty

exec systemd-run --user --quiet --collect --wait "$IO" \
    --property=Description="Tuxflix (hardened)" \
    --working-directory="$HOME" \
    --property=NoNewPrivileges=yes \
    --property=ProtectSystem=strict \
    --property=ReadOnlyPaths=/run \
    --property=ProtectHome=read-only \
    --property=ReadWritePaths="$DATA" \
    --property=ReadWritePaths="$CONFIG" \
    --property=ReadWritePaths="$STATE" \
    --property=ReadWritePaths="$CACHE" \
    --property=ReadWritePaths="$RUNTIME" \
    --property=PrivateTmp=yes \
    --property=CapabilityBoundingSet= \
    --property=LockPersonality=yes \
    --property=ProtectKernelModules=yes \
    --property=ProtectKernelTunables=yes \
    --property=ProtectKernelLogs=yes \
    --property=ProtectControlGroups=yes \
    --property=ProtectClock=yes \
    --property=ProtectHostname=yes \
    --property=RestrictRealtime=yes \
    --property=RestrictNamespaces=yes \
    --property=RestrictSUIDSGID=yes \
    --property=SystemCallArchitectures=native \
    --property=RestrictAddressFamilies="AF_UNIX AF_INET AF_INET6 AF_NETLINK" \
    --property=SystemCallFilter="@system-service mincore" \
    "$@"
