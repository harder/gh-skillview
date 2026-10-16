"""Actual-gh local merge CLI proof. All writes use disposable home/state/payloads."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--resolved', action='store_true', help='Use a captured custom SKILL.md resolution and delete its input before apply.')
parser.add_argument('--expect-metadata-refusal', metavar='REASON', help='Require this exact reason in every metadata refusal, and prove publication is refused without live changes.')
parser.add_argument('--proof-host', type=Path, help='Disposable process-death host; required with --kill-boundary.')
parser.add_argument('--kill-boundary', choices=('retain-moved', 'install-moved', 'displace-moved', 'restore-moved'))
args = parser.parse_args()
if bool(args.proof_host) != bool(args.kill_boundary) or args.kill_boundary and args.expect_metadata_refusal:
    parser.error('Use --proof-host and --kill-boundary together, without --expect-metadata-refusal.')
app = args.app.resolve()
proof_host = args.proof_host.resolve() if args.proof_host else None
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(args.gh[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()
old_commit = '33789149b9def6b66ddf247438a79fac94e22074'
results = []


def manifest(directory):
    rows = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        native = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        content = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        attrs = sorted((name, hashlib.sha256(os.getxattr(path, name, follow_symlinks=False)).hexdigest())
                       for name in os.listxattr(path, follow_symlinks=False)) if hasattr(os, 'listxattr') else []
        rows.append((str(path.relative_to(directory)), kind, stat.S_IMODE(native.st_mode) if os.name != 'nt' else None,
                     hashlib.sha256(content).hexdigest(), attrs))
    return rows


def prepare_windows_fixture_owner(path):
    """Establish the supported owner only on disposable fixture objects, before review."""
    if os.name != 'nt':
        return False
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    security = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    security.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    security.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    security.SetSecurityInfo.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.DWORD, *([ctypes.c_void_p] * 4)]
    security.SetSecurityInfo.restype = wintypes.DWORD
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    token = wintypes.HANDLE()
    assert security.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)), ctypes.get_last_error()
    try:
        length = wintypes.DWORD()
        security.GetTokenInformation(token, 1, None, 0, ctypes.byref(length))
        assert 0 < length.value <= 65536
        information = ctypes.create_string_buffer(length.value)
        assert security.GetTokenInformation(token, 1, information, length.value, ctypes.byref(length)), ctypes.get_last_error()
        sid = ctypes.cast(information, ctypes.POINTER(ctypes.c_void_p)).contents.value
        targets = [path, *sorted(path.rglob('*'))] if path.is_dir() else [path]
        for target in targets:
            assert not target.lstat().st_file_attributes & 0x400, 'Unexpected reparse point in disposable ownership fixture'
            handle = kernel.CreateFileW(str(target), 0x80000, 7, None, 3, 0x02200000, None)
            assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
            try:
                error = security.SetSecurityInfo(handle, 1, 1, sid, None, None, None)
                assert error == 0, ('Set disposable fixture owner', error)
            finally:
                kernel.CloseHandle(handle)
    finally:
        kernel.CloseHandle(token)
    return True


for gh_path in args.gh:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-local-merge-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        if proof_host:
            root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = {key: value for key, value in os.environ.items()
               if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR')
               and not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        skills = root / 'custom'
        installed = subprocess.run([str(gh), 'skill', 'install', '--dir', str(skills), '--pin=' + old_commit,
                                    '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root, capture_output=True, text=True, timeout=120)
        assert installed.returncode == 0, installed.stderr
        payload = skills / 'gh-skill'
        payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nReviewed local instructions\n')
        payload.joinpath('keep-local-extra').write_bytes(b'\x00\xffKeep exact local bytes')
        payload.joinpath('keep-empty').mkdir()
        if os.name != 'nt':
            payload.chmod(0o700)
        windows_owner_prepared = prepare_windows_fixture_owner(payload)
        before = manifest(payload)
        original_id = payload.stat().st_ino
        registry = home / '.agents' / '.skill-lock.json'
        original_registry = registry.read_bytes()
        commands = []

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(skills), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (command, process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            commands.append(dict(command=command, exitCode=process.returncode))
            return result

        def kill_at(saved, action, fingerprint):
            process = subprocess.Popen([str(proof_host), str(root), str(gh), saved['id'], fingerprint,
                                        action, args.kill_boundary], env=env, cwd=root,
                                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, text=True)
            ready, exited = threading.Event(), threading.Event()
            output = []

            def consume():
                try:
                    for line in process.stdout:
                        if len(output) < 128:
                            output.append(line[:4096])
                        if line.strip() == 'READY ' + args.kill_boundary:
                            ready.set()
                finally:
                    exited.set()

            reader = threading.Thread(target=consume, daemon=True)
            reader.start()
            try:
                deadline = time.monotonic() + 60
                while not ready.wait(0.05) and not exited.is_set() and time.monotonic() < deadline:
                    pass
                assert ready.is_set() and process.poll() is None, ('Process-death checkpoint missing', ''.join(output))
                process.kill()
                assert process.wait(timeout=15) != 0
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=15)
                reader.join(timeout=5)
                assert not reader.is_alive(), 'Process-death output drain did not finish'
                process.stdout.close()
            assert registry.read_bytes() == original_registry

        source_plan = run('update-plan', '--path', payload, '--unpin')['operation']
        source_payload_fingerprint = json.loads(Path(source_plan['planPath']).read_text())['candidate']['payload']
        preserved = run('update-preserve', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert preserved['status'] == 'preserved' and not preserved['mutationObserved'], preserved
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        source_path = Path(source_plan['candidatePath'])
        source_bytes = manifest(source_path)
        source_id = source_path.stat().st_ino
        review = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert any(row['path'] == 'keep-local-extra' and row['selection'] == 'installed' for row in review['review']['paths']), review
        refused = run('update-apply', '--id', source_plan['id'], '--yes', '--disposition', 'retain-original',
                      '--expect', source_plan['planFingerprint'], ok=False)
        assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], (commands[-1], refused.get('error'))
        assert 'earlier replacement approval is closed' in refused['error'].lower(), refused
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        if args.resolved:
            resolution_file = root / 'reviewed-resolution.txt'
            resolution_file.write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nExplicitly reviewed resolution\n')
            if os.name != 'nt':
                resolution_file.chmod(0o600)
            prepare_windows_fixture_owner(resolution_file)
            chosen = run('update-merge-choose', '--id', source_plan['id'], '--expect', review['reviewFingerprint'], '--resolve', 'SKILL.md', resolution_file)
            resolved_row = next(row for row in chosen['review']['paths'] if row['path'] == 'SKILL.md')
            assert resolved_row['selection'] == 'resolved' and resolved_row['resolution']['kind'] == 'file'
            resolution_file.write_text('Changed after capture\n')
            resolution_file.unlink()
            reused = run('update-merge-choose', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'], '--choose-resolved', 'SKILL.md')
            assert next(row for row in reused['review']['paths'] if row['path'] == 'SKILL.md')['resolution'] == resolved_row['resolution']
            chosen = reused
        else:
            chosen = run('update-merge-choose', '--id', source_plan['id'], '--expect', review['reviewFingerprint'], '--choose-installed', 'SKILL.md')
        if args.expect_metadata_refusal:
            assert not chosen['canMaterialize'] and chosen['metadataRefusals'], chosen
            assert all(args.expect_metadata_refusal in issue for issue in chosen['metadataRefusals']), chosen
            assert not chosen['review']['topologyConflicts'] and all(row['selection'] for row in chosen['review']['paths']), chosen
            refused = run('update-merge-plan', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'], ok=False)
            assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], refused
            assert 'native metadata cannot be preserved' in refused['error'].lower(), refused
            assert args.expect_metadata_refusal in refused['error'], refused
            assert manifest(payload) == before and payload.stat().st_ino == original_id
            assert registry.read_bytes() == original_registry
            assert source_path.stat().st_ino == source_id and manifest(source_path) == source_bytes
            assert not Path(source_plan['retainedPath']).exists() and not Path(source_plan['displacedPath']).exists()
            assert not list(Path(source_plan['planPath']).parent.glob('merged-*'))
            results.append(dict(host=str(app), gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                                sourceRevision=source_plan['revision']['source'], commands=commands,
                                proofKind='explicit-unsupported-metadata', metadataRefusals=chosen['metadataRefusals'],
                                windowsSupportedFixtureOwnerPrepared=windows_owner_prepared,
                                preserveVerified=True, priorApprovalRefused=True, nativePublicationRefused=True,
                                originalNativePayloadUnchanged=True, registryBytesUnchanged=True, sourceCandidateUnchanged=True,
                                **(dict(capturedResolutionSurvivesInputDeletion=True) if args.resolved else {})))
            continue
        assert chosen['canMaterialize'], chosen
        repeat = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert repeat['reviewFingerprint'] == chosen['reviewFingerprint']
        merged = run('update-merge-plan', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'])
        assert merged['localDisposition']['sourcePlanFingerprint'] == source_plan['planFingerprint']
        assert merged['localDisposition']['sourceCandidate']['payload'] == source_payload_fingerprint
        assert source_plan['revision']['source']['tree'] != source_plan['subject']['metadata']['githubTreeSha']
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        merged_bytes = manifest(Path(merged['candidatePath']))
        merged_id = Path(merged['candidatePath']).stat().st_ino
        refused = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'retain-original', '--expect', merged['planFingerprint'], ok=False)
        assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], (commands[-1], refused.get('error'))
        assert 'exact saved disposition: merge-reviewed' in refused['error'].lower(), refused
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        if args.kill_boundary in ('retain-moved', 'install-moved'):
            kill_at(merged, 'apply-merged', merged['planFingerprint'])
            inspection = run('update-inspect', '--id', merged['id'])['operation']
            applied = run('update-recover', '--id', merged['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        else:
            applied = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'merge-reviewed', '--expect', merged['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['status'] == 'installed', applied
        assert manifest(payload) == merged_bytes and payload.stat().st_ino == merged_id
        assert b'Reviewed local instructions' in payload.joinpath('SKILL.md').read_bytes()
        if args.resolved:
            assert b'Explicitly reviewed resolution' in payload.joinpath('SKILL.md').read_bytes()
            assert not resolution_file.exists()
        assert payload.joinpath('keep-local-extra').read_bytes() == b'\x00\xffKeep exact local bytes'
        assert payload.joinpath('keep-empty').is_dir()
        assert source_path.stat().st_ino == source_id and manifest(source_path) == source_bytes
        assert registry.read_bytes() == original_registry
        next_plan = run('update-plan', '--path', payload)['operation']
        assert next_plan['revision']['source']['tree'] == source_plan['revision']['source']['tree']
        assert next_plan['revision']['recordedBase']['payload'] == source_payload_fingerprint
        assert 'SKILL.md' in next_plan['revision']['localChanges'] and 'keep-local-extra' in next_plan['revision']['localChanges']
        inspection = run('update-inspect', '--id', merged['id'])['operation']
        if args.kill_boundary in ('displace-moved', 'restore-moved'):
            kill_at(merged, 'undo', inspection['reviewFingerprint'])
            inspection = run('update-inspect', '--id', merged['id'])['operation']
        undone = run('update-undo', '--id', merged['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        assert undone['succeeded'] and undone['status'] == 'restored', undone
        assert manifest(payload) == before and payload.stat().st_ino == original_id
        assert manifest(Path(undone['displacedPath'])) == merged_bytes
        assert registry.read_bytes() == original_registry and manifest(source_path) == source_bytes
        results.append(dict(host=str(app), gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            sourceRevision=source_plan['revision']['source'], commands=commands, preserveVerified=True,
                            immutableReviewRestartVerified=True, priorApprovalRefused=True, wrongMergedDispositionRefused=True, mergedNativeStagePromoted=True,
                            localBytesAndEmptyDirectoryPreserved=True, sourceBaselineUnchanged=True,
                            nextUpdateUsesRealSourceBaseline=True,
                            exactOriginalNativeObjectRestored=True, registryBytesUnchanged=True,
                            windowsSupportedFixtureOwnerPrepared=windows_owner_prepared,
                            **(dict(processKilledAt=args.kill_boundary, restartRecoveryVerified=True) if args.kill_boundary else {}),
                            **(dict(capturedResolutionSurvivesInputDeletion=True) if args.resolved else {})))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(dict(hosts=len(results), commands=sum(len(item['commands']) for item in results), output=str(args.output))))
