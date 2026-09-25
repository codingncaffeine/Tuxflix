#!/usr/bin/env bash
# Installs the desktop entry, the icons and the AppStream metainfo.
#
# Called by the .deb and .rpm builds, the AUR package and a reader installing the tarball by
# hand, so all of them end up with the same desktop integration. PREFIX and DESTDIR follow the
# usual conventions.
set -euo pipefail

PREFIX="${PREFIX:-/usr}"
DESTDIR="${DESTDIR:-}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ID=io.github.codingncaffeine.Tuxflix

install -Dm644 "$HERE/linux/$ID.desktop" "$DESTDIR$PREFIX/share/applications/$ID.desktop"
install -Dm644 "$HERE/$ID.metainfo.xml" "$DESTDIR$PREFIX/share/metainfo/$ID.metainfo.xml"
for size in 16 22 24 32 48 64 96 128 256 512; do
    install -Dm644 "$HERE/linux/icons/hicolor/${size}x${size}/apps/$ID.png" \
        "$DESTDIR$PREFIX/share/icons/hicolor/${size}x${size}/apps/$ID.png"
done

# Caches are refreshed only for a live install; a package leaves that to the package manager.
if [ -z "$DESTDIR" ]; then
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$PREFIX/share/applications" || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -qtf "$PREFIX/share/icons/hicolor" || true
fi

echo "Installed the desktop entry, icons and metainfo under $DESTDIR$PREFIX."
