"""Compare actual gh remote/pinned/local installs with native SkillView inventory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
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
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(gh_paths[-1]), 'auth', 'token'], capture_output=True,
                          text=True, check=True).stdout.strip()


def manifest(payloads):
    entries = []
    for payload in payloads:
        for path in [payload, *sorted(payload.rglob('*'))]:
            info = path.lstat()
            kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
            value = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
            entries.append((str(path), kind, info.st_dev, info.st_ino, stat.S_IMODE(info.st_mode),
                            hashlib.sha256(value).hexdigest()))
    return entries


def short_ref(value):
    for prefix in ['refs/heads/', 'refs/tags/']:
        if value and value.startswith(prefix):
            return value[len(prefix):]
    return value


results = []
for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-tracking-roundtrip-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if not key.startswith(('GIT_', 'SKILLVIEW_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false',
                   GIT_CONFIG_COUNT='0')

        def run(command):
            process = subprocess.run([str(value) for value in command], env=env, cwd=root,
                                     capture_output=True, text=True, timeout=180)
            assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
            return process.stdout

        source = root / 'author-source'
        descriptions = {'local-literal': ('|-', 'First line\nsecond line'),
                        'local-folded': ('>-', 'First line second line')}
        fixtures = []
        for kind in ['unpinned', 'pinned', 'local-literal', 'local-folded']:
            destination = root / ('destination-' + kind)
            name = kind if kind.startswith('local-') else 'gh-skill'
            command = [gh, 'skill', 'install', '--dir', destination]
            if kind.startswith('local-'):
                original = source / name
                original.mkdir(parents=True)
                style = descriptions[kind][0]
                original.joinpath('SKILL.md').write_bytes(
                    ('---\nname: ' + name + '\ndescription: ' + style
                     + '\n  First line\n  second line\nallowed-tools: Read Bash(git log:*)\nmetadata:\n'
                     + '  version: "1.0"\n  vendor/example: keep\n---\n# Private fixture\n').encode())
                command += ['--from-local', '--', source, name]
            else:
                if kind == 'pinned':
                    command += ['--pin=v2.102.0']
                command += ['--', 'cli/cli', 'skills/gh-skill']
            run(command)
            fixtures.append((kind, name, destination, destination / name))
        payloads = [fixture[3] for fixture in fixtures]
        before = manifest(payloads)
        registry = home / '.agents' / '.skill-lock.json'
        registry_before = registry.read_bytes()
        rows = []
        for kind, name, destination, payload in fixtures:
            listing = json.loads(run([gh, 'skill', 'list', '--dir', destination, '--json',
                                      'skillName,agentHosts,path,pinned,scope,sourceURL,version']))
            document = json.loads(run([app, '--scan-root', destination, 'list', '--allow-hidden-dirs', '--json']))
            assert len(listing) == len(document['skills']) == 1, (kind, listing, document)
            assert not document['partial'] and not document['inaccessiblePaths'], document
            assert all(provider['succeeded'] for provider in document['providers']), document
            record, skill = listing[0], document['skills'][0]
            assert skill['name'] == record['skillName'] == name, (kind, skill, record)
            assert os.path.samefile(record['path'], payload) and os.path.samefile(skill['resolvedPath'], payload)
            assert re.fullmatch(r'[0-9a-f]{64}', skill['installationId'] or ''), skill
            assert skill['validity'] == 'Valid' and not skill['diagnostics'], skill
            assert skill['source'] == record['sourceURL'], (kind, skill, record)
            assert skill['pinned'] == record['pinned'] == (kind == 'pinned'), (kind, skill, record)
            assert not skill['sourceIdentity']['conflicted'], skill
            if kind.startswith('local-'):
                assert os.path.samefile(skill['localSourcePath'], source / name), skill
                assert skill['sourceIdentity']['provider'] == 'local', skill
                assert os.path.samefile(skill['sourceIdentity']['origin'], source / name), skill
                assert skill['description'] == descriptions[kind][1] and skill['version'] == '1.0', skill
                assert all(skill[key] is None for key in ['repositoryRef', 'sourcePath', 'pinnedRef', 'githubTreeSha']), skill
                assert record['version'] == '', record
            else:
                assert skill['source'] == 'https://github.com/cli/cli', skill
                assert skill['sourcePath'] == 'skills/gh-skill', skill
                assert short_ref(skill['repositoryRef']) == record['version'], (kind, skill, record)
                assert re.fullmatch(r'[0-9a-f]{40}', skill['githubTreeSha'] or ''), skill
                assert skill['localSourcePath'] is None and skill['sourceIdentity']['provider'] == 'repository', skill
                assert skill['pinnedRef'] == ('v2.102.0' if kind == 'pinned' else None), skill
            assert manifest(payloads) == before and registry.read_bytes() == registry_before
            rows.append(dict(kind=kind, ghList=record, skill=skill))
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=run([gh, '--version']).splitlines()[0],
                            actualGhRemotePinnedAndLocalInstalls=True, capturedFieldsMatchGhList=True,
                            localDescriptionAndAuthoredVersionPreserved=True,
                            repositoryRefAndExactPinRemainDistinct=True,
                            installedObjectsBytesModesAndRegistryUnchanged=True, fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2) + '\n')
print('Tracking round-trip native proof passed')
