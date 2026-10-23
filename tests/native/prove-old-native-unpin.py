"""Create schema 4 with exact frozen c919 native host; restart new native host.

The pinned ownerless fixture is authored locally. No source fetch or auth is
needed. Existing schema 1/2/3 producer evidence stays in prove-old-native-plans.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time

BASELINE = 'c919199a6861b9aaf9301e31c4b2945e098da799'
spec = importlib.util.spec_from_file_location('local_reimport_proof', Path(__file__).with_name('prove-local-reimport.py'))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)


def kill_at(host, root, gh, env, saved, boundary):
    process = subprocess.Popen([str(host), str(root), str(gh), saved['id'], saved['planFingerprint'], 'apply-unpin', boundary],
                               env=env, cwd=root, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
    ready, exited, output = threading.Event(), threading.Event(), []

    def drain():
        try:
            for line in process.stdout:
                if len(output) < 128:
                    output.append(line[:4096])
                if line.strip() == 'READY ' + boundary:
                    ready.set()
        finally:
            exited.set()

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    try:
        deadline = time.monotonic() + 90
        while not ready.wait(.05) and not exited.is_set() and time.monotonic() < deadline:
            pass
        assert ready.is_set() and process.poll() is None, ('Old native restart checkpoint missing', ''.join(output))
        process.kill()
        assert process.wait(timeout=15) != 0
    finally:
        if process.poll() is None:
            process.kill(); process.wait(timeout=15)
        reader.join(timeout=5)
        process.stdout.close()
        assert not reader.is_alive()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--old-app', required=True, type=Path)
    binding = parser.add_mutually_exclusive_group(required=True)
    binding.add_argument('--old-checkout', type=Path)
    binding.add_argument('--old-source-receipt', type=Path)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host', required=True, type=Path)
    parser.add_argument('--gh', required=True, type=Path)
    parser.add_argument('--kill-boundary', required=True, choices=('retain-moved', 'install-moved'))
    parser.add_argument('--expect-metadata-refusal', metavar='REASON')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    old, app, host, gh = map(helpers.executable, (args.old_app, args.app, args.proof_host, args.gh))
    if args.old_checkout:
        checkout = args.old_checkout.resolve(strict=True)
        assert old.is_relative_to(checkout), 'Frozen binary is not beneath its source checkout'
        observed = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip()
        clean = not subprocess.check_output(['git', '-C', str(checkout), 'status', '--porcelain', '--untracked-files=no'], text=True).strip()
        source_receipt = dict(sourceCommit=observed, trackedClean=clean, binarySha256=helpers.digest(old))
    else:
        source_receipt = json.loads(args.old_source_receipt.read_text())
    assert source_receipt['sourceCommit'] == BASELINE and source_receipt['trackedClean'] is True
    assert source_receipt['binarySha256'] == helpers.digest(old), 'Frozen producer binary differs from checked source receipt'
    with tempfile.TemporaryDirectory(prefix='skillview-old-unpin-proof-') as temporary, socket.socket() as blocked:
        root = Path(temporary).resolve()
        blocked.bind(('127.0.0.1', 0))
        proxy = 'http://127.0.0.1:' + str(blocked.getsockname()[1])
        home, state = root / 'home', root / 'state'
        home.mkdir(); state.mkdir()
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GH_', 'GIT_')) and key not in ('GITHUB_TOKEN', 'GITHUB_AUTH_TOKEN',
                   'GITHUB_ENTERPRISE_TOKEN', 'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR',
                   'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(state),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull,
                   DOTNET_EnableDiagnostics='0', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1',
                   NO_PROXY='', no_proxy='')
        for name in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
            env[name] = proxy
        custom = root / 'custom'
        payload = custom / 'legacy-unpin'
        payload.mkdir(parents=True)
        payload.joinpath('SKILL.md').write_bytes(b'---\r\nname: legacy-unpin\r\ndescription: Local compatibility fixture\r\n'
            b'metadata:\r\n  github-repo: https://github.com/harder/fixture\r\n  github-ref: refs/tags/v1\r\n'
            b'  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n  github-pinned: v1 # remove only this member\r\n'
            b'---\r\nLocal authored bytes\r\n')
        payload.joinpath('extra').write_bytes(b'\x00\xffExact original bytes')
        payload.joinpath('empty-directory').mkdir()
        if os.name != 'nt':
            payload.chmod(0o700)
        helpers.prepare_windows_fixture_owner(payload)
        before, before_objects = helpers.manifest(payload), helpers.objects(payload)
        commands = []

        def run(binary, command, *arguments, expected=0):
            process = subprocess.run([str(binary), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root,
                                     capture_output=True, text=True, timeout=180)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            commands.append(dict(host='old' if binary == old else 'current', command=command, exitCode=process.returncode))
            return json.loads(process.stdout)

        prepared = run(old, 'unpin-plan', '--path', payload, expected=1 if args.expect_metadata_refusal else 0)
        result = dict(oldSource=source_receipt, oldHostSha256=helpers.digest(old), currentHostSha256=helpers.digest(app),
                      proofHostSha256=helpers.digest(host), ghSha256=helpers.digest(gh), schema=4,
                      boundary=args.kill_boundary, httpProxiesBlocked=True, githubCredentialsRemoved=True,
                      fixtureKind='ownerless-local-authored-pinned-tracking', commands=commands)
        if args.expect_metadata_refusal:
            assert prepared.get('succeeded') is False and args.expect_metadata_refusal in prepared.get('error', ''), prepared
            assert helpers.manifest(payload) == before and helpers.objects(payload) == before_objects
            result.update(compatibilityVerified=False, oldNativePublicationRefused=True, reason=args.expect_metadata_refusal,
                          installedNativeObjectsUnchanged=True)
        else:
            saved = prepared['operation']
            plan_path = Path(saved['planPath'])
            frozen = plan_path.read_bytes()
            record = json.loads(frozen)
            assert record['schemaVersion'] == 4 and record['purpose'] == 'unpin-only' and record['pinPolicy']
            assert all(key not in record for key in ('localImport', 'revision', 'localDisposition'))
            candidate, candidate_objects = helpers.manifest(Path(saved['candidatePath'])), helpers.objects(Path(saved['candidatePath']))
            inspected = run(app, 'unpin-inspect', '--id', saved['id'])['operation']
            assert inspected['status'] == 'ready' and inspected['planFingerprint'] == saved['planFingerprint']
            assert 'localImport' not in inspected
            kill_at(host, root, gh, env, saved, args.kill_boundary)
            interrupted = run(app, 'unpin-inspect', '--id', saved['id'])['operation']
            assert interrupted['status'] == ('original-retained' if args.kill_boundary == 'retain-moved' else 'installed')
            recovered = run(app, 'unpin-recover', '--id', saved['id'], '--yes', '--expect', interrupted['reviewFingerprint'])['operation']
            assert recovered['succeeded'] and recovered['status'] == 'installed', recovered
            assert helpers.manifest(payload) == candidate and helpers.objects(payload) == candidate_objects
            fresh = run(app, 'unpin-inspect', '--id', saved['id'])['operation']
            restored = run(app, 'unpin-undo', '--id', saved['id'], '--yes', '--expect', fresh['reviewFingerprint'])['operation']
            assert restored['succeeded'] and restored['status'] == 'restored', restored
            assert helpers.manifest(payload) == before and helpers.objects(payload) == before_objects
            assert plan_path.read_bytes() == frozen and 'localImport' not in restored
            result.update(compatibilityVerified=True, actualOldNativePlanCreated=True, currentNativeProcessKilled=True,
                          forwardRecoveryVerified=True, exactOriginalNativeObjectsAndBytesRestored=True,
                          oldPlanBytesAndFingerprintUnchanged=True, absentLocalImportRemainsOmitted=True,
                          savedPlanSha256=hashlib.sha256(frozen).hexdigest())
        assert not (home / '.agents' / '.skill-lock.json').exists()
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(dict(output=str(args.output), compatibilityVerified=result['compatibilityVerified'])))


if __name__ == '__main__':
    main()
