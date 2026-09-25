#!/usr/bin/env bash
# Builds the .rpm from the tree build-release.sh staged. Called by build-release.sh:
#   packaging/build-rpm.sh <staged root> <rpm arch> <version> <output file>
# rpmbuild runs here when installed, otherwise inside a Fedora container borrowed through podman.
set -euo pipefail
cd "$(dirname "$0")/.."

ROOT="$(realpath "${1:?staged root}")"
ARCH="${2:?rpm architecture}"
VER="${3:?version}"
DEST="$(realpath -m "${4:?output path}")"
IMAGE="${IMAGE:-fedora:42}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The self-contained bundle brings its own .NET libraries: rpm must neither claim them as provided
# to the rest of the system nor go looking for them as requirements.
BUNDLED=$(find "$ROOT/usr/lib/tuxflix" -name '*.so*' -printf '%f\n' | sed 's/\.so.*$//' | sort -u | paste -sd'|' -)

cat > "$WORK/tuxflix.spec" <<SPEC
%global debug_package %{nil}
%global __brp_strip %{nil}
%global __brp_strip_static_archive %{nil}
%global __brp_strip_comment_note %{nil}
%global __brp_mangle_shebangs %{nil}
%global __brp_check_rpaths %{nil}
%global __provides_exclude ^($BUNDLED)\\\\.so.*\$
%global __requires_exclude ^($BUNDLED)\\\\.so.*\$

Name:           tuxflix
Version:        $VER
Release:        1%{?dist}
Summary:        Watch and listen to your Plex library
License:        GPL-3.0-or-later
URL:            https://github.com/codingncaffeine/Tuxflix
Requires:       libicu
Requires:       libz.so.1()(64bit)
Requires:       libfreetype.so.6()(64bit)
Requires:       libfontconfig.so.1()(64bit)
Requires:       libX11.so.6()(64bit)
Requires:       libXext.so.6()(64bit)
Requires:       libXi.so.6()(64bit)
Requires:       libXrandr.so.2()(64bit)
Requires:       libXcursor.so.1()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)
Requires:       libEGL.so.1()(64bit)
Requires:       libGL.so.1()(64bit)
Requires:       libmpv.so.2()(64bit)
Requires:       /usr/bin/secret-tool
Recommends:     libwayland-client.so.0()(64bit)
Recommends:     libwayland-egl.so.1()(64bit)
Recommends:     libwayland-cursor.so.0()(64bit)
Recommends:     libxkbcommon.so.0()(64bit)
Recommends:     xdg-desktop-portal
Recommends:     SDL3

%description
Tuxflix is a native desktop client for Plex Media Server, laid out like a
game library. Films and episodes play inside the window with hardware
decoding, skipping intros and offering the next episode; music plays gapless
with an equalizer and a compact player that wears classic Winamp skins. It is
a viewer: it never changes how the server is set up.

%install
cp -a %{_sourcedir}/. %{buildroot}/

%files
/usr/lib/tuxflix
/usr/bin/tuxflix
/usr/share/applications/io.github.codingncaffeine.Tuxflix.desktop
/usr/share/metainfo/io.github.codingncaffeine.Tuxflix.metainfo.xml
/usr/share/icons/hicolor/*/apps/io.github.codingncaffeine.Tuxflix.png
%dir /usr/share/doc/tuxflix
%license /usr/share/doc/tuxflix/copyright
/usr/share/doc/tuxflix/NOTICES.txt

%changelog
* $(date -u '+%a %b %d %Y') Tuxflix <codingncaffeine@users.noreply.github.com> - $VER-1
- See the release notes at %{url}/releases
SPEC

build_here() {
    rpmbuild -bb --quiet --define "_topdir $WORK/rpmbuild" --define "_sourcedir $ROOT" --target "$ARCH" "$WORK/tuxflix.spec"
    cp "$WORK/rpmbuild/RPMS/$ARCH"/*.rpm "$DEST"
}

build_in_container() {
    local platform=""
    case "$(uname -m)" in
        x86_64)  platform=linux/amd64 ;;
        aarch64) platform=linux/arm64 ;;
    esac
    podman run --rm ${platform:+--platform "$platform"} \
        -v "$ROOT:/src:ro" -v "$WORK:/work:ro" -v "$(dirname "$DEST"):/out" \
        "$IMAGE" bash -c '
set -eu
dnf install -y -q rpm-build > /dev/null 2>&1
rpmbuild -bb --quiet --define "_topdir /tmp/rpmbuild" --define "_sourcedir /src" --target '"$ARCH"' /work/tuxflix.spec
cp /tmp/rpmbuild/RPMS/'"$ARCH"'/*.rpm /out/'"$(basename "$DEST")"'
' 2>&1 | grep -v -E 'overlay|graph driver|vfs' || true
    [ -f "$DEST" ]
}

if command -v rpmbuild > /dev/null 2>&1; then
    build_here
elif command -v podman > /dev/null 2>&1; then
    echo "   no rpmbuild here — borrowing one from $IMAGE"
    build_in_container
else
    echo "   no rpmbuild and no podman: install rpm-build, or podman to borrow one." >&2
    exit 1
fi
