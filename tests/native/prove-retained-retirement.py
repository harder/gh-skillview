#!/usr/bin/env python3
"""Offline native retirement/process-death proof using marked disposable state."""
import argparse
import hashlib
import json
import os
import pathlib
import queue
import shutil
import stat
import subprocess
import tempfile
import threading
import time


def documents(text):
    decoder = json.JSONDecoder()
    offset = 0
    while True:
        start = text.find('{', offset)
        if start < 0:
            return
        try:
            value, length = decoder.raw_decode(text[start:])
            yield value
            offset = start + length
        except ValueError:
            offset = start + 1


def audit(paths):
    result = {}
    def visit(path):
        try:
            info = path.lstat()
        except FileNotFoundError:
            result[str(path)] = None
            return
        item = [info.st_dev, info.st_ino, info.st_ctime_ns, stat.S_IMODE(info.st_mode)]
        if stat.S_ISLNK(info.st_mode):
            item += ['link', os.readlink(path)]
        elif stat.S_ISDIR(info.st_mode):
            item += ['directory']
            for child in sorted(path.iterdir()):
                visit(child)
        else:
            item += ['file', hashlib.sha256(path.read_bytes()).hexdigest()]
        result[str(path)] = item
    for path in paths:
        visit(path)
    return result


def isolated_environment(root):
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('GH_', 'GITHUB_', 'GIT_', 'SKILLVIEW_', 'CLAUDE_', 'CODEX_'))}
    env.update(HOME=str(root / 'home'), USERPROFILE=str(root / 'home'),
               SKILLVIEW_STATE_HOME=str(root / 'state'),
               XDG_STATE_HOME=str(root / 'xdg-state'), XDG_CONFIG_HOME=str(root / 'xdg-config'),
               XDG_DATA_HOME=str(root / 'xdg-data'), XDG_CACHE_HOME=str(root / 'xdg-cache'),
               APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
               CODEX_HOME=str(root / 'home/.codex'), CLAUDE_CONFIG_DIR=str(root / 'home/.claude'),
               CLAUDE_CODE_PLUGIN_CACHE_DIR=str(root / 'plugins'), CLAUDE_CODE_PLUGIN_DIRS='',
               CLAUDE_CODE_PLUGIN_SEED_DIR='', PI_CODING_AGENT_DIR=str(root / 'home/.pi'),
               GH_CONFIG_DIR=str(root / 'gh-config'), GIT_CONFIG_COUNT='0', GIT_CONFIG_NOSYSTEM='1',
               GIT_CONFIG_GLOBAL=str(root / 'gitconfig'), GIT_DIR=str(root / 'private.git'),
               GIT_WORK_TREE=str(root), GIT_COMMON_DIR=str(root / 'private.git'),
               GIT_ALLOW_PROTOCOL='', HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9',
               ALL_PROXY='http://127.0.0.1:9', NO_PROXY='',
               GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false',
               TMPDIR=str(root / 'tmp'), TMP=str(root / 'tmp'), TEMP=str(root / 'tmp'))
    return env


def owner_scenarios(app, proof):
    cases = []
    modes = ['install-moved', 'owner-publish-reviewed', 'restore-moved',
             'pause-install-moved', 'pause-restore-moved', 'completed-reverted', 'equal',
             'preview-pending', 'slot-pending', 'resume-pending', 'fallback-advanced']
    for mode in modes:
        root = pathlib.Path(tempfile.mkdtemp(prefix='skillview-retirement-owner-',
                    dir=os.environ.get('TMPDIR', '/var/tmp') if os.name != 'nt' else None)).resolve()
        root.chmod(0o700)
        (root / 'tmp').mkdir()
        (root / 'disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = isolated_environment(root)
        def invoke(command, expected=0):
            result = subprocess.run(command, cwd=root, env=env, text=True, capture_output=True, timeout=60)
            assert result.returncode == expected, (command, result.returncode, result.stdout, result.stderr)
            return result.stdout if expected == 0 else result.stdout + result.stderr
        invoke([proof, '--retained-retirement-fixture', str(root)])
        setup_mode = 'install-moved' if mode.endswith('-pending') or mode == 'fallback-advanced' else mode
        command = [proof, '--retained-retirement', str(root), 'owner-prepare', setup_mode]
        if mode.startswith('pause-'):
            proc = subprocess.Popen(command, cwd=root, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            lines = queue.Queue()
            def drain():
                for line in proc.stdout:
                    lines.put(line)
            thread = threading.Thread(target=drain, daemon=True); thread.start()
            captured = []
            try:
                deadline = time.monotonic() + 45
                while True:
                    line = lines.get(timeout=max(.01, deadline - time.monotonic()))
                    captured.append(line)
                    if line.strip() == 'READY ' + mode.removeprefix('pause-'): break
                proc.kill(); proc.wait(timeout=10); thread.join(timeout=5)
            finally:
                if proc.poll() is None: proc.kill(); proc.wait(timeout=10)
            setup = ''.join(captured)
        else:
            setup = invoke(command)
        plan = next(item for item in documents(setup) if item.get('subject', {}).get('name') == 'owner')
        id_ = plan['id']; operation = pathlib.Path(plan['planPath']).parent
        def action(name, expected=0):
            return invoke([proof, '--retained-retirement', str(root), name, id_], expected)
        live = root / 'home/.agents/skills/owner'
        protected = [live, root / 'home/.agents/.skill-lock.json', root / 'custom/owner-consumer']
        if mode.endswith('-pending') or mode == 'fallback-advanced':
            action('owner-registry-foreign')
            review = json.loads(action('inspect'))
            retire = [proof, '--retained-retirement', str(root), 'retire', id_, review['fingerprint'], review['kind'], 'none']
            if mode == 'resume-pending':
                invoke(retire[:-1] + ['stop:retirement-marker-written'], expected=2)
            if mode != 'slot-pending' and mode != 'fallback-advanced': action('owner-registry-before')
            before = audit(protected); retained_before = audit([operation / 'original', operation / 'recorded-base'])
            if mode == 'resume-pending': action('inspect', expected=2)
            elif mode == 'slot-pending':
                invoke(retire[:-1] + ['pending-owner:retirement-before-original'], expected=2)
                # The adversary changes registry bytes; only that change is allowed.
                registry = root / 'home/.agents/.skill-lock.json'
                before[str(registry)] = audit([registry])[str(registry)]
            elif mode == 'fallback-advanced':
                invoke(retire); assert not operation.exists()
            else: invoke(retire, expected=2)
            assert audit(protected) == before
            if mode != 'fallback-advanced': assert audit([operation / 'original', operation / 'recorded-base']) == retained_before
        elif mode in ('completed-reverted', 'equal'):
            before = audit(protected); review = json.loads(action('inspect'))
            invoke([proof, '--retained-retirement', str(root), 'retire', id_, review['fingerprint'], review['kind'], 'none'])
            assert not operation.exists() and audit(protected) == before
        else:
            before = audit(protected); private_before = audit([operation])
            for _ in range(2):
                refusal = action('inspect', expected=2)
                assert 'owner transition is still pending' in refusal, refusal
            assert audit(protected) == before and audit([operation]) == private_before
            assert not (operation / 'retiring.json').exists()
            recovered = action('owner-recover-undo' if 'restore' in mode else 'owner-recover-forward')
            assert any(item.get('succeeded') for item in documents(recovered)), recovered
            assert json.loads(action('inspect'))['kind'] == 'discard-undo'
        cases.append(dict(scenario='owner-' + mode, root=str(root), before=before, after=audit(protected), setup=setup))
    return cases


def run(args):
    app = str(pathlib.Path(args.app).resolve())
    proof = str(pathlib.Path(args.proof).resolve())
    cases = []
    scenarios = [(skill, point, 'ordinary') for skill, point in [
        ('ready', 'none'), ('applied', 'none'), ('restored', 'none'),
        ('ready', 'retirement-marker-written'), ('ready', 'retirement-removed-candidate'),
        ('applied', 'retirement-removed-original'), ('ready', 'retirement-before-operation'),
        ('ready', 'retirement-entry-deleted')]]
    scenarios += [('applied', 'none', 'superseded'), ('ready', 'none', 'stale'),
                  ('applied', 'none', 'consumer-registry'), ('ready', 'none', 'missing'),
                  ('applied', 'none', 'missing'), ('applied', 'none', 'alias-plan'),
                  ('applied', 'none', 'alias-original'), ('applied', 'none', 'replaced-preview'),
                  ('applied', 'missing-live:retirement-before-original', 'missing-boundary'),
                  ('applied', 'missing-live:retirement-removed-original', 'missing-boundary')]
    for skill, checkpoint, scenario in scenarios:
        root = pathlib.Path(tempfile.mkdtemp(prefix='skillview-retirement-',
                    dir=os.environ.get('TMPDIR', '/var/tmp') if os.name != 'nt' else None)).resolve()
        root.chmod(0o700)
        (root / 'tmp').mkdir()
        (root / 'disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = isolated_environment(root)
        old_listing = None
        old_inspection = None
        def invoke(command, expected=0):
            result = subprocess.run(command, cwd=root, env=env, text=True, capture_output=True, timeout=60)
            assert result.returncode == expected, (command, result.returncode, result.stdout, result.stderr)
            return result.stdout if expected == 0 else result.stdout + result.stderr
        fixture = invoke([proof, '--retained-retirement-fixture', str(root)])
        plans = [item for item in documents(fixture) if item.get('subject', {}).get('name') == skill]
        plan = plans[-1]
        id_ = plan['id']
        operation = pathlib.Path(plan['planPath']).parent
        def action(mode):
            return invoke([proof, '--retained-retirement', str(root), mode, id_])
        if scenario == 'superseded': action('supersede')
        if scenario == 'stale': action('edit-live')
        if scenario == 'consumer-registry':
            action('add-consumer'); action('registry-advanced')
        if scenario == 'missing': action('missing-live')
        if scenario.startswith('alias-'):
            source = operation / ('plan.json' if scenario == 'alias-plan' else 'original/SKILL.md')
            destination = root / 'custom' / skill / 'shared-native-file'
            os.link(source, destination)
            assert os.path.samefile(source, destination)
        protected = [root / 'custom', root / 'home/.agents/.skill-lock.json', root / ('held-live-' + skill)]
        before = audit(protected)
        ordinary_refusals = []
        if scenario in ('superseded', 'stale', 'consumer-registry'):
            state_before = audit([operation])
            ordinary_refusals.append(invoke([proof, '--retained-retirement', str(root), 'ordinary-inspect', id_], expected=2))
            ordinary_refusals.append(invoke([proof, '--retained-retirement', str(root), 'ordinary-undo', id_, plan['reviewFingerprint']], expected=2))
            assert audit([operation]) == state_before, 'Ordinary obsolete inspection/Undo must not mutate'
        if scenario.startswith('alias-') or scenario == 'missing' and skill == 'applied':
            private_before = audit([operation])
            invoke([proof, '--retained-retirement', str(root), 'inspect', id_], expected=2)
            assert audit([operation]) == private_before and audit(protected) == before
            cases.append(dict(skill=skill, scenario=scenario, checkpoint=checkpoint, refused=True, root=str(root), before=before, after=audit(protected)))
            continue
        history_before = json.loads(invoke([app, 'replacement-history', '--json']))
        review = json.loads(invoke([proof, '--retained-retirement', str(root), 'inspect', id_]))
        if scenario in ('superseded', 'stale'):
            assert review['superseded'], 'Obsolete live copy must be disclosed'
        command = [proof, '--retained-retirement', str(root), 'retire', id_, review['fingerprint'], review['kind'], checkpoint]
        if scenario == 'replaced-preview':
            live = root / 'custom' / skill
            moved = root / ('held-live-' + skill)
            live.rename(moved); shutil.copytree(moved, live)
            changed_before = audit(protected); private_before = audit([operation])
            invoke(command, expected=2)
            assert audit(protected) == changed_before and audit([operation]) == private_before
            cases.append(dict(skill=skill, scenario=scenario, checkpoint=checkpoint, refused=True, root=str(root), before=changed_before, after=audit(protected)))
            continue
        if scenario == 'missing-boundary':
            live_before = audit([root / 'custom' / skill])
            registry_before = audit([root / 'home/.agents/.skill-lock.json'])
            invoke(command, expected=2)
            held = root / 'held-live-applied'
            after_move = audit([held])
            # Root rename changes its own generation. Descendant native objects
            # and bytes must remain the exact objects that were moved by the seam.
            for path, evidence in live_before.items():
                if path != str(root / 'custom' / skill):
                    assert evidence == after_move[path.replace(str(root / 'custom' / skill), str(held), 1)]
            assert audit([root / 'home/.agents/.skill-lock.json']) == registry_before
            assert (operation / 'recorded-base').is_dir() and (operation / 'retiring.json').is_file()
            if checkpoint.endswith('before-original'): assert (operation / 'original').is_dir()
            state_before = audit([operation])
            invoke([proof, '--retained-retirement', str(root), 'inspect', id_], expected=2)
            assert audit([operation]) == state_before
            cases.append(dict(skill=skill, scenario=scenario, checkpoint=checkpoint, refused=True, root=str(root), before=live_before, after=after_move))
            continue
        if checkpoint == 'none':
            invoke(command)
            assert not operation.exists()
        else:
            proc = subprocess.Popen(command, cwd=root, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            lines = queue.Queue()
            def drain():
                for line in proc.stdout:
                    lines.put(line)
            thread = threading.Thread(target=drain, daemon=True)
            thread.start()
            captured = []
            try:
                deadline = time.monotonic() + 45
                while True:
                    line = lines.get(timeout=max(.01, deadline - time.monotonic()))
                    captured.append(line)
                    if line.strip() == 'READY ' + checkpoint:
                        break
                    if proc.poll() is not None:
                        raise AssertionError('Proof exited before checkpoint: ' + ''.join(captured))
                proc.kill()
                proc.wait(timeout=10)
                thread.join(timeout=5)
            finally:
                if proc.poll() is None:
                    proc.kill()
                    proc.wait(timeout=10)
            marker = operation / 'retiring.json'
            assert marker.exists()
            if checkpoint in ('retirement-removed-candidate', 'retirement-removed-original'):
                assert (operation / 'recorded-base').is_dir(), 'Kill must be between distinct payload slots'
            marker_before = audit([marker])
            listing = json.loads(invoke([app, 'replacement-history', '--json']))
            row = next(row for row in listing['entries'] if row['id'] == id_)
            assert row['mayNeedRecovery'] and row['hint'].startswith('Protected')
            assert audit([marker]) == marker_before
            if args.old_app:
                old_before = audit([operation])
                old_listing = json.loads(invoke([str(pathlib.Path(args.old_app).resolve()), 'replacement-history', '--json']))
                old_row = next(row for row in old_listing['entries'] if row['id'] == id_)
                assert old_row['hint'].startswith('Protected') and old_row['mayNeedRecovery']
                empty_bin = root / 'empty-bin'
                empty_bin.mkdir()
                inspection = subprocess.run([str(pathlib.Path(args.old_app).resolve()), '--scan-root', str(root / 'custom'),
                    'restore-inspect', '--id', id_, '--json', '--allow-hidden-dirs'], cwd=root,
                    env={**env, 'PATH': str(empty_bin)}, text=True, capture_output=True, timeout=30)
                assert inspection.returncode != 0
                if checkpoint == 'retirement-marker-written':
                    assert 'Unknown replacement operation entry' in inspection.stdout + inspection.stderr
                old_inspection = dict(code=inspection.returncode, stdout=inspection.stdout, stderr=inspection.stderr)
                assert audit([operation]) == old_before, 'Older reader changed marked native evidence'
            # The original approval must not authorize a resumed topology.
            invoke(command[:-1] + ['none'], expected=2)
            if checkpoint == 'retirement-entry-deleted':
                new_file = operation / 'candidate' / 'unknown-new-content'
                new_file.write_bytes(b'Never delete unrecognized new content\n')
                state_before = audit([operation])
                invoke([proof, '--retained-retirement', str(root), 'inspect', id_], expected=2)
                assert audit([operation]) == state_before
                assert new_file.exists()
            else:
                fresh = json.loads(invoke([proof, '--retained-retirement', str(root), 'inspect', id_]))
                assert fresh['resuming'] and fresh['fingerprint'] != review['fingerprint']
                assert operation.exists(), 'Inspection must never resume automatically'
                invoke([proof, '--retained-retirement', str(root), 'retire', id_, fresh['fingerprint'], fresh['kind'], 'none'])
                assert not operation.exists()
        assert audit(protected) == before, 'Retirement changed live native objects/bytes or owner registry'
        history_after = json.loads(invoke([app, 'replacement-history', '--json']))
        expected_count = history_before['observedCount'] - (0 if checkpoint == 'retirement-entry-deleted' else 1)
        assert history_after['observedCount'] == expected_count
        cases.append(dict(skill=skill, scenario=scenario, checkpoint=checkpoint, root=str(root), before=before,
                          after=audit(protected), historyBefore=history_before, historyAfter=history_after,
                          olderHistory=old_listing, olderInspection=old_inspection, ordinaryRefusals=ordinary_refusals))
    cases.extend(owner_scenarios(app, proof))
    return dict(passed=True, app=app, proof=proof,
                appSha256=hashlib.sha256(pathlib.Path(app).read_bytes()).hexdigest(),
                proofSha256=hashlib.sha256(pathlib.Path(proof).read_bytes()).hexdigest(),
                coreSha256=hashlib.sha256(pathlib.Path(proof).with_name('SkillView.Core.dll').read_bytes()).hexdigest(),
                oldApp=args.old_app,
                oldCoreSha256=hashlib.sha256(pathlib.Path(args.old_app).with_name('SkillView.Core.dll').read_bytes()).hexdigest() if args.old_app else None,
                cases=cases)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--app', required=True)
    parser.add_argument('--proof', required=True)
    parser.add_argument('--old-app')
    parser.add_argument('--output', required=True)
    arguments = parser.parse_args()
    result = run(arguments)
    pathlib.Path(arguments.output).write_text(json.dumps(result, indent=2))
    print(json.dumps(dict(passed=result['passed'], cases=len(result['cases']))))
