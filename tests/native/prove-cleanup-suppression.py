"""Verify external cleanup preferences using disposable actual-gh installations."""
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
results = []
gh_paths = args.gh or [Path(shutil.which('gh') or 'gh')]
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(gh_paths[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()


def manifest(paths):
    result = []
    for payload in paths:
        for path in [payload, *sorted(payload.rglob('*'))]:
            info = path.lstat()
            kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
            data = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
            result.append((str(path), kind, info.st_dev, info.st_ino, info.st_ctime_ns,
                           stat.S_IMODE(info.st_mode), hashlib.sha256(data).hexdigest()))
    return result


for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-suppression-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GIT_', 'SKILLVIEW_', 'GH_', 'GITHUB_', 'CLAUDE_', 'CODEX_', 'PI_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth,
                   GH_EXTENSION='1', GH_PATH=str(gh), GH_CONFIG_DIR=str(root / 'gh-config'),
                   SKILLVIEW_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_STATE_HOME=str(root / 'xdg-state'), XDG_DATA_HOME=str(root / 'data'),
                   XDG_CACHE_HOME=str(root / 'cache'), APPDATA=str(root / 'appdata'),
                   LOCALAPPDATA=str(root / 'localappdata'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GIT_CONFIG_COUNT='0')
        commands = []

        def run(command, expected=0):
            command = [str(item) for item in command]
            process = subprocess.run(command, env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            commands.append({'argv': command, 'exitCode': process.returncode})
            return process.stdout

        source = root / 'source' / 'demo'
        source.mkdir(parents=True)
        source.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Local authored fixture\n---\nPrivate instructions.\n')
        source.joinpath('extra.txt').write_text('Keep all extra files and modes.\n')
        skills = home / '.agents' / 'skills'
        run([gh, 'skill', 'install', '--from-local', '--dir', skills, '--', source.parent, 'demo'])
        run([gh, 'skill', 'install', '--dir', skills, '--', 'cli/cli', 'skills/gh-skill'])
        owned = skills / 'gh-skill'
        peer = skills / 'same-name'
        unknown = skills / 'unknown'
        for destination in [peer, unknown]:
            destination.mkdir()
            destination.joinpath('SKILL.md').write_text('---\nname: gh-skill\ndescription: Ownerless fixture\n---\nAuthored local instructions.\n')
        alias = root / 'agent-demo'
        alias.symlink_to(owned, target_is_directory=True)
        registry = home / '.agents' / '.skill-lock.json'
        registry_before = registry.read_bytes()
        payloads = [owned, skills / 'demo', peer, unknown]
        before = manifest(payloads)

        def product(*arguments, expected=0):
            return json.loads(run([app, '--scan-root', skills, *arguments, '--json'], expected))

        def row(document, path):
            return next(value for value in document['skills'] if os.path.samefile(value['resolvedPath'], path))

        initial = product('list', '--allow-hidden-dirs')
        assert not initial['preferenceDiagnostics'] and not initial['partial'], initial
        assert not (root / 'state' / 'SkillView' / 'preferences').exists(), 'Read created preferences'
        assert row(initial, owned)['ownerRecords'], 'Actual gh owner was not captured'
        assert row(initial, skills / 'demo')['sourceIdentity']['provider'] == 'local'
        assert os.path.samefile(row(initial, skills / 'demo')['localSourcePath'], source)
        checks = []
        for target, selector, mode in [(owned, alias, 'source'), (skills / 'demo', skills / 'demo', 'source'), (unknown, unknown, 'object')]:
            saved = product('ignore', '--path', selector)
            assert saved['succeeded'] and saved['changed'] and saved['cleanupSuppression']['external'], saved
            assert saved['cleanupSuppression']['bindingMode'] == mode, saved
            repeated = product('ignore', '--path', target)
            assert not repeated['changed'], repeated
            listing = product('list', '--allow-hidden-dirs')
            observed = row(listing, target)
            assert observed['ignored'] and observed['cleanupSuppression']['external'], observed
            assert not observed['cleanupSuppression']['legacyMarker'], observed
            assert not row(listing, peer)['ignored'], listing
            assert manifest(payloads) == before and registry.read_bytes() == registry_before
            cleared = product('ignore', '--install-id', observed['installationId'], '--clear')
            assert cleared['changed'] and not cleared['cleanupSuppression']['external'], cleared
            assert not cleared['cleanupSuppression']['legacyMarker'], cleared
            assert not row(product('list', '--allow-hidden-dirs'), target)['ignored']
            assert manifest(payloads) == before and registry.read_bytes() == registry_before
            checks.append({'path': str(target), 'bindingMode': saved['cleanupSuppression']['bindingMode'],
                           'restartObserved': True, 'payloadAndRegistryUnchanged': True})
        assert all(not (target / '.skillview-ignore').exists() for target in payloads)
        product('ignore', '--path', unknown)
        unknown.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Replacement content\n---\nChanged in place.\n')
        expired = row(product('list', '--allow-hidden-dirs'), unknown)['cleanupSuppression']
        assert not expired['external'] and expired['state'] == 'inactive', expired
        product('ignore', '--path', unknown, '--clear')
        # Legacy marker is recognized but never silently deleted by clear-external.
        unknown.joinpath('.skillview-ignore').write_bytes(b'')
        legacy_before = manifest([unknown])
        product('ignore', '--path', unknown)
        legacy_clear = product('ignore', '--path', unknown, '--clear')
        assert legacy_clear['cleanupSuppression']['legacyMarker'] and not legacy_clear['cleanupSuppression']['external']
        assert manifest([unknown]) == legacy_before
        # A separate state home cannot inherit this home's suppression.
        product('ignore', '--path', owned)
        env['SKILLVIEW_STATE_HOME'] = str(root / 'other-state')
        assert not row(product('list', '--allow-hidden-dirs'), owned)['ignored']
        env['SKILLVIEW_STATE_HOME'] = str(root / 'state')
        assert row(product('list', '--allow-hidden-dirs'), owned)['ignored']
        product('ignore', '--path', owned, '--clear')
        preferences = root / 'state' / 'SkillView' / 'preferences'
        records = sorted(preferences.glob('*.json'))
        assert records, preferences
        records[-1].write_text('{truncated')
        damaged = product('list', '--allow-hidden-dirs')
        assert damaged['preferenceDiagnostics'] and not damaged['partial'] and not row(damaged, owned)['cleanupSuppression']['external'], damaged
        assert registry.read_bytes() == registry_before
        results.append({'ghVersion': run([gh, '--version']).splitlines()[0], 'app': str(app),
                        'checks': checks, 'aliasResolved': True, 'unknownInPlaceEditExpires': True,
                        'legacyPreserved': True, 'alternateStateIsolated': True,
                        'corruptClearDoesNotReviveSuppression': True, 'commands': commands})
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps({'schemaVersion': 1, 'status': 'passed',
    'app': {'path': str(app), 'sha256': hashlib.sha256(app.read_bytes()).hexdigest()},
    'results': results}, indent=2) + '\n')
print(json.dumps({'results': len(results), 'commands': sum(len(item['commands']) for item in results)}))
