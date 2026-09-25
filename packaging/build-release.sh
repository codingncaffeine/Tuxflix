#!/usr/bin/env bash
# Builds the Linux release artifacts, one self-contained publish per architecture:
#   Tuxflix-<ver>-linux-<arch>.tar.gz  extract anywhere and run ./tuxflix, or run
#                                      packaging/install-desktop-files.sh for the desktop entry
#   Tuxflix.deb / Tuxflix-arm64.deb    system install under /usr/lib/tuxflix, launcher /usr/bin/tuxflix
#   Tuxflix.rpm / Tuxflix-aarch64.rpm  the same layout for Fedora, openSUSE and their relatives
#
#   bash packaging/build-release.sh            # x64, the default and the one the AUR reads
#   bash packaging/build-release.sh arm64      # aarch64 only
#   bash packaging/build-release.sh all        # both
#
# The x64 names are a contract: the AUR package (packaging/aur/PKGBUILD) repackages the tarball
# from the GitHub release. The .deb and .rpm are named for the person downloading them: a
# software centre titles a sideloaded file by its name up to the first dot.
#
# Every build is meant to be followed by packaging/test-deb.sh and packaging/test-rpm.sh: the
# native libraries are exactly what works on a development machine and fails on a clean install.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=packaging/out

ARCHES=("${@:-x64}")
[ "${ARCHES[0]}" = "all" ] && ARCHES=(x64 arm64)
for arch in "${ARCHES[@]}"; do
    case "$arch" in
        x64|arm64) ;;
        *) echo "Unknown architecture '$arch' — expected x64, arm64 or all." >&2; exit 1 ;;
    esac
done

rm -rf "$OUT" && mkdir -p "$OUT"

build_one() {
    local arch="$1" pub="$OUT/publish-$1"
    local debarch rpmarch suffix
    case "$arch" in
        x64)   debarch=amd64; rpmarch=x86_64;  suffix="" ;;
        arm64) debarch=arm64; rpmarch=aarch64; suffix="-arm64" ;;
    esac
    mkdir -p "$pub"

    # A publish reuses what an earlier one left under obj and bin, and an old intermediate can
    # carry XAML that was never compiled into the assembly: the application then starts to a blank
    # window. So the architecture's own folders go first, and the result is checked below.
    rm -rf "src/Tuxflix.App/obj/Release/net10.0/linux-$arch" "src/Tuxflix.App/bin/Release/net10.0/linux-$arch"

    echo "── publish v$VER (self-contained linux-$arch, Release)"
    "$DOTNET" publish src/Tuxflix.App/Tuxflix.App.csproj -c Release -r "linux-$arch" --self-contained true -o "$pub" -v q

    local compiled
    compiled=$(strings -a "$pub/tuxflix.dll" | grep -c CompiledAvaloniaXaml || true)
    if [ "$compiled" -lt 1 ]; then
        echo "The published assembly carries no compiled XAML (a stale build?): stopping." >&2
        exit 1
    fi

    cp LICENSE "$pub/LICENSE"
    cp NOTICES.txt "$pub/NOTICES.txt"

    # The runtime's LTTng tracing shim is opened only when tracing is asked for, and wants a
    # library none of the targets ship; nothing in the package may want what it did not bring.
    rm -f "$pub/libcoreclrtraceptprovider.so"

    # What a person unpacking the tarball, and the AUR package building from it, find beside the binary.
    mkdir -p "$pub/packaging/linux"
    cp packaging/install-desktop-files.sh packaging/tuxflix-launcher.sh packaging/io.github.codingncaffeine.Tuxflix.metainfo.xml "$pub/packaging/"
    cp -a packaging/linux/. "$pub/packaging/linux/"

    echo "── tarball (linux-$arch)"
    tar -C "$pub" -czf "$OUT/Tuxflix-$VER-linux-$arch.tar.gz" .

    # The tree both packages install, assembled once.
    local root="$OUT/root-$arch"
    rm -rf "$root"
    mkdir -p "$root/usr/lib/tuxflix" "$root/usr/bin" "$root/usr/share/doc/tuxflix"
    cp -a "$pub/." "$root/usr/lib/tuxflix/"
    rm -rf "$root/usr/lib/tuxflix/packaging"
    rm -f "$root/usr/lib/tuxflix/LICENSE" "$root/usr/lib/tuxflix/NOTICES.txt"
    cp LICENSE "$root/usr/share/doc/tuxflix/copyright"
    cp NOTICES.txt "$root/usr/share/doc/tuxflix/NOTICES.txt"
    DESTDIR="$root" PREFIX=/usr bash packaging/install-desktop-files.sh > /dev/null
    # The launcher confines the application in a hardened transient systemd user unit; it execs
    # the binary directly where there is no user manager to ask.
    sed 's|@LIB@|/usr/lib/tuxflix|' packaging/tuxflix-launcher.sh > "$root/usr/bin/tuxflix"
    chmod 755 "$root/usr/bin/tuxflix"

    echo "── deb ($debarch)"
    local deb="$OUT/debroot-$arch"
    rm -rf "$deb"
    cp -a "$root" "$deb"
    mkdir -p "$deb/DEBIAN"
    local installed_kb
    installed_kb=$(du -sk "$deb/usr" | cut -f1)
    # libmpv plays everything and is the one library the application cannot start playback
    # without (it says so plainly when missing). secret-tool keeps the sign-in in the keyring.
    # SDL3 is what reads a game controller for the TV mode; without it the keyboard does.
    cat > "$deb/DEBIAN/control" <<CTRL
Package: tuxflix
Version: $VER
Section: video
Priority: optional
Architecture: $debarch
Installed-Size: $installed_kb
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72, zlib1g, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libice6, libsm6, libfontconfig1, libfreetype6, libegl1, libgl1, libwayland-client0, libwayland-egl1, libwayland-cursor0, libxkbcommon0, libmpv2, libsecret-tools
Recommends: xdg-desktop-portal, libsdl3-0, systemd
Maintainer: Tuxflix <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Tuxflix
Description: Watch and listen to your Plex library
 Tuxflix is a native desktop client for Plex Media Server, laid out like a
 game library. Films and episodes play inside the window with hardware
 decoding, skipping intros and offering the next episode; music plays gapless
 with an equalizer and a compact player that wears classic Winamp skins. It is
 a viewer: it never changes how the server is set up.
CTRL
    dpkg-deb --build --root-owner-group "$deb" "$OUT/Tuxflix$suffix.deb" > /dev/null
    rm -rf "$deb"

    echo "── rpm ($rpmarch)"
    if bash packaging/build-rpm.sh "$root" "$rpmarch" "$VER" "$OUT/Tuxflix${suffix:+-$rpmarch}.rpm"; then
        :
    else
        echo "   skipped — see the message above."
    fi

    rm -rf "$root"
}

for arch in "${ARCHES[@]}"; do
    build_one "$arch"
done

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
echo "── sha256 (for packaging/aur/PKGBUILD):"
for arch in "${ARCHES[@]}"; do
    printf '%s  %s\n' "$(sha256sum "$OUT/Tuxflix-$VER-linux-$arch.tar.gz" | cut -d' ' -f1)" "linux-$arch"
done
