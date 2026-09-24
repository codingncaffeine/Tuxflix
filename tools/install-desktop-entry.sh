#!/usr/bin/env bash
# Puts Tuxflix's desktop entry and icons for THIS build into ~/.local/share, so the desktop shows
# Tuxflix's name and icon in the taskbar, the window switcher and the application menu (on Wayland
# the desktop finds both through the desktop entry). The entry starts this build.
#
#   tools/install-desktop-entry.sh            install or refresh
#   tools/install-desktop-entry.sh --remove   take out exactly what it put in
#
# A packaged Tuxflix installs the same entry system-wide; remove this one first, since the copy
# in ~/.local/share wins over the package's.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

ID=io.github.codingncaffeine.Tuxflix
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
BIN="$TUXFLIX_ROOT/src/Tuxflix.App/bin/Release/net10.0/tuxflix"
SIZES=(16 22 24 32 48 64 96 128 256 512)

if [[ "${1:-}" == "--remove" ]]; then
  rm -f "$DATA/applications/$ID.desktop"
  for size in "${SIZES[@]}"; do rm -f "$DATA/icons/hicolor/${size}x${size}/apps/$ID.png"; done
  echo "Removed the Tuxflix desktop entry and icons from $DATA."
else
  [[ -x "$BIN" ]] || { echo "Build Tuxflix first: $BIN is missing." >&2; exit 1; }
  for size in "${SIZES[@]}"; do
    install -Dm644 "$TUXFLIX_ROOT/packaging/linux/icons/hicolor/${size}x${size}/apps/$ID.png" "$DATA/icons/hicolor/${size}x${size}/apps/$ID.png"
  done
  mkdir -p "$DATA/applications"
  sed -e "s|^Exec=.*|Exec=\"$BIN\"|" -e "s|^TryExec=.*|TryExec=$BIN|" \
    "$TUXFLIX_ROOT/packaging/linux/$ID.desktop" > "$DATA/applications/$ID.desktop"
  echo "Installed the Tuxflix desktop entry for $BIN."
fi

# Tell the desktop: the menu and icon caches, and KDE's service cache.
command -v update-desktop-database > /dev/null && update-desktop-database -q "$DATA/applications" || true
# The GTK icon cache only if one is already there: a new one could hide icons other programs add later.
[[ -f "$DATA/icons/hicolor/icon-theme.cache" ]] && command -v gtk-update-icon-cache > /dev/null && gtk-update-icon-cache -q -f -t "$DATA/icons/hicolor" 2> /dev/null || true
command -v kbuildsycoca6 > /dev/null && kbuildsycoca6 > /dev/null 2>&1 || true
