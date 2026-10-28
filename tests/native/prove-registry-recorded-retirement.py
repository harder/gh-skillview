"""Dedicated F1/H2 native interoperability. Preserve every fixture and initial failure.

Positive authority comes from actual gh installs, or explicitly supplied immutable
previously verified offline source/registry evidence. Neither mode executes skill
payloads. Offline transport is never reported as actual gh live mutation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import threading

from source_review_proof import acknowledge_frozen_source


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def native(path):
    if not path.exists() and not path.is_symlink():
        return None
    rows = []
    def visit(item):
        s = item.lstat()
        kind = 'link' if stat.S_ISLNK(s.st_mode) else 'directory' if stat.S_ISDIR(s.st_mode) else 'file'
        rows.append(dict(path='/' if item == path else item.relative_to(path).as_posix(), kind=kind,
                         device=s.st_dev, inode=s.st_ino, mode=stat.S_IMODE(s.st_mode), uid=s.st_uid, gid=s.st_gid,
                         mtimeNs=s.st_mtime_ns, ctimeNs=s.st_ctime_ns,
                         value=os.readlink(item) if kind == 'link' else digest(item) if kind == 'file' else None,
                         xattrs={k: os.getxattr(item, k, follow_symlinks=False).hex()
                                 for k in os.listxattr(item, follow_symlinks=False)} if hasattr(os, 'listxattr') else {}))
        if kind == 'directory':
            for child in sorted(item.iterdir()):
                visit(child)
    visit(path)
    return rows


def environment(root, gh):
    env = {k: v for k, v in os.environ.items()
           if k in ('GH_TOKEN', 'GITHUB_TOKEN') or not k.startswith(('GIT_', 'GH_', 'GITHUB_', 'CODEX_', 'CLAUDE_', 'PI_', 'XDG_', 'SKILLVIEW_', 'GEMINI_', 'COPILOT_', 'OPENCODE_'))}
    home = root / 'home'
    env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'), GH_PATH=str(gh),
               GH_EXTENSION='1', GH_PROMPT_DISABLED='1', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1',
               GH_TELEMETRY='false', SKILLVIEW_STATE_HOME=str(root / 'state'),
               XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
               XDG_CACHE_HOME=str(root / 'cache'), XDG_DATA_HOME=str(root / 'data'),
               APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
               CODEX_HOME=str(home / '.codex'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
               CLAUDE_CODE_PLUGIN_CACHE_DIR=str(home / '.claude/plugins'), CLAUDE_CODE_PLUGIN_DIRS='', CLAUDE_CODE_PLUGIN_SEED_DIR='',
               PI_CODING_AGENT_DIR=str(home / '.pi/agent'), GEMINI_CLI_HOME=str(home),
               GIT_DIR=str(root / 'absent-git'), GIT_COMMON_DIR=str(root / 'absent-git'), GIT_WORK_TREE=str(root),
               GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0',
               TMPDIR=str(root / 'tmp'), TMP=str(root / 'tmp'), TEMP=str(root / 'tmp'))
    return env


class Journey:
    def __init__(self, root, app, proof, gh, evidence, damage, pinned=False, expect_metadata_refusal=False):
        self.root, self.app, self.proof, self.gh = root, app, proof, gh
        root.mkdir(mode=0o700)
        for name in ('home', 'state', 'custom', 'tmp', 'receipts'):
            (root / name).mkdir()
        (root / 'disposable-proof-marker').write_text('SkillView disposable process proof\n')
        self.env = environment(root, gh)
        self.env['OFFLINE_GH_LOG'] = str(root / 'offline-gh.jsonl')
        self.sequence = 0
        self.damage = damage
        name = 'cli-exercise' if damage == 'missing-nonempty' else 'gh-skill'
        self.payload = root / 'home/.agents/skills' / name
        self.payload.mkdir(parents=True)
        self.registry = root / 'home/.agents/.skill-lock.json'
        key = 'corroborated' if damage == 'missing-nonempty' else 'pinned' if pinned else 'unpinned'
        shutil.copyfile(evidence / (key + '-registry-original.json'), self.registry)
        raw = (evidence / (key + '-gh-materialized-SKILL.md')).read_bytes()
        if damage == 'corrupt-yaml':
            (self.payload / 'SKILL.md').write_bytes(b'---\nname: [unterminated\n---\n\x00raw damaged\r\n')
        elif damage == 'invalid-metadata':
            text = raw.decode()
            lines = text.splitlines(keepends=True)
            # Keep surviving exact tracking/name; only description is invalid.
            lines = [line for line in lines if not line.startswith('description:')]
            record_url = json.loads(self.registry.read_text())['skills'][name]['sourceUrl']
            for i, line in enumerate(lines):
                if line.lstrip().startswith('github-repo:'):
                    observed_url = line.split('github-repo:', 1)[1].strip()
                    assert observed_url.removesuffix('.git') == record_url.removesuffix('.git')
                    # InvalidMetadata admission requires exact surviving tracking.
                    # Construct that prerequisite in this NEW fixture before any
                    # native receipt; original gh bytes and registry stay saved.
                    lines[i] = line.split('github-repo:', 1)[0] + 'github-repo: ' + record_url + '\n'
            (self.payload / 'SKILL.md').write_text(''.join(lines))
        elif damage == 'missing-nonempty':
            shutil.copyfile(evidence / 'corroborated-resource', self.payload / '.gitignore')
        else:
            assert damage == 'missing-empty'
        self.peer = root / 'home/.agents/skills/zz-unrelated'
        self.peer.mkdir()
        (self.peer / 'SKILL.md').write_text('---\nname: zz-unrelated\ndescription: Unselected peer\n---\nKeep\n')
        (self.peer / 'raw').write_bytes(b'\x00unrelated\xff')
        if os.name != 'nt':
            self.payload.chmod(0o750)
            if (self.payload / 'SKILL.md').exists():
                (self.payload / 'SKILL.md').chmod(0o640)
        self.owner_before, self.peer_before = native(self.registry), native(self.peer)
        self.original_before = native(self.payload)
        self.write('initial-native.json', dict(original=self.original_before, registry=self.owner_before, peer=self.peer_before))
        planned = self.run('registry-restore-plan', '--path', self.payload, ok=not expect_metadata_refusal)
        if expect_metadata_refusal:
            assert 'CAP_SYS_ADMIN' in json.dumps(planned), planned
            self.assert_untouched(self.original_before)
            return
        self.plan = planned['operation']
        self.operation = Path(self.plan['planPath']).parent
        self.write('immutable-plan.json', json.loads(Path(self.plan['planPath']).read_text()))
        assert json.loads(Path(self.plan['planPath']).read_text())['purpose'] == 'restore-registry-recorded'
        assert self.plan['registryRestore']['observedValidity'] == {'missing-empty': 1, 'missing-nonempty': 1, 'corrupt-yaml': 2, 'invalid-metadata': 5}[damage]
        self.assert_untouched(self.original_before)

    def write(self, name, body):
        (self.root / name).write_text(json.dumps(body, indent=2) + '\n')

    def command(self, argv, ok=True, env=None):
        self.sequence += 1
        prefix = self.root / 'receipts' / f'{self.sequence:04d}'
        try:
            p = subprocess.run(list(map(str, argv)), cwd=self.root, env=env or self.env,
                               stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
        except subprocess.TimeoutExpired as ex:
            prefix.with_suffix('.stdout').write_bytes(ex.stdout or b'')
            prefix.with_suffix('.stderr').write_bytes(ex.stderr or b'')
            prefix.with_suffix('.json').write_text(json.dumps(dict(argv=list(map(str, argv)), timeout=180)))
            raise
        prefix.with_suffix('.stdout').write_bytes(p.stdout)
        prefix.with_suffix('.stderr').write_bytes(p.stderr)
        prefix.with_suffix('.json').write_text(json.dumps(dict(argv=list(map(str, argv)), exit=p.returncode)))
        assert (p.returncode == 0) == ok, (argv, p.returncode, p.stdout.decode(errors='replace'), p.stderr.decode(errors='replace'))
        return p

    def run(self, action, *arguments, ok=True):
        return json.loads(self.command([self.app, '--scan-root', self.root / 'custom', action, *arguments,
                                        '--allow-hidden-dirs', '--json'], ok).stdout)

    def assert_untouched(self, live):
        assert native(self.payload) == live
        assert native(self.registry) == self.owner_before
        assert native(self.peer) == self.peer_before

    def apply(self):
        self.run('registry-restore-apply', '--id', self.plan['id'], '--yes', '--disposition', 'retain-original',
                 '--expect', self.plan['planFingerprint'], ok=False)
        acknowledge_frozen_source(self.run, self.plan, 'registry-restore')
        applied = self.run('registry-restore-apply', '--id', self.plan['id'], '--yes', '--disposition', 'retain-original',
                           '--expect', self.plan['planFingerprint'])['operation']
        assert applied['status'] == 'installed'
        retained = native(self.operation / 'original')
        assert len(retained) == len(self.original_before)
        for old, new in zip(self.original_before, retained):
            # Native retain changes the root generation. Record both raw
            # observations; compare all other fields, and every leaf generation.
            assert {k: v for k, v in old.items() if k != 'ctimeNs' or old['path'] != '/'} == {
                k: v for k, v in new.items() if k != 'ctimeNs' or new['path'] != '/'}
        self.write('applied-native.json', dict(live=native(self.payload), retained=retained))
        return applied

    def undo(self):
        inspection = self.run('registry-restore-inspect', '--id', self.plan['id'])['operation']
        restored = self.run('registry-restore-undo', '--id', self.plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        assert restored['status'] == 'restored'

    def inspect(self, ok=True):
        p = self.command([self.proof, '--retained-retirement', self.root, 'inspect', self.plan['id']], ok)
        return json.loads(p.stdout) if ok else None

    def retire(self, review, checkpoint='none', ok=True):
        return self.command([self.proof, '--retained-retirement', self.root, 'retire', self.plan['id'],
                             review['fingerprint'], review['kind'], checkpoint], ok)

    def pause(self):
        review = self.inspect()
        self.retire(review, 'stop:retirement-marker-written', ok=False)
        assert (self.operation / 'retiring.json').is_file()
        return review

    def kill_after(self, review, checkpoint):
        self.sequence += 1
        prefix = self.root / 'receipts' / f'{self.sequence:04d}'
        argv = list(map(str, [self.proof, '--retained-retirement', self.root, 'retire', self.plan['id'],
                              review['fingerprint'], review['kind'], checkpoint]))
        ready = threading.Event()
        with prefix.with_suffix('.stdout').open('wb') as stdout, prefix.with_suffix('.stderr').open('wb') as stderr:
            child = subprocess.Popen(argv, cwd=self.root, env=self.env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=stderr)
            def drain():
                for line in child.stdout:
                    stdout.write(line); stdout.flush()
                    if line.strip() == ('READY ' + checkpoint).encode():
                        ready.set()
            thread = threading.Thread(target=drain)
            thread.start()
            reached = ready.wait(45)
            if child.poll() is None:
                child.kill()
            code = child.wait(timeout=10)
            thread.join(timeout=10)
            prefix.with_suffix('.json').write_text(json.dumps(dict(argv=argv, reached=reached, killed=True, exit=code)))
            assert reached and not thread.is_alive(), 'Native checkpoint was not reached: ' + checkpoint

    def successful_retirement(self, interrupt=False):
        live = native(self.payload)
        review = self.inspect()
        if interrupt:
            first = 'candidate' if review['kind'] == 'abandon-review' else 'original' if (self.operation / 'original').exists() else 'displaced'
            for slot in (first, 'review-installed-candidate', 'review-source-candidate'):
                self.kill_after(review, 'retirement-removed-' + slot)
                assert not (self.operation / slot).exists()
                self.assert_untouched(live)
                review = self.inspect()
                assert review['resuming']
        self.retire(review)
        assert not self.operation.exists()
        self.assert_untouched(live)
        self.write('final-native.json', dict(live=native(self.payload), registry=native(self.registry), peer=native(self.peer)))

    def refuses_without_delete(self, review=None):
        before, live = native(self.operation), native(self.payload)
        self.inspect(ok=False)
        if review is not None:
            self.retire(review, ok=False)
        assert native(self.operation) == before
        self.assert_untouched(live)
        self.write('refusal-native.json', dict(retained=before, live=live, registry=native(self.registry), peer=native(self.peer)))


def prepare_live_evidence(root, gh):
    evidence = root / 'evidence'
    evidence.mkdir()
    for key, pinned, source, name in [('unpinned', False, 'skills/gh-skill', 'gh-skill'),
                                       ('pinned', True, 'skills/gh-skill', 'gh-skill'),
                                       ('corroborated', False, '.github/skills/cli-exercise', 'cli-exercise')]:
        base = root / ('genuine-install-' + key)
        base.mkdir(mode=0o700)
        for directory in ('home', 'tmp'):
            (base / directory).mkdir()
        env = environment(base, gh)
        argv = [str(gh), 'skill', 'install', '--agent', 'universal', '--scope', 'user', '--allow-hidden-dirs']
        if pinned:
            argv.append('--pin=v2.103.2')
        argv += ['--', 'cli/cli', source]
        p = subprocess.run(argv, cwd=base, env=env, stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
        (base / 'install.stdout').write_bytes(p.stdout); (base / 'install.stderr').write_bytes(p.stderr)
        assert p.returncode == 0, (argv, p.returncode, p.stderr)
        installed = base / 'home/.agents/skills' / name
        shutil.copyfile(base / 'home/.agents/.skill-lock.json', evidence / (key + '-registry-original.json'))
        shutil.copyfile(installed / 'SKILL.md', evidence / (key + '-gh-materialized-SKILL.md'))
        if key == 'corroborated':
            shutil.copyfile(installed / '.gitignore', evidence / 'corroborated-resource')
    return evidence


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--app', type=Path, required=True)
    p.add_argument('--proof', type=Path, required=True)
    p.add_argument('--gh', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--offline-evidence', type=Path)
    p.add_argument('--scenario', action='append')
    p.add_argument('--expect-metadata-refusal', choices=['CAP_SYS_ADMIN'])
    a = p.parse_args()
    root = Path(tempfile.mkdtemp(prefix='registry-retirement-')).resolve()
    root.chmod(0o700)
    app, proof, gh = a.app.resolve(), a.proof.resolve(), a.gh.resolve()
    report = dict(root=str(root), scriptSha256=digest(Path(__file__)), ghSha256=digest(gh), offline=bool(a.offline_evidence),
                  actualGhLiveMutation=not bool(a.offline_evidence), cases=[], status='running', bindings={})
    for kind, binary in [('app', app), ('proof', proof)]:
        frozen = root / ('frozen-' + kind)
        before = {str(f.relative_to(binary.parent)): digest(f) for f in binary.parent.rglob('*') if f.is_file()}
        shutil.copytree(binary.parent, frozen)
        assert before == {str(f.relative_to(frozen)): digest(f) for f in frozen.rglob('*') if f.is_file()}
        report['bindings'][kind] = before
        if kind == 'app': app = frozen / binary.name
        else: proof = frozen / binary.name
    a.output.parent.mkdir(parents=True, exist_ok=True)
    def save(): a.output.write_text(json.dumps(report, indent=2) + '\n')
    save()
    try:
        evidence = a.offline_evidence.resolve() if a.offline_evidence else prepare_live_evidence(root, gh)
        if a.offline_evidence:
            manifest = json.loads((evidence / 'evidence-manifest.json').read_text())
            for entry in manifest['files']:
                assert digest(evidence / entry['path']) == entry['sha256'], entry['path']
            report['offlineManifest'] = manifest
        if a.expect_metadata_refusal:
            Journey(root / 'metadata-refusal', app, proof, gh, evidence, 'missing-empty', expect_metadata_refusal=True)
            report['status'] = 'expected-metadata-capability-refusal'
            return
        scenarios = ['abandon-empty', 'abandon-corrupt', 'abandon-invalid', 'abandon-nonempty',
                     'discard-empty', 'discard-corrupt', 'discard-nonempty', 'discard-restored',
                     'resume-ready', 'resume-installed', 'resume-restored', 'context-ready', 'context-installed',
                     'missing-live', 'replace-before', 'replace-after', 'add-file-after', 'chmod-after',
                     'marker-objects', 'marker-malformed-objects', 'marker-identity', 'marker-mode', 'marker-payload', 'marker-children',
                     'empty-candidate', 'empty-displaced', 'empty-archive', 'unknown-slot',
                     'wrong-topology', 'wrong-purpose', 'nonempty-immutable', 'consumer', 'consumer-readable', 'live-alias',
                     'blob', 'manifest', 'metadata', 'surviving-blob', 'surviving-manifest', 'surviving-metadata',
                     'surviving-installed-blob', 'surviving-installed-manifest', 'surviving-installed-metadata']
        for index, scenario in enumerate(a.scenario or scenarios):
            damage = 'corrupt-yaml' if scenario == 'consumer-readable' or scenario.startswith('surviving-installed-') else 'missing-nonempty' if 'nonempty' in scenario and scenario != 'nonempty-immutable' else 'corrupt-yaml' if 'corrupt' in scenario else 'invalid-metadata' if 'invalid' in scenario else 'missing-empty'
            j = Journey(root / scenario, app, proof, gh, evidence, damage, pinned=(index % 2 == 0 or damage == 'invalid-metadata') and damage != 'missing-nonempty')
            case = dict(name=scenario, root=str(j.root), id=j.plan['id'], damage=damage, status='running')
            report['cases'].append(case); save()
            installed = scenario not in ('abandon-empty', 'abandon-corrupt', 'abandon-invalid', 'abandon-nonempty', 'resume-ready', 'context-ready', 'empty-candidate')
            if installed: j.apply()
            if scenario in ('discard-restored', 'resume-restored', 'empty-displaced'): j.undo()
            if scenario.startswith(('abandon-', 'discard-', 'resume-')):
                j.successful_retirement(interrupt=scenario.startswith('resume-'))
            elif scenario.startswith('context-'):
                # A catalog override changes future membership computation while
                # preserving this operation's saved same-address structural data.
                undo_review = j.run('registry-restore-inspect', '--id', j.plan['id'])['operation']['reviewFingerprint'] if installed else None
                j.env['CLAUDE_CONFIG_DIR'] = str(j.root / 'home/.agents')
                j.run('registry-restore-inspect', '--id', j.plan['id'], ok=False)
                if installed:
                    j.run('registry-restore-undo', '--id', j.plan['id'], '--yes', '--expect', undo_review, ok=False)
                j.successful_retirement()
            else:
                after_marker = scenario.endswith('-after') or scenario.startswith('marker-') or scenario.startswith('surviving-') or scenario == 'wrong-topology'
                review = j.pause() if after_marker else j.inspect()
                slot = j.operation / ('candidate' if scenario == 'empty-candidate' else 'displaced' if scenario == 'empty-displaced' else 'original')
                if scenario in ('replace-before', 'replace-after', 'empty-candidate', 'empty-displaced', 'empty-archive'):
                    if scenario == 'empty-archive': slot = j.operation / 'review-installed-candidate'
                    (j.root / 'held-').mkdir()
                    slot.rename(j.root / 'held-' / slot.name)
                    slot.mkdir(mode=0o700)
                elif scenario == 'add-file-after': (slot / 'extra').write_bytes(b'must remain')
                elif scenario == 'chmod-after':
                    if os.name == 'nt':
                        case['limit'] = 'Windows has no Unix chmod; changed-child guard exercised here, with ACL guards retained in existing native proofs'
                        (slot / 'extra').write_bytes(b'Windows equivalent changed native slot')
                    else: slot.chmod(0o755 if stat.S_IMODE(slot.stat().st_mode) != 0o755 else 0o700)
                elif scenario.startswith('marker-') or scenario == 'wrong-topology':
                    path = j.operation / 'retiring.json'; marker = json.loads(path.read_text())
                    item = next(d['evidence'] for d in marker['directories'] if d['name'] == 'original')
                    if scenario == 'marker-objects': item['objects'] = '0' * 64
                    elif scenario == 'marker-malformed-objects': item['objects'] = 'malformed'
                    elif scenario == 'marker-identity': item['identity']['fileIdLow'] += 1
                    elif scenario == 'marker-mode': item['mode'] = 448 if item['mode'] is None else item['mode'] ^ 64
                    elif scenario == 'marker-payload': item['payload'] = '0' * 64
                    elif scenario == 'marker-children': item['children'] = json.loads(Path(j.plan['planPath']).read_text())['candidate']['children'][:1]
                    else: marker['before']['topology'] = 'ready'; marker['kind'] = 'abandon-review'
                    path.write_text(json.dumps(marker))
                elif scenario == 'unknown-slot': (j.operation / 'unknown').mkdir()
                elif scenario == 'missing-live': j.payload.rename(j.root / 'held-live')
                elif scenario in ('consumer', 'consumer-readable'):
                    consumer = j.root / 'custom/consumer'
                    consumer.symlink_to(slot, target_is_directory=True)
                    capture = json.loads(j.command([j.proof, '--retained-retirement', j.root, 'inventory', j.plan['id']]).stdout)
                    actual = j.run('list')
                    if scenario == 'consumer':
                        assert capture['isPartial'] and actual['partial']
                        assert str(consumer) in capture['inaccessiblePaths'] and str(consumer) in actual['inaccessiblePaths']
                        assert any(item['path'] == str(consumer) and item['state'] == 'unsupported' for item in capture['locations'])
                    else:
                        assert not capture['isPartial'] and not actual['partial']
                    j.write('consumer-inventory-proof.json', dict(proofCapture=capture, actualProduct=actual))
                elif scenario == 'live-alias':
                    j.payload.rename(j.root / 'held-live'); j.payload.symlink_to(slot, target_is_directory=True)
                elif scenario in ('wrong-purpose', 'nonempty-immutable'):
                    path = Path(j.plan['planPath']); plan = json.loads(path.read_text())
                    if scenario == 'wrong-purpose': plan['purpose'] = 'restore-recorded'
                    else: plan['original']['entries'] = plan['candidate']['entries'][:1]
                    path.write_text(json.dumps(plan))
                elif scenario in ('blob', 'manifest', 'metadata') or scenario.startswith('surviving-'):
                    archive = j.operation / ('review-installed-candidate' if 'installed-' in scenario else 'review-source-candidate')
                    if scenario.startswith('surviving-'):
                        # Recreate a genuinely approved partial retirement by
                        # killing after original, leaving both archives alive.
                        # This scenario already has a valid written marker.
                        review = j.inspect(); j.kill_after(review, 'retirement-removed-original'); review = j.inspect()
                    files = [f for f in archive.rglob('*') if f.is_file()]
                    manifest = next(f for f in files if f.name == 'manifest.json')
                    if scenario.endswith('manifest'): manifest.write_bytes(manifest.read_bytes() + b' ')
                    elif scenario.endswith('metadata'):
                        value = json.loads(manifest.read_text()); value['rootMetadata']['supported'] = False; manifest.write_text(json.dumps(value))
                    else:
                        target = next(f for f in files if f != manifest and f.name != 'writer.lock')
                        target.write_bytes(target.read_bytes() + b'mutated blob')
                else: raise AssertionError('Unhandled scenario ' + scenario)
                j.refuses_without_delete(review)
            case['status'] = 'passed'; save()
        report['status'] = 'passed'
    except BaseException as ex:
        report['status'] = 'failed'; report['failure'] = repr(ex)
        raise
    finally:
        save()


if __name__ == '__main__':
    main()
