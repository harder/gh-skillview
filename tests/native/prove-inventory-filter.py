"""Actual CLI filters over one payload with user/project/profile/alias exposures."""
import argparse
import hashlib
import json
import os
import re
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


def manifest(paths):
    result = []
    for path in paths:
        info = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        value = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        result.append((str(path), kind, stat.S_IMODE(info.st_mode), info.st_dev, info.st_ino,
                       hashlib.sha256(value).hexdigest()))
    return result


results = []
for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-inventory-filter-') as temporary:
        root = Path(temporary).resolve()
        home, project, store = root / 'home', root / 'project', root / 'payloads'
        home.mkdir()
        project.mkdir()
        payload = store / 'demo'
        payload.mkdir(parents=True)
        payload.joinpath('SKILL.md').write_bytes(b'---\nname: demo\ndescription: Shared exposure fixture\n---\nBody\n')
        current = project / 'packages' / 'app'
        current.mkdir(parents=True)
        links = [home / '.codex' / 'skills' / 'demo', project / '.claude' / 'skills' / 'demo']
        for link in links:
            link.parent.mkdir(parents=True)
            link.symlink_to(payload, target_is_directory=True)
        alias = root / 'payload-alias'
        alias.symlink_to(store, target_is_directory=True)
        env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith(('GIT_', 'SKILLVIEW_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false',
                   GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='protocol.allow', GIT_CONFIG_VALUE_0='never',
                   HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9',
                   ALL_PROXY='http://127.0.0.1:9', NO_PROXY='')
        subprocess.run(['git', 'init', '--quiet', str(project)], env=env, cwd=root,
                       capture_output=True, text=True, check=True, timeout=30)
        def installed_manifest():
            # Enumerate again so additions cannot hide outside a frozen path list.
            return manifest([payload, *sorted(payload.rglob('*')), *links, alias])

        before = installed_manifest()
        baseline = None
        rows = []
        for scope, agent, expected in [
                (None, None, 1), ('user', 'codex', 1), ('project', 'claude', 1),
                ('project', 'claude-code', 1), ('user', 'claude-code', 0), ('project', 'codex', 0),
                ('user', None, 1), (None, 'codex', 1), (None, 'claude', 1),
                ('custom', None, 1), ('custom', 'codex', 0)]:
            flags = (['--scope', scope] if scope else []) + (['--agent', agent] if agent else [])
            command = [str(app), '--scan-root', str(store), '--scan-root', str(alias),
                       '--profile', f'codex:alias-profile={alias}', 'list', *flags,
                       '--allow-hidden-dirs', '--json']
            process = subprocess.run(command, env=env, cwd=current, capture_output=True, text=True, timeout=90)
            assert process.returncode == (0 if expected else 20), (scope, agent, process.returncode, process.stdout, process.stderr)
            document = json.loads(process.stdout)
            skills = document['skills']
            assert len(skills) == expected, (scope, agent, document)
            assert all(provider['succeeded'] for provider in document['providers']), document['providers']
            assert not document['partial'] and not document['inaccessiblePaths'], document
            if expected:
                skill = skills[0]
                if baseline is None:
                    baseline = skill
                    assert re.fullmatch(r'[0-9a-f]{64}', skill['installationId'] or ''), skill
                    exposures = {(a['id'], a['scope']) for a in skill['agents']}
                    assert {('codex', 'User'), ('claude-code', 'Project')}.issubset(exposures), skill
                    assert any(a['profile'] == 'alias-profile' for a in skill['agents']), skill
                    assert any(Path(a['path']) == alias / 'demo' for a in skill['agents']), skill
                assert skill['installationId'] == baseline['installationId'], skill
                assert skill['agents'] == baseline['agents'], (scope, agent, skill, baseline)
            assert installed_manifest() == before
            rows.append(dict(scope=scope, agent=agent, expectedRows=expected, actualRows=len(skills),
                             exitCode=process.returncode, selectedInstallationId=skills[0]['installationId'] if skills else None))
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            onePhysicalInstallation=True, directLinkAncestorAndProfileExposuresRetained=True,
                            oppositeScopeAgentCombinationsExcluded=True, allConsumerObservationsRetained=True,
                            installedNativeObjectsAndBytesUnchanged=True, networkProxiesBlocked=True, fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps(results, indent=2))
