"""Run source-switch and frozen schema5/6 recovery on a private container root.

The fixtures deliberately share the container's root mount: source placement
uses a strict NO_XDEV walk from /. Existing tmpfs/ENOSPC proof runners retain
their separate contracts. No host source checkout or authentication is mounted.
"""
import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import stat
import subprocess
import tempfile
import uuid


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host-dir', required=True, type=Path)
    parser.add_argument('--gh', required=True, type=Path)
    parser.add_argument('--extra-gh', action='append', type=Path, default=[])
    parser.add_argument('--legacy-app', required=True, type=Path)
    parser.add_argument('--legacy-checkout', required=True, type=Path)
    parser.add_argument('--source-switch-legacy-app', required=True, type=Path)
    parser.add_argument('--source-switch-legacy-checkout', required=True, type=Path)
    parser.add_argument('--legacy-kill-boundary', choices=('retain-moved', 'install-moved'), required=True)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--platform', choices=('linux/amd64', 'linux/arm64'), required=True)
    parser.add_argument('--pull', choices=('missing', 'never'), default='missing')
    args = parser.parse_args()
    scripts = Path(__file__).resolve().parent
    # Inspect the literal only. Importing a proof must never allocate fixtures
    # or load helpers before its final merged native producer is frozen.
    module = ast.parse((scripts / 'prove-old-native-reimport.py').read_text())
    values = [ast.literal_eval(node.value) for node in module.body if isinstance(node, ast.Assign)
              and any(isinstance(target, ast.Name) and target.id == 'BASELINE' for target in node.targets)]
    if len(values) != 1 or not isinstance(values[0], str) or len(values[0]) != 40 or any(
            value not in '0123456789abcdef' for value in values[0]):
        parser.error('Frozen merged schema5 baseline is unavailable; no tools, output directory or container were created.')
    baseline = values[0]
    schema6_baseline = None
    if args.source_switch_legacy_app:
        values6 = [ast.literal_eval(node.value) for node in module.body if isinstance(node, ast.Assign)
                   and any(isinstance(target, ast.Name) and target.id == 'SOURCE_SWITCH_BASELINE' for target in node.targets)]
        if len(values6) != 1 or not isinstance(values6[0], str) or len(values6[0]) != 40 or any(
                value not in '0123456789abcdef' for value in values6[0]):
            parser.error('Frozen merged schema6 baseline is unavailable; no tools, output directory or container were created.')
        schema6_baseline = values6[0]
        if schema6_baseline == baseline:
            parser.error('Schema5 and schema6 require distinct actual native producers; no proof has run.')
    docker = shutil.which('docker')
    if docker is None:
        parser.error('Docker is required; no proof has run.')
    requested = args.output.absolute()

    def validate_output(expected=None, empty=False):
        for path in (requested, *requested.parents):
            if path.is_symlink():
                parser.error('Output and its ancestors must not be symlinks.')
        if not requested.exists():
            if expected is not None:
                parser.error('Output directory disappeared before Docker binding.')
            requested.mkdir(mode=0o700, parents=True)
        information = requested.lstat()
        if not stat.S_ISDIR(information.st_mode) or information.st_uid != os.geteuid() or information.st_mode & 0o077:
            parser.error('Output must be a current-user-owned private directory.')
        identity = information.st_dev, information.st_ino
        if expected is not None and identity != expected:
            parser.error('Output directory identity changed before Docker binding.')
        if empty and any(requested.iterdir()):
            parser.error('Output must be a new or empty owned directory.')
        return identity

    output_identity = validate_output(empty=True)
    output = requested.resolve(strict=True)
    app, proof, gh = args.app.resolve(strict=True), args.proof_host_dir.resolve(strict=True), args.gh.resolve(strict=True)
    checkout, old_app = args.legacy_checkout.resolve(strict=True), args.legacy_app.resolve(strict=True)
    assert app.is_file() and gh.is_file() and (proof / 'SkillView.MaintenanceProof').is_file()
    assert old_app.is_file() and old_app.is_relative_to(checkout), 'Baseline binary must be beneath its exact checkout'
    git_env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
    git_env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0', GIT_OPTIONAL_LOCKS='0')
    git_command = ['git', '--no-optional-locks', '-c', 'core.fsmonitor=false', '-C', str(checkout)]
    observed = subprocess.check_output([*git_command, 'rev-parse', 'HEAD'], env=git_env, text=True, timeout=30).strip()
    clean = not subprocess.check_output([*git_command, 'status', '--porcelain', '--untracked-files=no', '--ignore-submodules=all'],
                                        env=git_env, text=True, timeout=30).strip()
    assert observed == baseline and clean, 'Baseline source is not exact and tracked-clean'
    old6_app = None
    if schema6_baseline:
        checkout6 = args.source_switch_legacy_checkout.resolve(strict=True)
        old6_app = args.source_switch_legacy_app.resolve(strict=True)
        assert old6_app.is_file() and old6_app.is_relative_to(checkout6), 'Schema6 binary must be beneath its exact checkout'
        git6 = ['git', '--no-optional-locks', '-c', 'core.fsmonitor=false', '-C', str(checkout6)]
        observed6 = subprocess.check_output([*git6, 'rev-parse', 'HEAD'], env=git_env, text=True, timeout=30).strip()
        clean6 = not subprocess.check_output([*git6, 'status', '--porcelain', '--untracked-files=no', '--ignore-submodules=all'],
                                            env=git_env, text=True, timeout=30).strip()
        assert observed6 == schema6_baseline and clean6, 'Schema6 baseline source is not exact and tracked-clean'

    def copy_tools(source, destination):
        assert source.is_dir() and not source.is_symlink()
        assert not any(path.is_symlink() for path in source.rglob('*')), 'Tool directories must not contain links'
        shutil.copytree(source, destination)

    container = 'skillview-source-switch-' + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix='skillview-source-switch-tools-') as temporary:
        tools = Path(temporary)
        copy_tools(app.parent, tools / 'app')
        copy_tools(proof, tools / 'proof')
        copy_tools(old_app.parent, tools / 'legacy')
        if old6_app:
            copy_tools(old6_app.parent, tools / 'legacy6')
        shutil.copy2(gh, tools / 'gh')
        gh_arguments = ' --gh /proof-tools/gh'
        for index, extra in enumerate(args.extra_gh):
            assert extra.resolve(strict=True).is_file()
            shutil.copy2(extra.resolve(strict=True), tools / ('gh-extra-' + str(index)))
            gh_arguments += ' --gh /proof-tools/gh-extra-' + str(index)
        for name in ('prove-local-source-switch.py', 'prove-local-reimport.py', 'prove-old-native-reimport.py', 'source_review_proof.py'):
            shutil.copy2(scripts / name, tools / name)
        (tools / 'legacy-source.json').write_text(json.dumps(dict(sourceCommit=observed,
            trackedClean=clean, binarySha256=digest(old_app)), indent=2) + '\n')
        if old6_app:
            (tools / 'legacy6-source.json').write_text(json.dumps(dict(sourceCommit=observed6,
                trackedClean=clean6, binarySha256=digest(old6_app)), indent=2) + '\n')
        schema6_command = ''
        if old6_app:
            schema6_command = ('python3 /proof-tools/prove-old-native-reimport.py --legacy-schema 6 --old-app /proof-tools/legacy6/'
                + shlex.quote(old6_app.name) + ' --old-source-receipt /proof-tools/legacy6-source.json --app "$1"'
                + ' --proof-host /proof-tools/proof/SkillView.MaintenanceProof --gh /proof-tools/gh --kill-boundary '
                + args.legacy_kill_boundary + ' --output /proof-results/old-native-source-switch-proof.json\n')
        (tools / 'entry.sh').write_text('''#!/bin/sh
set -eu
export DOTNET_EnableDiagnostics=0
export DOTNET_CLI_HOME=/tmp/skillview-native-proof-dotnet-home
apt-get update -qq > /proof-results/container-tools.log 2>&1
apt-get install -y -qq python3 git >> /proof-results/container-tools.log 2>&1
mkdir -m 700 /proof-fixtures
export TMPDIR=/proof-fixtures
python3 - <<'PY'
import os
assert os.stat('/').st_dev == os.stat('/proof-fixtures').st_dev
assert not os.path.ismount('/proof-fixtures'), 'Fixtures must remain on the private container root mount'
PY
python3 /proof-tools/prove-local-source-switch.py --app "$1" --proof-host /proof-tools/proof/SkillView.MaintenanceProof'''
            + gh_arguments + ' --output /proof-results/local-source-switch-proof.json\n'
            + 'python3 /proof-tools/prove-old-native-reimport.py --old-app /proof-tools/legacy/' + shlex.quote(old_app.name)
            + ' --old-source-receipt /proof-tools/legacy-source.json --app "$1" --proof-host /proof-tools/proof/SkillView.MaintenanceProof'
            + ' --gh /proof-tools/gh --kill-boundary ' + args.legacy_kill_boundary
            + ' --output /proof-results/old-native-reimport-proof.json\n' + schema6_command)
        tool_hashes = {str(path.relative_to(tools)): digest(path) for path in sorted(tools.rglob('*')) if path.is_file()}
        (output / 'inputs.json').write_text(json.dumps(dict(schemaVersion=1, proofKind='local-source-switch',
            sdkImage='mcr.microsoft.com/dotnet/sdk:10.0.402', platform=args.platform,
            hashes=tool_hashes, frozenSchema5Source=baseline, frozenSchema6Source=schema6_baseline,
            fixturePlacement='private-container-root-mount',
            fixtureTmpfsBytes=None, privilegedMetadataRequired=True, authenticationPassed=False,
            qualification='Staged input observation only; success requires both native proof outputs and Docker exit zero.'), indent=2) + '\n')
        command = [docker, 'run', '--rm', '--name', container, '--pull=' + args.pull,
                   '--platform', args.platform, '--cap-add=SYS_ADMIN',
                   '--mount', f'type=bind,src={tools},dst=/proof-tools,readonly',
                   '--mount', f'type=bind,src={output},dst=/proof-results',
                   '--env', 'DOTNET_EnableDiagnostics=0',
                   'mcr.microsoft.com/dotnet/sdk:10.0.402', 'sh', '/proof-tools/entry.sh', '/proof-tools/app/' + app.name]
        process = None
        try:
            with (output / 'docker.log').open('w') as log:
                validate_output(output_identity)
                process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
                code = process.wait()
            assert code == 0, f'Isolated native proof container failed ({code}); inspect owned output logs.'
            for name in ('local-source-switch-proof.json', 'old-native-reimport-proof.json'):
                result = json.loads((output / name).read_text())
                assert result['proofCompleted'] is True, (name, result.get('status'))
            assert json.loads((output / 'old-native-reimport-proof.json').read_text())['compatibilityVerified'] is True
            if old6_app:
                legacy6 = json.loads((output / 'old-native-source-switch-proof.json').read_text())
                assert legacy6['proofCompleted'] is True and legacy6['compatibilityVerified'] is True
                assert legacy6['savedSchemaVersion'] == 6 and legacy6['absentSourceReviewRemainsOmitted'] is True
                assert legacy6['mode'] == 'actual-old-native-schema6'
                assert legacy6['oldSource']['sourceCommit'] == schema6_baseline
                assert legacy6['bothExternalSourcesRemovedBeforeRecovery'] is True
        finally:
            if process is not None:
                try:
                    subprocess.run([docker, 'rm', '-f', container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=15)


if __name__ == '__main__':
    main()
