"""Offline native-host proof of physical identities and frozen operation impacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
results = []


def verify_native(path, observation):
    """Check product evidence against an independent platform observation."""
    assert Path(observation['canonicalPath']).samefile(path)
    if os.name != 'nt':
        native = path.stat()
        assert int(observation['volume'], 16) == native.st_dev
        assert int(observation['objectId'], 16) == native.st_ino
        assert int(observation['changeTimeSeconds']) == native.st_ctime_ns // 1_000_000_000
        assert int(observation['changeTimeNanoseconds']) == native.st_ctime_ns % 1_000_000_000
        return
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetFileInformationByHandleEx.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.GetFileInformationByHandleEx.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    class FileIdInfo(ctypes.Structure):
        _fields_ = [('volume', ctypes.c_uint64), ('id', ctypes.c_ubyte * 16)]

    class BasicInfo(ctypes.Structure):
        _fields_ = [('created', ctypes.c_int64), ('accessed', ctypes.c_int64),
                    ('written', ctypes.c_int64), ('changed', ctypes.c_int64), ('attributes', wintypes.DWORD)]

    handle = kernel.CreateFileW(str(path), 0x80, 7, None, 3, 0x02200000, None)
    assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
    try:
        identity, basic = FileIdInfo(), BasicInfo()
        assert kernel.GetFileInformationByHandleEx(handle, 18, ctypes.byref(identity), ctypes.sizeof(identity)), ctypes.get_last_error()
        assert kernel.GetFileInformationByHandleEx(handle, 0, ctypes.byref(basic), ctypes.sizeof(basic)), ctypes.get_last_error()
        assert int(observation['volume'], 16) == identity.volume
        assert int(observation['objectId'], 16) == int.from_bytes(bytes(identity.id), 'little')
        assert int(observation['windowsCreationTime']) == basic.created
        assert int(observation['windowsChangeTime']) == basic.changed
    finally:
        kernel.CloseHandle(handle)

for gh_path in args.gh or [Path(shutil.which('gh') or 'gh')]:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-identity-proof-') as temporary:
        root = Path(temporary).resolve()
        home, cwd, custom = root / 'home', root / 'project', root / 'custom'
        for path in (home, cwd, custom):
            path.mkdir()
        excluded = {'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR',
                    'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR'}
        env = {key: value for key, value in os.environ.items()
               if key not in excluded and not key.startswith(('GIT_', 'SKILLVIEW_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   PI_CODING_AGENT_DIR=str(home / '.pi/agent'),
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false',
                   GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='protocol.allow', GIT_CONFIG_VALUE_0='never',
                   HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9',
                   ALL_PROXY='http://127.0.0.1:9', NO_PROXY='')
        subprocess.run(['git', 'init', '--quiet', str(cwd)], env=env, cwd=root,
                       capture_output=True, text=True, check=True, timeout=30)

        def skill(path, metadata=''):
            path.mkdir(parents=True)
            path.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Identity fixture\n'
                                               + metadata + '---\nInstructions\n')
            return path

        tracking = ('metadata:\n  github-repo: https://github.com/fixture/skills\n'
                    '  github-path: skills/demo\n  github-ref: main\n'
                    '  github-tree-sha: ' + 'a' * 40 + '\n')
        shared = skill(home / '.agents/skills/demo', tracking)
        pinned = skill(cwd / '.agents/skills/demo', 'pinned: true\n' + tracking)
        local_source = root / 'source'
        local_source.mkdir()
        fork = skill(custom / 'demo', 'metadata:\n  local-path: ' + str(local_source).replace('\\', '/') + '\n')
        fork.joinpath('local-edit').write_bytes(b'preserve edited fork until exact removal')
        plugins = []
        for version in ('1', '2'):
            plugin = home / '.codex/plugins/cache/market/demo-plugin' / version
            plugin.joinpath('.codex-plugin').mkdir(parents=True)
            plugin.joinpath('.codex-plugin/plugin.json').write_text('{"name":"demo-plugin"}')
            plugins.append(skill(plugin / 'skills/demo'))
        flags = ['--scan-root', str(custom), '--profile', 'codex:one=' + str(shared.parent),
                 '--profile', 'codex:two=' + str(shared.parent)]
        rows = []

        def run(command, expected=0, decode=True):
            process = subprocess.run([str(app), *flags, *map(str, command), '--json'], env=env,
                                     cwd=cwd, capture_output=True, text=True, timeout=90)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            rows.append(dict(command=str(command[0]), exitCode=process.returncode))
            return json.loads(process.stdout) if decode else process

        inventory = run(['list'])
        assert not inventory['partial'] and len(inventory['skills']) == 5, inventory
        by_path = {Path(item['resolvedPath']).resolve(): item for item in inventory['skills']}
        assert len({item['installationId'] for item in by_path.values()}) == 5
        shared_row, pinned_row, fork_row = (by_path[path.resolve()] for path in (shared, pinned, fork))
        for path, item in by_path.items():
            native = item['subject']['nativeObject']
            assert native['status'] == 'observed' and native['role'] == 'inventory-observation', item
            assert native['objectId'] and native['volume'] and native['capturedAt'], item
            verify_native(path, native)
            assert item['subject']['addressRole'] == 'installation-address'
            assert item['subject']['materialization'] == 'directory'
            assert item['subject']['revision']['resolvedCommit'] is None
            assert item['subject']['revision']['payloadFingerprint'] is None
        assert shared_row['sourceIdentity']['id'] == pinned_row['sourceIdentity']['id']
        assert fork_row['sourceIdentity']['provider'] == 'local'
        assert {'one', 'two'} <= {exposure['profile'] for exposure in shared_row['subject']['exposures']}
        distributors = [by_path[path.resolve()]['distributors'][0] for path in plugins]
        assert distributors[0]['id'] == distributors[1]['id']
        assert distributors[0]['deliveryId'] != distributors[1]['deliveryId']
        assert all(not distributor['verifiedOrigin'] for distributor in distributors)
        assert all(by_path[path.resolve()]['sourceIdentity']['id'] is None for path in plugins)
        preview = run(['remove', '--install-id', shared_row['installationId']])
        assert preview['dryRun'] and preview['impact']['subject']['installationId'] == shared_row['installationId']
        assert {'one', 'two'} <= {exposure['profile'] for exposure in preview['impact']['affectedExposures']}
        update = run(['update', '--path', pinned, '--dry-run'])
        assert update['outcomes'][0]['status'] == 'pinned', update
        assert update['outcomes'][0]['impact']['subject']['installationId'] == pinned_row['installationId']
        managed = run(['remove', '--path', plugins[0], '--yes'], expected=1)
        assert not managed['succeeded'] and managed['impact']['subject']['distributors'], managed
        assert plugins[0].joinpath('SKILL.md').is_file()

        def unchanged_fixture():
            return [(str(path), path.stat().st_dev, path.stat().st_ino, path.stat().st_ctime_ns,
                     path.stat().st_mode, hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None)
                    for payload in (shared, pinned, fork, *plugins) for path in (payload, *sorted(payload.rglob('*'))) ]

        before_refusals = unchanged_fixture()
        ambiguous = run(['remove', 'demo'], expected=1, decode=False)
        assert 'skills match' in ambiguous.stderr and 'select --path or --install-id' in ambiguous.stderr, ambiguous.stderr
        assert unchanged_fixture() == before_refusals
        unsupported = run(['remove', '--path', fork, '--unknown'], expected=2, decode=False)
        assert 'unsupported remove flag: --unknown' in unsupported.stderr, unsupported.stderr
        assert unchanged_fixture() == before_refusals
        quarantine = run(['quarantine', '--path', fork])
        quarantine_operation = quarantine['operations'][0]
        assert quarantine_operation['status'] == 'preview'
        assert quarantine_operation['impact']['payloadRetained']
        assert Path(quarantine_operation['impact']['retainedPayloadPath']).samefile(fork)
        assert 'preview' in quarantine_operation['impact']['retentionEvidence']
        assert unchanged_fixture() == before_refusals
        broken = custom / 'dangling'
        broken.symlink_to(root / 'missing-payload', target_is_directory=True)
        cleanup = run(['cleanup', '--candidates', 'BrokenSymlink', '--apply', '--yes'])
        assert len(cleanup['applied']) == 1, cleanup
        impact = cleanup['applied'][0]['impact']
        assert impact['subject']['materialization'] == 'broken-link'
        assert not impact['payloadRetained'] and impact['retainedPayloadPath'] is None
        assert len(impact['affectedExposures']) == 1
        assert not broken.is_symlink()
        assert unchanged_fixture() == before_refusals
        before = shared_row['subject']['nativeObject']
        retained = root / 'held-original'
        shared.rename(retained)
        skill(shared, tracking)
        refreshed = run(['list'])
        recreated = next(item for item in refreshed['skills'] if Path(item['resolvedPath']).resolve() == shared.resolve())
        assert recreated['installationId'] == shared_row['installationId']
        assert recreated['subject']['nativeObject']['objectId'] != before['objectId']
        removed = run(['remove', '--install-id', fork_row['installationId'], '--yes'])
        assert removed['succeeded'] and not fork.exists(), removed
        assert removed['impact']['subject']['nativeObject']['objectId'] == fork_row['subject']['nativeObject']['objectId']
        assert removed['impact']['subject']['source']['provider'] == 'local'
        assert retained.joinpath('SKILL.md').is_file() and pinned.joinpath('SKILL.md').is_file()
        assert all(path.joinpath('SKILL.md').is_file() for path in plugins)
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            sameNameCopiesAndContextsPreserved=True, packageVersionsShareLogicalIdentity=True,
                            addressStableAcrossNativeReplacement=True, frozenDeletedSubjectRetained=True,
                            nativeObjectGenerationIndependentlyVerified=True,
                            managedRefusalRetainsSubject=True, exactSelectorsRequired=True,
                            quarantinePreviewRetainsOriginal=True, brokenUnlinkDoesNotInventRetainedPayload=True,
                            resolvedCommitAndPayloadHashNotInvented=True, networkProxiesBlocked=True, fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps(results, indent=2))
