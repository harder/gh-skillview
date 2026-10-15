"""Offline native-host managed discovery and mutation-refusal proof, disposable only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
gh_paths = args.gh or [Path(shutil.which('gh') or 'gh')]
results = []


def snapshot(root):
    rows = []
    if not root.exists():
        return rows
    for path in [root, *sorted(root.rglob('*'))]:
        info = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        data = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        rows.append((str(path.relative_to(root)), kind, stat.S_IMODE(info.st_mode),
                     info.st_dev, info.st_ino, hashlib.sha256(data).hexdigest()))
    return rows


for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-managed-proof-') as temporary:
        root = Path(temporary).resolve()
        home, cwd = root / 'home', root / 'project'
        home.mkdir()
        cwd.mkdir()
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

        def skill(path, name):
            path.mkdir(parents=True)
            path.joinpath('SKILL.md').write_bytes(f'---\nname: {name}\ndescription: Managed fixture\n---\nBody\n'.encode())
            return path

        control = skill(home / '.agents/skills/control', 'control')
        builtin = skill(home / '.codex/skills/.system/builtin', 'builtin')
        plugins = []
        for agent, version in [('codex', '1.0.0'), ('codex', '2.0.0'), ('claude', '3.0.0')]:
            plugin = home / f'.{agent}/plugins/cache/fixture-market/fixture-plugin' / version
            meta = plugin / f'.{agent}-plugin'
            meta.mkdir(parents=True)
            meta.joinpath('plugin.json').write_text(json.dumps({'name': 'fixture-plugin', 'skills': './skills'}))
            payload = skill(plugin / 'skills' / ('claude-skill' if agent == 'claude' else 'packaged'),
                            'claude-skill' if agent == 'claude' else 'packaged')
            plugin.joinpath('resources/empty').mkdir(parents=True)
            plugins.append((plugin, payload))
        protected = [home / '.codex', home / '.claude', control]
        before = [snapshot(path) for path in protected]
        rows = []

        def run(command, expected=0, flags=()):
            process = subprocess.run([str(app), *map(str, flags), *map(str, command), '--json'], env=env,
                                     cwd=cwd, capture_output=True, text=True, timeout=90)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            document = json.loads(process.stdout)
            assert [snapshot(path) for path in protected] == before, command
            rows.append(dict(command=str(command[0]), exitCode=process.returncode))
            return document

        inventory = run(['list'])
        assert len(inventory['skills']) == 5 and not inventory['partial'], inventory
        doctor = run(['doctor', '--inventory'])
        assert len(doctor['inventory']['skills']) == 5 and not doctor['inventory']['partial'], doctor
        assert all(provider['succeeded'] for provider in inventory['providers']), inventory['providers']
        packaged = [item for item in inventory['skills'] if item['name'] == 'packaged']
        assert {item['revision']['distributorVersion'] for item in packaged} == {'1.0.0', '2.0.0'}
        assert len({item['installationId'] for item in packaged}) == 2
        for item in inventory['skills']:
            if item['name'] == 'control':
                assert not item['ownerRecords']
                continue
            assert item['validity'] == 'Valid' and item['sourceIdentity']['origin'] is None, item
            expected_provider = 'codex-builtin' if item['name'] == 'builtin' else 'claude-plugin' if item['name'] == 'claude-skill' else 'codex-plugin'
            assert len(item['ownerRecords']) == 1 and item['ownerRecords'][0]['provider'] == expected_provider, item
            assert all(owner['readOnly'] and owner['recordPath'] is None for owner in item['ownerRecords']), item
            assert item['agents'], item
            assert all(agent['enabled'] is None and agent['activeSessionState'] == 'unknown' for agent in item['agents']), item
        for name in ['builtin', 'claude-skill']:
            refused = run(['remove', name, '--yes'], expected=1)
            assert not refused['succeeded'] and 'owner' in json.dumps(refused).lower(), refused
        plugin, payload = plugins[0]
        for command in [['quarantine', '--path', payload], ['update-plan', '--path', payload]]:
            refused = run(command, expected=1, flags=['--scan-root', plugin / 'skills'])
            assert 'managed' in json.dumps(refused).lower() or 'owner' in json.dumps(refused).lower(), refused
        source = root / 'source'
        skill(source / 'skills/different-leaf', 'safe-local')
        refused = run(['install', source, 'safe-local', '--from-local', '--force', '--path', plugin / 'resources'], expected=10)
        assert 'managed' in json.dumps(refused).lower(), refused
        ordinary = root / 'ordinary'
        child = ordinary / 'managed-child'
        child.mkdir(parents=True)
        child.joinpath('plugin.json').write_text('{"name":"fixture"}')
        neighbor_before = snapshot(child)
        ordinary_before = snapshot(ordinary)
        # A mutable --all set cannot be authorized by a separate preflight list.
        refused = run(['install', source, '--all', '--from-local', '--force', '--path', ordinary], expected=10)
        assert 'immutable staged' in json.dumps(refused), refused
        assert snapshot(ordinary) == ordinary_before
        installed = run(['install', source, 'safe-local', '--from-local', '--path', ordinary])
        assert installed['succeeded'] and ordinary.joinpath('safe-local/SKILL.md').is_file(), installed
        assert snapshot(child) == neighbor_before
        assert control.joinpath('SKILL.md').is_file()
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            builtinsAndAllCachedVersionsObserved=True, activeLoadingNotInferred=True,
                            protectedNativeObjectsBytesModesUnchanged=True, managedMutationsRefused=True,
                            unrelatedNamedLocalImportSucceeded=True, mutableAllSetRefused=True,
                            networkProxiesBlocked=True, fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps(results, indent=2))
