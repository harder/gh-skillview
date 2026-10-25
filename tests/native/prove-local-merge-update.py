"""Historical schema3 lifecycle proof with current CLI apply/recovery/undo. All writes use disposable home/state/payloads."""
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

from historical_merge_proof import add_arguments, verify, assert_reviewed_shape

parser = argparse.ArgumentParser()
add_arguments(parser)
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--resolved', action='store_true', help='Use a captured custom SKILL.md resolution and delete its input before apply.')
parser.add_argument('--expect-metadata-refusal', metavar='REASON', help='Require this exact reason in every metadata refusal, and prove publication is refused without live changes.')
parser.add_argument('--proof-host', type=Path, help='Disposable proof host; required with a kill or disk-full boundary.')
fault = parser.add_mutually_exclusive_group()
fault.add_argument('--kill-boundary', choices=('retain-moved', 'install-moved', 'displace-moved', 'restore-moved'))
fault.add_argument('--disk-full-boundary', choices=('retain-moved', 'install-moved', 'displace-moved', 'restore-moved'),
                   help='Real ENOSPC; requires a Linux tmpfs of at most 128 MiB under TMPDIR.')
args = parser.parse_args()
boundary = args.kill_boundary or args.disk_full_boundary
if bool(args.proof_host) != bool(boundary) or boundary and args.expect_metadata_refusal:
    parser.error('Use --proof-host and one fault boundary together, without --expect-metadata-refusal.')
if args.disk_full_boundary and os.name != 'posix':
    parser.error('Real disk-full proof requires Linux; the host additionally verifies bounded tmpfs authority.')
app = args.app.resolve()
old, producer = verify(args, app)
proof_host = args.proof_host.resolve() if args.proof_host else None
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(args.gh[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()
old_commit = '33789149b9def6b66ddf247438a79fac94e22074'
results = []


from source_review_proof import acknowledge_frozen_source

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


def native_objects(directory):
    return [(str(path.relative_to(directory)), path.lstat().st_dev, path.lstat().st_ino)
            for path in [directory, *sorted(directory.rglob('*'))]]


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
        if args.disk_full_boundary:
            (root / 'state').mkdir()
            (root / 'tmp').mkdir()
            env.update(TMPDIR=str(root / 'tmp'), DOTNET_EnableDiagnostics='0')
            # Read-only admission occurs before installing or staging anything.
            admission = subprocess.run([str(proof_host), '--check-disk-full-root', str(root)],
                                       env=env, cwd=root, capture_output=True, text=True, timeout=30)
            assert admission.returncode == 0, (admission.stdout, admission.stderr)
            assert not (root / 'disk-full-proof-filler').exists()
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
        original_objects = native_objects(payload)
        registry = home / '.agents' / '.skill-lock.json'
        original_registry = registry.read_bytes()
        commands = []

        def run(command, *arguments, ok=True, historical=False):
            process = subprocess.run([str(old if historical else app), '--scan-root', str(skills), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (command, process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            commands.append(dict(host="historical" if historical else "current", command=command, exitCode=process.returncode))
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

        def disk_full_at(saved, action, fingerprint):
            filler = root / 'disk-full-proof-filler'
            assert not filler.exists() and not filler.is_symlink()
            # Save immutable resolution evidence too, including raw archive
            # bytes and modes. The filler never enters this private subtree.
            review_path = Path(source_plan['planPath']).parent / 'local-review'
            frozen_review = manifest(review_path)
            process = subprocess.Popen([str(proof_host), str(root), str(gh), saved['id'], fingerprint,
                                        action, 'disk-full:' + args.disk_full_boundary], env=env, cwd=root,
                                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            output = bytearray()
            truncated = threading.Event()

            def consume():
                while chunk := process.stdout.read(4096):
                    room = 256 * 1024 - len(output)
                    output.extend(chunk[:room])
                    if len(chunk) > room:
                        truncated.set()

            reader = threading.Thread(target=consume, daemon=True)
            reader.start()
            try:
                try:
                    exit_code = process.wait(timeout=90)
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=15)
                    reader.join(timeout=5)
                    assert not reader.is_alive(), 'Disk-full output drain did not finish'
                    process.stdout.close()
                text = output.decode('utf-8', errors='replace')
                assert not truncated.is_set(), 'Disk-full host exceeded its bounded output budget'
                identities = [json.loads(line.removeprefix('DISK_FULL_FILLER ')) for line in text.splitlines()
                              if line.startswith('DISK_FULL_FILLER ')]
                witnesses = [json.loads(line.removeprefix('DISK_FULL ')) for line in text.splitlines()
                             if line.startswith('DISK_FULL ')]
                assert len(identities) == len(witnesses) == 1, text
                witness = witnesses[0]
                assert witness['Name'] == filler.name and witness['Errno'] == 28 and witness['AvailableBytes'] == 0, witness
                assert (witness['Device'], witness['Inode']) == (identities[0]['Device'], identities[0]['Inode']), witness
                assert 0 < witness['FillerBytes'] <= witness['FilesystemBytes'] <= 128 * 1024 * 1024, witness
                lines = text.splitlines()
                witness_line = next(index for index, line in enumerate(lines) if line.startswith('DISK_FULL '))
                failed = json.loads('\n'.join(lines[witness_line + 1:]))
                assert exit_code == 2 and not failed['succeeded'] and not failed['canceled'], failed
                assert failed['status'] == 'needs-recovery' and failed['mutationObserved'], failed
                assert 'no space left on device' in failed['message'].lower(), failed
                disk_full_evidence.append(dict(boundary=args.disk_full_boundary, nativeWitness=witness, failure=failed))
            finally:
                # Only remove the exact inode this host exclusively created,
                # after its exit and output drain; never recursively clear space.
                identities = [json.loads(line.removeprefix('DISK_FULL_FILLER '))
                              for line in output.decode('utf-8', errors='replace').splitlines()
                              if line.startswith('DISK_FULL_FILLER ')]
                if len(identities) == 1:
                    identity = identities[0]
                    native = filler.lstat()
                    assert identity['Name'] == filler.name and stat.S_ISREG(native.st_mode) and native.st_nlink == 1
                    assert (native.st_dev, native.st_ino) == (identity['Device'], identity['Inode'])
                    assert 0 <= native.st_size <= 128 * 1024 * 1024
                    root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
                    try:
                        held = os.stat(filler.name, dir_fd=root_fd, follow_symlinks=False)
                        assert (held.st_dev, held.st_ino) == (native.st_dev, native.st_ino)
                        os.unlink(filler.name, dir_fd=root_fd)
                    finally:
                        os.close(root_fd)
            assert manifest(review_path) == frozen_review
            assert registry.read_bytes() == original_registry
            assert source_path.stat().st_ino == source_id and manifest(source_path) == source_bytes
            original_location = payload if args.disk_full_boundary == 'restore-moved' else Path(saved['retainedPath'])
            merged_location = (Path(saved['candidatePath']) if args.disk_full_boundary == 'retain-moved' else payload
                               if args.disk_full_boundary == 'install-moved' else Path(saved['displacedPath']))
            assert manifest(original_location) == before and native_objects(original_location) == original_objects
            assert manifest(merged_location) == merged_bytes and native_objects(merged_location) == merged_objects

        disk_full_evidence = []

        source_plan = run('update-plan', '--path', payload, '--unpin', historical=True)['operation']
        source_record = assert_reviewed_shape(source_plan, 2)
        source_payload_fingerprint = source_record['candidate']['payload']
        preserved = run('update-preserve', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert preserved['status'] == 'preserved' and not preserved['mutationObserved'], preserved
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        source_path = Path(source_plan['candidatePath'])
        source_bytes = manifest(source_path)
        source_id = source_path.stat().st_ino
        review = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'], historical=True)
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
            chosen = run('update-merge-choose', '--id', source_plan['id'], '--expect', review['reviewFingerprint'], '--resolve', 'SKILL.md', resolution_file, historical=True)
            resolved_row = next(row for row in chosen['review']['paths'] if row['path'] == 'SKILL.md')
            assert resolved_row['selection'] == 'resolved' and resolved_row['resolution']['kind'] == 'file'
            resolution_file.write_text('Changed after capture\n')
            resolution_file.unlink()
            reused = run('update-merge-choose', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'], '--choose-resolved', 'SKILL.md', historical=True)
            assert next(row for row in reused['review']['paths'] if row['path'] == 'SKILL.md')['resolution'] == resolved_row['resolution']
            chosen = reused
        else:
            chosen = run('update-merge-choose', '--id', source_plan['id'], '--expect', review['reviewFingerprint'], '--choose-installed', 'SKILL.md', historical=True)
        if args.expect_metadata_refusal:
            assert not chosen['canMaterialize'] and chosen['metadataRefusals'], chosen
            assert all(args.expect_metadata_refusal in issue for issue in chosen['metadataRefusals']), chosen
            assert not chosen['review']['topologyConflicts'] and all(row['selection'] for row in chosen['review']['paths']), chosen
            refused = run('update-merge-plan', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'], ok=False, historical=True)
            assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], refused
            assert 'native metadata cannot be preserved' in refused['error'].lower(), refused
            assert args.expect_metadata_refusal in refused['error'], refused
            assert manifest(payload) == before and payload.stat().st_ino == original_id
            assert registry.read_bytes() == original_registry
            assert source_path.stat().st_ino == source_id and manifest(source_path) == source_bytes
            assert not Path(source_plan['retainedPath']).exists() and not Path(source_plan['displacedPath']).exists()
            assert not list(Path(source_plan['planPath']).parent.glob('merged-*'))
            results.append(dict(host=str(app), historicalProducer=producer, gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                                sourceRevision=source_plan['revision']['source'], commands=commands,
                                proofKind='explicit-unsupported-metadata', metadataRefusals=chosen['metadataRefusals'],
                                windowsSupportedFixtureOwnerPrepared=windows_owner_prepared,
                                preserveVerified=True, priorApprovalRefused=True, nativePublicationRefused=True,
                                originalNativePayloadUnchanged=True, registryBytesUnchanged=True, sourceCandidateUnchanged=True,
                                **(dict(capturedResolutionSurvivesInputDeletion=True) if args.resolved else {})))
            continue
        assert chosen['canMaterialize'], chosen
        repeat = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'], historical=True)
        assert repeat['reviewFingerprint'] == chosen['reviewFingerprint']
        merged = run('update-merge-plan', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'], historical=True)
        assert_reviewed_shape(merged, 3)
        assert merged['localDisposition']['sourcePlanFingerprint'] == source_plan['planFingerprint']
        assert merged['localDisposition']['sourceCandidate']['payload'] == source_payload_fingerprint
        assert source_plan['revision']['source']['tree'] != source_plan['subject']['metadata']['githubTreeSha']
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        merged_bytes = manifest(Path(merged['candidatePath']))
        merged_id = Path(merged['candidatePath']).stat().st_ino
        merged_objects = native_objects(Path(merged['candidatePath']))
        refused = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'retain-original', '--expect', merged['planFingerprint'], ok=False)
        assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], (commands[-1], refused.get('error'))
        assert 'exact saved disposition: merge-reviewed' in refused['error'].lower(), refused
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        frozen_plan = Path(merged['planPath']).read_bytes()
        unacknowledged = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'merge-reviewed',
                             '--expect', merged['planFingerprint'], ok=False)
        assert 'source-review acknowledgement is required' in unacknowledged['error'], unacknowledged
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        assert not Path(merged['retainedPath']).exists()
        acknowledge_frozen_source(run, merged)
        assert Path(merged['planPath']).read_bytes() == frozen_plan
        if boundary in ('retain-moved', 'install-moved'):
            (disk_full_at if args.disk_full_boundary else kill_at)(merged, 'apply-merged', merged['planFingerprint'])
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
        if boundary in ('displace-moved', 'restore-moved'):
            (disk_full_at if args.disk_full_boundary else kill_at)(merged, 'undo', inspection['reviewFingerprint'])
            inspection = run('update-inspect', '--id', merged['id'])['operation']
        undone = run('update-undo', '--id', merged['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        assert undone['succeeded'] and undone['status'] == 'restored', undone
        assert manifest(payload) == before and payload.stat().st_ino == original_id
        assert native_objects(payload) == original_objects
        assert manifest(Path(undone['displacedPath'])) == merged_bytes
        assert registry.read_bytes() == original_registry and manifest(source_path) == source_bytes
        results.append(dict(host=str(app), historicalProducer=producer, gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            sourceRevision=source_plan['revision']['source'], commands=commands, preserveVerified=True,
                            immutableReviewRestartVerified=True, sourceReviewRequiredAndAcknowledged=True, priorApprovalRefused=True, wrongMergedDispositionRefused=True, mergedNativeStagePromoted=True,
                            localBytesAndEmptyDirectoryPreserved=True, sourceBaselineUnchanged=True,
                            nextUpdateUsesRealSourceBaseline=True,
                            exactOriginalNativeObjectRestored=True, registryBytesUnchanged=True,
                            windowsSupportedFixtureOwnerPrepared=windows_owner_prepared,
                            **(dict(processKilledAt=args.kill_boundary, restartRecoveryVerified=True) if args.kill_boundary else {}),
                            **(dict(diskFull=disk_full_evidence, restartRecoveryVerified=True,
                                    frozenReviewArchiveUnchanged=True) if args.disk_full_boundary else {}),
                            **(dict(capturedResolutionSurvivesInputDeletion=True) if args.resolved else {})))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(dict(hosts=len(results), commands=sum(len(item['commands']) for item in results), output=str(args.output))))
