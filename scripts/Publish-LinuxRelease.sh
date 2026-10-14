#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --native-package PACKAGE.zip --output NEW_FOLDER [--version X.Y.Z] [--enable-updates]" >&2
}

native_package=""
output=""
version=""
enable_updates=false
while (($#)); do
  case "$1" in
    --native-package) native_package="${2:-}"; shift 2 ;;
    --output) output="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --enable-updates) enable_updates=true; shift ;;
    *) usage; exit 2 ;;
  esac
done
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -n "$version" ]] || version="$(tr -d '\r\n' < "$project_root/VERSION")"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ || -z "$output" || -z "$native_package" || ! -f "$native_package" || -e "$output" ]]; then
  usage
  exit 2
fi

output="$(realpath -m "$output")"
native_package="$(realpath "$native_package")"
mkdir -p "$(dirname "$output")"
stage="$(mktemp -d -p "$(dirname "$output")" .briefcase-release-XXXXXX)"
trap 'rm -rf -- "$stage"' EXIT
"$project_root/scripts/Publish-LinuxLocal.sh" --native-package "$native_package" --output "$stage/flat"

python3 - "$stage/flat" "$stage/release" "$version" "$enable_updates" "$output" <<'PY'
import hashlib, json, pathlib, shutil, sys

flat, root = map(pathlib.Path, sys.argv[1:3])
version = sys.argv[3]
enabled = sys.argv[4] == 'true'
final_root = pathlib.Path(sys.argv[5])
app = root / 'App' / 'versions' / version
app.mkdir(parents=True)
launcher_files = ('Briefcase', 'libSkiaSharp.so', 'libHarfBuzzSharp.so')
for name in launcher_files:
    shutil.copy2(flat / name, root / name)
for path in sorted(flat.rglob('*')):
    if not path.is_file():
        continue
    relative = path.relative_to(flat)
    if relative.as_posix() == 'Briefcase' or relative.name.endswith('.dbg') or relative.name in ('Briefcase.desktop', 'BriefcaseIcon.png'):
        continue
    target = app / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(path, target)

def record(path, base):
    return {'path': path.relative_to(base).as_posix(), 'bytes': path.stat().st_size,
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}

app_files = sorted((p for p in app.rglob('*') if p.is_file()), key=lambda p: p.as_posix())
if not (app / 'Briefcase.ServerApp').is_file() or not (app / 'Native' / 'Briefcase.ServerLauncher').is_file():
    raise SystemExit('The application package is missing a Linux executable.')
for name in ('Briefcase.ServerApp', 'Native/Briefcase.ServerLauncher'):
    if not (app / name).stat().st_mode & 0o111:
        raise SystemExit('The application package contains a non-executable Linux program.')
(app / 'manifest.json').write_text(json.dumps({'schemaVersion': 1, 'version': version,
    'files': [record(path, app) for path in app_files]}, separators=(',', ':')) + '\n')
(root / 'App' / 'current.json').write_text(json.dumps({'version': version}) + '\n')
(root / 'Launcher').mkdir()
(root / 'Launcher' / 'manifest.json').write_text(json.dumps({'schemaVersion': 1, 'version': version,
    'files': [record(root / name, root) for name in launcher_files]}, separators=(',', ':')) + '\n')
(root / 'UpdateSource.json').write_text(json.dumps({'schemaVersion': 1, 'enabled': enabled,
    'repository': 'EnoPM/Briefcase'}) + '\n')
shutil.copy2(flat / 'BriefcaseIcon.png', root / 'BriefcaseIcon.png')
desktop = flat.joinpath('Briefcase.desktop').read_text().replace(str(flat), str(final_root))
(root / 'Briefcase.desktop').write_text(desktop)
(root / 'Briefcase.desktop').chmod(0o755)
PY

archive="$(dirname "$output")/Briefcase.ServerApp-linux-x64-$version.zip"
if [[ -e "$archive" ]]; then
  echo "Archive already exists: $archive" >&2
  exit 2
fi
python3 - "$stage/release" "$stage/release.zip" <<'PY'
import pathlib, sys, zipfile
root, archive = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
    for file in sorted(root.rglob('*')):
        if file.is_file():
            output.write(file, file.relative_to(root).as_posix())
PY
mv -- "$stage/release.zip" "$archive"
mv -- "$stage/release" "$output"
echo "Briefcase Linux release folder ready: $output"
echo "Briefcase Linux update archive ready: $archive"
