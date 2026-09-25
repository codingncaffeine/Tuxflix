#!/usr/bin/env bash
# The dependency test every build gets: install the .rpm into a clean Fedora container, sweep every
# ELF file for a library the package did not bring, and start the binary with no display.
#
#   PASS is: the install resolves; the only "not found" is liblttng-ust (the .NET runtime's optional
#   tracer, opened only when tracing, benign in every publish); libmpv is present; and the start
#   probe stops at the display, meaning the runtime, Avalonia and every native library loaded and
#   only the screen was missing.
set -euo pipefail
cd "$(dirname "$0")/.."
RPM="${RPM:-Tuxflix.rpm}"
IMAGE="${IMAGE:-fedora:42}"
OUT="$(pwd)/packaging/out"
[ -f "$OUT/$RPM" ] || { echo "No $OUT/$RPM — run packaging/build-release.sh first." >&2; exit 1; }

echo "── $IMAGE: install $RPM, sweep, probe"
podman run --rm -v "$OUT:/out:ro" "$IMAGE" bash -c '
set -u

if ! dnf install -y -q /out/'"$RPM"' > /tmp/install.log 2>&1; then
    echo "INSTALL FAILED"; tail -30 /tmp/install.log; exit 2
fi
echo "installed:"; rpm -q --queryformat "%{NAME} %{VERSION}-%{RELEASE} %{ARCH}\n" tuxflix
echo "── ldd sweep (anything not found):"
found=0
: > /tmp/missing
for f in $(find /usr/lib/tuxflix -type f \( -perm -u+x -o -name "*.so*" \) ); do
    if [ "$(head -c 4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ]; then
        found=$((found+1))
        ldd "$f" 2>/dev/null | grep "not found" | sed "s|^|  $(basename "$f"): |" >> /tmp/missing
    fi
done
sort -u /tmp/missing
echo "  ($found ELF files scanned)"
echo "── what playback and sign-in need:"
if ldconfig -p | grep -q "libmpv.so.2"; then echo "  libmpv.so.2: present"; else echo "  libmpv.so.2: MISSING"; fi
if command -v secret-tool > /dev/null; then echo "  secret-tool: present"; else echo "  secret-tool: MISSING"; fi
echo "── start probe (no display; expect it to stop at the display):"
env -u DISPLAY -u WAYLAND_DISPLAY TUXFLIX_NO_SANDBOX=1 timeout 15 /usr/bin/tuxflix --version 2>&1 | head -4
env -u DISPLAY -u WAYLAND_DISPLAY TUXFLIX_NO_SANDBOX=1 timeout 15 /usr/bin/tuxflix 2>&1 | grep -v "^[0-9:.]* *[0-9.]*s INF" | head -6
echo "── desktop integration:"
ls /usr/share/applications/io.github.codingncaffeine.Tuxflix.desktop /usr/share/icons/hicolor/256x256/apps/io.github.codingncaffeine.Tuxflix.png /usr/share/metainfo/io.github.codingncaffeine.Tuxflix.metainfo.xml
' 2>&1 | grep -v -E 'overlay|graph driver|vfs' || true
