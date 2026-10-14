#!/usr/bin/env python3
"""Open a packaged Linux release on a private X display without touching a desktop session."""

import os
import json
import pathlib
import signal
import subprocess
import sys
import tempfile
import time
import hashlib
import shutil

if len(sys.argv) not in (2, 3) or (len(sys.argv) == 3 and sys.argv[2] != '--test-launcher-replacement'):
    raise SystemExit('Usage: Test-LinuxRelease.py /path/to/release-folder [--test-launcher-replacement]')
original_root = pathlib.Path(sys.argv[1]).resolve()
replace_launcher = len(sys.argv) == 3
working_copy = tempfile.TemporaryDirectory(prefix='briefcase-release-copy-') if replace_launcher else None
root = pathlib.Path(working_copy.name) / 'install' if working_copy else original_root
if working_copy:
    shutil.copytree(original_root, root)
version = json.loads((root / 'App' / 'current.json').read_text())['version']
app = root / 'App' / 'versions' / version / 'Briefcase.ServerApp'
launcher = root / 'Briefcase'
if not app.is_file() or not launcher.is_file():
    raise SystemExit('The packaged application or launcher is missing.')

def running_app_pid():
    for entry in pathlib.Path('/proc').iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if pathlib.Path(os.readlink(entry / 'exe')) == app:
                return int(entry.name)
        except (OSError, PermissionError):
            pass
    return None

if running_app_pid() is not None:
    raise SystemExit('The release application is already running; refusing to interfere.')

if replace_launcher:
    staged = root / 'App' / 'launcher-updates' / version
    staged.mkdir(parents=True)
    launcher_files = ('Briefcase', 'libSkiaSharp.so', 'libHarfBuzzSharp.so')
    for name in launcher_files:
        shutil.copy2(root / name, staged / name)
    with (staged / 'Briefcase').open('ab') as output:
        output.write(b'\nBriefcase Linux launcher replacement test\n')
    new_hash = hashlib.sha256((staged / 'Briefcase').read_bytes()).hexdigest()
    records = [{'path': name, 'bytes': (staged / name).stat().st_size,
                'sha256': hashlib.sha256((staged / name).read_bytes()).hexdigest()}
               for name in launcher_files]
    (staged / 'manifest.json').write_text(json.dumps({'schemaVersion': 1, 'version': version,
                                                       'files': records}) + '\n')
    probe = subprocess.run(['dotnet', 'run', '--no-build', '-c', 'Release', '--project',
                            str(pathlib.Path(__file__).resolve().parents[1] /
                                'Briefcase.ServerApp.LinuxTests/Briefcase.ServerApp.LinuxTests.csproj'),
                            '--', '--launcher-pending', str(root)], capture_output=True, text=True)
    if probe.returncode or probe.stdout.strip() != 'PENDING':
        raise RuntimeError('The staged launcher was not accepted before startup: ' +
                           probe.stdout + probe.stderr)

with tempfile.TemporaryDirectory(prefix='briefcase-linux-smoke-') as data:
    display = ':104'
    env = os.environ.copy()
    env['DISPLAY'] = display
    env['XDG_DATA_HOME'] = data
    env['AVALONIA_TELEMETRY_OPTOUT'] = '1'
    xvfb = subprocess.Popen(['Xvfb', display, '-screen', '0', '1280x800x24'],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(1)
        if xvfb.poll() is not None:
            raise RuntimeError('The isolated X display did not start.')
        log_path = pathlib.Path(data) / 'launcher.log'
        log = log_path.open('wb')
        launched = subprocess.Popen([str(launcher)], cwd=root, env=env,
                                    stdout=log, stderr=subprocess.STDOUT)
        try:
            try:
                result = launched.wait(timeout=20)
            except subprocess.TimeoutExpired as error:
                log.flush()
                raise RuntimeError('Launcher did not finish in 20 seconds; app PID: ' +
                    str(running_app_pid()) + '; log: ' + log_path.read_text(errors='replace')) from error
            if result != 0:
                raise RuntimeError(f'The launcher exited with status {result}.')
            pid = running_app_pid()
            if pid is None:
                raise RuntimeError('The launcher exited without a running application.')
            if replace_launcher:
                for _ in range(100):
                    if not staged.exists():
                        break
                    time.sleep(0.1)
                if staged.exists() or hashlib.sha256(launcher.read_bytes()).hexdigest() != new_hash:
                    pid = running_app_pid()
                    arguments = pathlib.Path(f'/proc/{pid}/cmdline').read_bytes().replace(b'\0', b' ').decode(errors='replace') if pid else 'app stopped'
                    raise RuntimeError('The application did not install the staged launcher. ' +
                        f'Staged folder remains: {staged.exists()}; app PID: {pid}; args: {arguments}; ' +
                        'launcher log: ' + log_path.read_text(errors='replace'))
            print('Linux release splash-to-app launch passed.')
        finally:
            log.close()
            if launched.poll() is None:
                launched.terminate()
                launched.wait(timeout=5)
            pid = running_app_pid()
            if pid is not None:
                os.kill(pid, signal.SIGTERM)
                for _ in range(50):
                    if running_app_pid() is None:
                        break
                    time.sleep(0.1)
    finally:
        xvfb.terminate()
        xvfb.wait(timeout=5)
if working_copy:
    working_copy.cleanup()
