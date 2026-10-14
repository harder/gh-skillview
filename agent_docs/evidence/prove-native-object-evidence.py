"""Inspect native object evidence only in disposable local skill fixtures."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
results = []

for gh in args.gh or [None]:
    with tempfile.TemporaryDirectory(prefix='skillview-object-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'),
                   SKILLVIEW_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_STATE_HOME=str(root / 'xdg-state'), APPDATA=str(root / 'appdata'),
                   LOCALAPPDATA=str(root / 'localappdata'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        if gh:
            env.update(GH_EXTENSION='1', GH_PATH=str(gh.resolve()))
        else:
            env.pop('GH_EXTENSION', None)
            env.pop('GH_PATH', None)
        skills = root / 'skills'
        first, second = skills / 'first', skills / 'second'
        markdown = ('---\nname: object-skill\ndescription: Fixture\nmetadata:\n'
                    '  github-repo: https://github.com/fixture/skills\n  github-ref: main\n'
                    '  github-tree-sha: ' + 'a' * 40 + '\n  github-path: skills/object\n---\nBody\n')
        for payload in [first, second]:
            payload.mkdir(parents=True)
            payload.joinpath('SKILL.md').write_text(markdown)

        def compare():
            process = subprocess.run([str(app), '--scan-root', str(skills), 'compare', '--path', str(first),
                                      '--other-path', str(second), '--json'], env=env, cwd=root,
                                     capture_output=True, text=True, timeout=120)
            assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
            value = json.loads(process.stdout)
            assert value['complete'] and value['payloadRelationship'] == 'byte-identical', value
            assert value['leftFingerprint'] == value['rightFingerprint'], value
            assert value['leftObjectStateFingerprint'] != value['rightObjectStateFingerprint'], value
            assert len(value['leftObjectStateFingerprint']) == len(value['rightObjectStateFingerprint']) == 64
            assert 'not attested' in value['objectEvidenceContract'], value
            return value

        before = compare()
        stable = compare()
        assert before['leftObjectStateFingerprint'] == stable['leftObjectStateFingerprint']
        assert before['rightObjectStateFingerprint'] == stable['rightObjectStateFingerprint']
        replacement = root / 'replacement'
        replacement.write_bytes(first.joinpath('SKILL.md').read_bytes())
        replacement.replace(first / 'SKILL.md')
        changed = compare()
        assert before['leftObjectStateFingerprint'] != changed['leftObjectStateFingerprint']
        assert before['leftFingerprint'] == changed['leftFingerprint']
        assert before['rightObjectStateFingerprint'] == changed['rightObjectStateFingerprint']
        if os.name != 'nt':
            mode = first.stat().st_mode & 0o7777
            first.chmod(mode ^ 0o001)
            changed_mode = compare()
            assert changed_mode['leftRootUnixMode'] != changed_mode['rightRootUnixMode']
            assert not changed_mode['requiresExposureMigration']
        assert not root.joinpath('state').exists(), 'Read-only comparison created maintenance state'
        assert first.joinpath('SKILL.md').read_text() == second.joinpath('SKILL.md').read_text() == markdown
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=str(gh) if gh else 'runner default', sameBytesDistinctObjects=True,
                            readOnlyObjectEvidenceStable=True, identicalContentReplacementObserved=True,
                            unixRootModesRespected=os.name != 'nt', metadataCoverageExplicit=True,
                            comparisonCreatedNoMaintenanceState=True, payloadBytesUnchanged=True))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
