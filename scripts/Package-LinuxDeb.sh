#!/usr/bin/env bash
set -euo pipefail

if (($# != 2)); then
  echo "Usage: $0 RELEASE_FOLDER OUTPUT.deb" >&2
  exit 2
fi

release=$(realpath -e -- "$1")
output=$(realpath -m -- "$2")
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
version=$(tr -d '\r\n' < "$root/VERSION")
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid VERSION' >&2; exit 2; }
[[ $output == *.deb && ! -e $output && -f $release/Briefcase && -f $release/App/current.json ]] || {
  echo 'Release folder is incomplete or output already exists.' >&2
  exit 2
}
[[ ! -e $release/DEBIAN ]] || { echo 'Unexpected DEBIAN folder in release.' >&2; exit 2; }
if find "$release" -type l -print -quit | grep -q .; then
  echo 'Release folder contains a symbolic link.' >&2
  exit 2
fi

mkdir -p "$(dirname "$output")"
stage=$(mktemp -d -p "$(dirname "$output")" .briefcase-deb-XXXXXX)
trap 'rm -rf -- "$stage"' EXIT
install -d "$stage/DEBIAN" "$stage/opt/briefcase" "$stage/usr/bin" \
  "$stage/usr/share/applications" "$stage/usr/share/pixmaps" \
  "$stage/usr/share/doc/briefcase"
cp -a "$release/." "$stage/opt/briefcase/"
find "$stage/opt/briefcase" -type d -exec chmod 755 {} +
find "$stage/opt/briefcase" -type f -exec chmod 644 {} +
chmod 755 "$stage/opt/briefcase/Briefcase" \
  "$stage/opt/briefcase/App/versions/$version/Briefcase.ServerApp" \
  "$stage/opt/briefcase/App/versions/$version/Native/Briefcase.ServerLauncher"

# A package-manager installation lives under root-owned /opt. Its application
# files are upgraded by a new .deb; extracted ZIP installations can self-update.
python3 - "$stage/opt/briefcase/UpdateSource.json" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
data = json.loads(path.read_text())
data['enabled'] = False
path.write_text(json.dumps(data, separators=(',', ':')) + '\n')
PY

cat > "$stage/usr/bin/briefcase" <<'SH'
#!/bin/sh
exec /opt/briefcase/Briefcase "$@"
SH
chmod 755 "$stage/usr/bin/briefcase"
cat > "$stage/usr/share/applications/briefcase.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Briefcase
Comment=Manage Deceive Inc. dedicated servers
Exec=briefcase
Icon=briefcase
Terminal=false
Categories=Game;Utility;
DESKTOP
install -m 644 "$root/Briefcase.ServerApp/Assets/BriefcaseIcon.png" \
  "$stage/usr/share/pixmaps/briefcase.png"
install -m 644 "$root/LICENSE" "$stage/usr/share/doc/briefcase/copyright"
cat > "$stage/DEBIAN/control" <<CONTROL
Package: briefcase
Version: $version
Section: games
Priority: optional
Architecture: amd64
Maintainer: EnoPM
Homepage: https://github.com/EnoPM/Briefcase
Depends: libc6, libgcc-s1, libstdc++6, libfontconfig1, libcurl4t64, libarchive13t64, ca-certificates
Description: Briefcase dedicated-server manager for Deceive Inc.
 Standalone desktop application and launcher for managing dedicated servers.
CONTROL
chmod 644 "$stage/DEBIAN/control"
dpkg-deb --build --root-owner-group "$stage" "$output"
dpkg-deb --field "$output" Package Version Architecture
echo "Briefcase Debian package ready: $output"
