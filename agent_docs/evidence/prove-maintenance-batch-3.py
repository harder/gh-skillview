"""Disposable native-host proof. Every mutation stays under a private home."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', required=True, action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
auth = subprocess.run([str(args.gh[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()
results = []


def payload(path):
    return {str(file.relative_to(path)): hashlib.sha256(file.read_bytes()).hexdigest()
            for file in sorted(path.rglob('*')) if file.is_file()}


for gh in args.gh:
    with tempfile.TemporaryDirectory(prefix='skillview-batch3-proof-') as temporary:
        root = Path(temporary)
        home = root / 'home'
        workspace = root / 'workspace'
        home.mkdir()
        workspace.mkdir()
        shared = home / '.agents' / 'skills'
        custom = root / 'custom'
        env = os.environ.copy()
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), XDG_STATE_HOME=str(root / 'state'),
                   XDG_CACHE_HOME=str(root / 'cache'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_DATA_HOME=str(root / 'data'), APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        for directory in (shared, custom):
            installed = subprocess.run([str(gh), 'skill', 'install', '--dir', str(directory),
                                        '--pin=33789149b9def6b66ddf247438a79fac94e22074', '--', 'cli/cli', 'skills/gh-skill'],
                                       env=env, cwd=workspace, capture_output=True, text=True, timeout=120)
            assert installed.returncode == 0, installed.stderr
        roots = [shared, custom]

        def run(command, parse=True):
            argv = [str(args.app)]
            for directory in roots:
                argv.extend(['--scan-root', str(directory)])
            result = subprocess.run(argv + command, env=env, cwd=workspace, capture_output=True, text=True, timeout=180)
            return result, json.loads(result.stdout) if parse else None

        process, inventory = run(['list', '--json'])
        assert process.returncode == 0, process.stderr
        assert inventory['schemaVersion'] == 3
        assert len(inventory['skills']) == 2, inventory
        assert all(Path(row['resolvedPath']).resolve().is_relative_to(root.resolve()) for row in inventory['skills'])
        left, right = shared / 'gh-skill', custom / 'gh-skill'
        shared_row = next(row for row in inventory['skills'] if Path(row['resolvedPath']) == left)
        assert shared_row['ownerRecords'][0]['exclusiveOwner'] is False
        process, comparison = run(['compare', '--path', str(left), '--other-path', str(right), '--json'])
        assert process.returncode == 0, comparison
        assert comparison['sourceRelationship'] == 'same-source' and comparison['revisionRelationship'] == 'same-recorded-revision'
        assert comparison['complete'] and not comparison['requiresExposureMigration']
        original_right = payload(right)
        process, update = run(['update', '--path', str(left), '--unpin', '--json'])
        assert process.returncode == 0 and update['mutationAttempted'], update
        assert update['outcomes'][0]['status'] == 'changed' and payload(right) == original_right
        lock = home / '.agents' / '.skill-lock.json'
        shared_record = lock.read_bytes()
        process, update = run(['update', '--path', str(right), '--unpin', '--json'])
        assert process.returncode == 0 and update['mutationAttempted'], update
        assert update['outcomes'][0]['status'] == 'changed'
        assert lock.read_bytes() == shared_record, 'Custom update replaced the shared record'
        before = (payload(left), payload(right))
        process, install = run(['install', 'different/repo', 'gh-skill', '--path', str(custom), '--force', '--json'])
        assert process.returncode != 0 and not install['succeeded'], install
        assert 'existing shared selector' in install['errorMessage']
        assert lock.read_bytes() == shared_record and before == (payload(left), payload(right))
        for invalid in (dict(version=99, skills={}), dict(version=3, skills={}, lastSelectedAgents=['codex'])):
            lock.write_text(json.dumps(invalid))
            exact_lock = lock.read_bytes()
            process, install = run(['install', 'different/repo', 'new-skill', '--path', str(custom), '--json'])
            assert process.returncode != 0 and not install['succeeded']
            assert lock.read_bytes() == exact_lock and before == (payload(left), payload(right))
        lock.write_bytes(shared_record)
        right.joinpath('local-extra.txt').write_text('Retain this local addition.')
        process, comparison = run(['compare', '--path', str(left), '--other-path', str(right), '--json'])
        assert process.returncode == 0 and comparison['payloadRelationship'] == 'different-payloads', comparison
        assert 'local-extra.txt' in comparison['differingFiles'] and not comparison['requiresExposureMigration']
        projects = []
        for name, revision in (('one', 'v1'), ('two', 'v2')):
            project = root / name
            directory = project / '.agents' / 'skills' / 'demo'
            directory.mkdir(parents=True)
            directory.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Demo\n---\nInstructions\n')
            project.joinpath('skills-lock.json').write_text(json.dumps(dict(version=1, skills=dict(demo=dict(
                source='team/repo', sourceType='github', skillPath='skills/demo/SKILL.md', ref=revision, computedHash=revision)))))
            roots.append(directory.parent)
            projects.append(directory)
        process, comparison = run(['compare', '--path', str(projects[0]), '--other-path', str(projects[1]), '--json'])
        assert process.returncode == 0 and comparison['complete'], comparison
        assert comparison['revisionRelationship'] == 'different-recorded-revisions' and comparison['payloadRelationship'] == 'byte-identical'
        offline_trace = None
        if os.name != 'nt':
            trace = root / 'trace.txt'
            wrapper = root / 'gh-wrapper'
            wrapper.write_text('#!/bin/sh\nprintf "%s\\n" "$*" >> "$SKILLVIEW_TRACE_FILE"\nexec "$SKILLVIEW_REAL_GH" "$@"\n')
            wrapper.chmod(0o700)
            env.update(GH_PATH=str(wrapper), SKILLVIEW_REAL_GH=str(gh), SKILLVIEW_TRACE_FILE=str(trace))
            process, comparison = run(['compare', '--path', str(left), '--other-path', str(right), '--json'])
            assert process.returncode == 0
            offline_trace = trace.read_text().splitlines()
            assert not any(line.startswith(('auth ', 'api ', 'skill update ', 'skill install ', 'send-telemetry')) for line in offline_trace), offline_trace
        results.append(dict(gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            nativeHost=str(args.app), listSchemaVersion=3, sharedOwnerCorroborated=True,
                            equivalentGitTreeDomains=True, sharedUpdateChangedOneCopy=True,
                            customUpdatePreservedSharedRecord=True, collidingInstallRefusedWithoutMutation=True,
                            unsupportedSharedWritesPreservedBytes=True, completePayloadExtraDetected=True,
                            contextualRevisionsDistinguished=True, offlineComparisonTrace=offline_trace))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
