"""Stage owned tools for guarded ENOSPC or standalone-unpin proofs in Docker tmpfs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import shlex
import stat
import subprocess
import tempfile
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--proof-host-dir', required=True, type=Path)
parser.add_argument('--gh', required=True, type=Path)
parser.add_argument('--extra-gh', action='append', type=Path, default=[], help='Additional checked gh binaries for standalone-unpin contracts.')
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--unpin-only', action='store_true', help='Run standalone-unpin recovery without filling any filesystem.')
parser.add_argument('--legacy-app', type=Path, help='Optional frozen native baseline for unpin schema compatibility.')
parser.add_argument('--legacy-kill-boundary', choices=('retain-moved', 'install-moved'), default='retain-moved')
parser.add_argument('--legacy-checkout', type=Path, help='Exact clean baseline checkout used to build --legacy-app; never mounted.')
parser.add_argument('--platform', choices=('linux/amd64', 'linux/arm64'))
parser.add_argument('--pull', choices=('missing', 'never'), default='missing')
args = parser.parse_args()
if args.extra_gh and not args.unpin_only:
    parser.error('--extra-gh requires --unpin-only.')
if bool(args.legacy_app) != bool(args.legacy_checkout) or args.legacy_app and not args.unpin_only:
    parser.error('Use --legacy-app and --legacy-checkout together with --unpin-only.')
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
container = ('skillview-unpin-' if args.unpin_only else 'skillview-disk-full-') + uuid.uuid4().hex


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
    extra_gh_arguments = ''
    for index, extra in enumerate(args.extra_gh):
        assert extra.resolve().is_file()
        shutil.copy2(extra.resolve(), tools / ('gh-extra-' + str(index)))
        extra_gh_arguments += ' --gh /proof-tools/gh-extra-' + str(index)
    proof_scripts = ('prove-shared-revision-update.py',) if args.unpin_only else ('prove-local-merge-update.py', 'prove-disk-full-guards.py', 'prove-local-merge-disk-full.sh')
    for name in proof_scripts:
        shutil.copy2(scripts / name, tools / name)
    legacy_command = ''
    if args.legacy_app:
        baseline = '0dba59be59776e6e654123aa1ce1e5496074763e'
        checkout, old_app = args.legacy_checkout.resolve(), args.legacy_app.resolve()
        assert old_app.is_relative_to(checkout), 'Baseline binary must be beneath its exact source checkout'
        observed = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip()
        clean = not subprocess.check_output(['git', '-C', str(checkout), 'status', '--porcelain', '--untracked-files=no'], text=True).strip()
        assert observed == baseline and clean, 'Baseline source is not exact and tracked-clean'
        copy_tools(old_app.parent, tools / 'legacy')
        (tools / 'legacy-source.json').write_text(json.dumps(dict(sourceCommit=observed, trackedClean=clean,
            binarySha256=digest(old_app)), indent=2) + '\n')
        shutil.copy2(scripts / 'prove-old-native-plans.py', tools / 'prove-old-native-plans.py')
        legacy_command = 'python3 /proof-tools/prove-old-native-plans.py --old-app /proof-tools/legacy/' + shlex.quote(old_app.name) + ' --old-source-receipt /proof-tools/legacy-source.json --app "$1" --proof-host /proof-tools/proof/SkillView.MaintenanceProof --gh /proof-tools/gh --kill-boundary ' + args.legacy_kill_boundary + ' --output /proof-results/old-native-proof.json\n'
    proof_command = ('export TMPDIR=/proof-fixtures\n'
        'python3 /proof-tools/prove-shared-revision-update.py --unpin-only --app "$1" --proof-host /proof-tools/proof/SkillView.MaintenanceProof --gh /proof-tools/gh' + extra_gh_arguments + ' --output /proof-results/standalone-unpin-proof.json\n' + legacy_command) if args.unpin_only else 'sh /proof-tools/prove-local-merge-disk-full.sh "$1" /proof-tools/proof/SkillView.MaintenanceProof /proof-tools/gh /proof-results\n'
    (tools / 'entry.sh').write_text('''#!/bin/sh
set -eu
export DOTNET_EnableDiagnostics=0
export DOTNET_CLI_HOME=/tmp/skillview-native-proof-dotnet-home
apt-get update -qq > /proof-results/container-tools.log 2>&1
apt-get install -y -qq python3 git >> /proof-results/container-tools.log 2>&1
''' + proof_command)
    tool_hashes = {str(path.relative_to(tools)): digest(path) for path in sorted(tools.rglob('*')) if path.is_file()}
    (output / 'inputs.json').write_text(json.dumps(dict(sdkImage='mcr.microsoft.com/dotnet/sdk:10.0.402',
        platform=args.platform, proofKind='standalone-unpin' if args.unpin_only else 'real-enospc',
        hashes=tool_hashes, fixtureTmpfsBytes=64 * 1024 * 1024,
        oversizedNegativeTmpfsBytes=None if args.unpin_only else 256 * 1024 * 1024,
        sharedNegativeTmpfsBytes=None if args.unpin_only else 64 * 1024 * 1024), indent=2) + '\n')
    command = [docker, 'run', '--rm', '--name', container, '--pull=' + args.pull,
               '--cap-add=SYS_ADMIN', '--shm-size=64m',
               '--tmpfs', '/proof-fixtures:rw,size=64m,mode=700',
               '--mount', f'type=bind,src={tools},dst=/proof-tools,readonly',
               '--mount', f'type=bind,src={output},dst=/proof-results',
               '--env', 'GH_TOKEN', '--env', 'DOTNET_EnableDiagnostics=0']
    if not args.unpin_only:
        command += ['--tmpfs', '/proof-too-large:rw,size=256m,mode=700']
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
        assert code == 0, f'Isolated native proof container failed ({code}); inspect owned output logs.'
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
