#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --native-package /path/to/BriefcaseNative-Server-linux-x64-VERSION.zip --output /path/to/new-folder" >&2
}

native_package=""
output=""
while (($#)); do
  case "$1" in
    --native-package) native_package="${2:-}"; shift 2 ;;
    --output) output="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done
if [[ -z "$native_package" || -z "$output" || ! -f "$native_package" || -e "$output" ]]; then
  usage
  echo "The native archive must exist and the output folder must not exist." >&2
  exit 2
fi

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native_package="$(realpath "$native_package")"
output="$(realpath -m "$output")"
mkdir -p "$(dirname "$output")"
stage="$(mktemp -d -p "$(dirname "$output")" .briefcase-linux-XXXXXX)"
trap 'rm -rf -- "$stage"' EXIT
mkdir -p "$stage/native" "$stage/app" "$stage/launcher"

python3 - "$native_package" "$stage/native" <<'PY'
import json, pathlib, re, shutil, stat, sys, zipfile
archive_path, destination = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
with zipfile.ZipFile(archive_path) as archive:
    names = archive.namelist()
    if len(names) != len(set(names)) or names.count('Package.json') != 1 or names.count('Briefcase.ServerLauncher') != 1:
        raise SystemExit('The native archive inventory is invalid.')
    for item in archive.infolist():
        if (item.external_attr >> 16) & 0o170000 == stat.S_IFLNK:
            raise SystemExit('The native archive contains a link.')
    metadata = json.loads(archive.read('Package.json'))
    version = metadata.get('frameworkVersion')
    if metadata.get('platform') != 'linux-x64' or not isinstance(version, str) or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise SystemExit('The native archive is not a Linux server package.')
    if archive_path.name != f'BriefcaseNative-Server-linux-x64-{version}.zip':
        raise SystemExit('The native archive filename does not match its version.')
    (destination / 'Briefcase.ServerLauncher').write_bytes(archive.read('Briefcase.ServerLauncher'))
    (destination / 'Briefcase.ServerLauncher').chmod(0o755)
    shutil.copyfile(archive_path, destination / archive_path.name)
PY

dotnet publish "$project_root/Briefcase.ServerApp/Briefcase.ServerApp.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:BriefcaseLinuxNativeDirectory="$stage/native" \
  -o "$stage/app"
dotnet publish "$project_root/Briefcase.ServerApp.Launcher/Briefcase.ServerApp.Launcher.csproj" \
  -c Release -r linux-x64 --self-contained true -p:PublishAot=true \
  -o "$stage/launcher"
for library in libSkiaSharp.so libHarfBuzzSharp.so; do
  if [[ ! -f "$stage/app/$library" || ! -f "$stage/launcher/$library" ]] ||
     ! cmp -s "$stage/app/$library" "$stage/launcher/$library"; then
    echo "The app and launcher need different copies of $library." >&2
    exit 1
  fi
done
cp -a "$stage/launcher/." "$stage/app/"

test -x "$stage/app/Briefcase.ServerApp"
test -x "$stage/app/Briefcase"
test -x "$stage/app/Native/Briefcase.ServerLauncher"
test -f "$stage/app/Native/$(basename "$native_package")"
cp "$project_root/Briefcase.ServerApp/Assets/BriefcaseIcon.png" "$stage/app/BriefcaseIcon.png"
cat > "$stage/app/Briefcase.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Briefcase
Comment=Manage Deceive Inc. dedicated servers
Exec=$output/Briefcase
Icon=$output/BriefcaseIcon.png
Terminal=false
Categories=Game;Utility;
EOF
chmod 755 "$stage/app/Briefcase.desktop"
mv -- "$stage/app" "$output"
echo "Briefcase Linux AOT folder ready: $output"
