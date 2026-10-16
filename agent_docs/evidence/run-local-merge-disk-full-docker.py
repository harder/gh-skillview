"""Stage owned tools, then run guarded ENOSPC proofs only in fresh Docker tmpfs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--proof-host-dir', required=True, type=Path)
parser.add_argument('--gh', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--platform', choices=('linux/amd64', 'linux/arm64'))
parser.add_argument('--pull', choices=('missing', 'never'), default='missing')
args = parser.parse_args()
if not (os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')):
    parser.error('A child GH_TOKEN/GITHUB_TOKEN is required; its value is never printed.')
docker = shutil.which('docker')
if docker is None:
    parser.error('Docker is required; no proof has run.')
requested_output = args.output.absolute()


def reject_output_links():
    # Resolve only after checking the requested lexical path. A final or ancestor
    # symlink must never grant the container writable authority over its target.
    for path in (requested_output, *requested_output.parents):
        if path.is_symlink():
            parser.error('Output and its ancestors must not be symlinks.')


def validate_output(expected=None, empty=False):
    reject_output_links()
    information = requested_output.lstat()
    if not stat.S_ISDIR(information.st_mode) or information.st_uid != os.geteuid() or information.st_mode & 0o077:
        parser.error('Output must be a current-user-owned private directory.')
    identity = information.st_dev, information.st_ino
    if expected is not None and identity != expected:
        parser.error('Output directory identity changed before Docker binding.')
    if empty and any(requested_output.iterdir()):
        parser.error('Output must be a new or empty owned directory.')
    return identity


reject_output_links()
if requested_output.exists() and not requested_output.is_dir():
    parser.error('Output must be a directory.')
requested_output.mkdir(mode=0o700, parents=True, exist_ok=True)
output_identity = validate_output(empty=True)
output = requested_output.resolve(strict=True)
scripts = Path(__file__).resolve().parent
app, proof, gh = args.app.resolve(), args.proof_host_dir.resolve(), args.gh.resolve()
assert app.is_file() and (proof / 'SkillView.MaintenanceProof').is_file() and gh.is_file()
container = 'skillview-disk-full-' + uuid.uuid4().hex


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def copy_tools(source, destination):
    assert source.is_dir() and not source.is_symlink()
    assert not any(path.is_symlink() for path in source.rglob('*')), 'Tool directories must not contain links'
    shutil.copytree(source, destination)


with tempfile.TemporaryDirectory(prefix='skillview-disk-full-tools-') as temporary:
    tools = Path(temporary)
    copy_tools(app.parent, tools / 'app')
    copy_tools(proof, tools / 'proof')
    shutil.copy2(gh, tools / 'gh')
    for name in ('prove-local-merge-update.py', 'prove-disk-full-guards.py', 'prove-local-merge-disk-full.sh'):
        shutil.copy2(scripts / name, tools / name)
    (tools / 'entry.sh').write_text('''#!/bin/sh
set -eu
export DOTNET_EnableDiagnostics=0
export DOTNET_CLI_HOME=/tmp/skillview-disk-full-dotnet-home
apt-get update -qq > /proof-results/container-tools.log 2>&1
apt-get install -y -qq python3 git >> /proof-results/container-tools.log 2>&1
sh /proof-tools/prove-local-merge-disk-full.sh "$1" /proof-tools/proof/SkillView.MaintenanceProof /proof-tools/gh /proof-results
''')
    tool_hashes = {str(path.relative_to(tools)): digest(path) for path in sorted(tools.rglob('*')) if path.is_file()}
    (output / 'inputs.json').write_text(json.dumps(dict(sdkImage='mcr.microsoft.com/dotnet/sdk:10.0.402',
        platform=args.platform, hashes=tool_hashes, fixtureTmpfsBytes=64 * 1024 * 1024,
        oversizedNegativeTmpfsBytes=256 * 1024 * 1024, sharedNegativeTmpfsBytes=64 * 1024 * 1024), indent=2) + '\n')
    command = [docker, 'run', '--rm', '--name', container, '--pull=' + args.pull,
               '--cap-add=SYS_ADMIN', '--shm-size=64m',
               '--tmpfs', '/proof-fixtures:rw,size=64m,mode=700',
               '--tmpfs', '/proof-too-large:rw,size=256m,mode=700',
               '--mount', f'type=bind,src={tools},dst=/proof-tools,readonly',
               '--mount', f'type=bind,src={output},dst=/proof-results',
               '--env', 'GH_TOKEN', '--env', 'DOTNET_EnableDiagnostics=0']
    if args.platform:
        command += ['--platform', args.platform]
    command += ['mcr.microsoft.com/dotnet/sdk:10.0.402', 'sh', '/proof-tools/entry.sh', '/proof-tools/app/' + app.name]
    env = os.environ.copy()
    env['GH_TOKEN'] = env.get('GH_TOKEN') or env['GITHUB_TOKEN']
    process = None
    try:
        with (output / 'docker.log').open('w') as log:
            validate_output(output_identity)
            process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            for line in process.stdout:
                log.write(line); log.flush(); print(line, end='', flush=True)
            code = process.wait()
        assert code == 0, f'Isolated disk-full container failed ({code}); inspect owned output logs.'
    finally:
        if process is not None:
            try:
                subprocess.run([docker, 'rm', '-f', container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            finally:
                try:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=15)
                finally:
                    process.stdout.close()
