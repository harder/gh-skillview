"""Source-only until separately approved: actual App/gh existing-target M17 proof.

No live GitHub, client/model/skill execution or synthetic installed tracking.
The dependency is the exact tracked, separately reviewed consolidation supervisor;
it supplies bounded process/HTTP/cleanup code, not consolidation product flows.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import stat
import sys
import threading
import time
from types import ModuleType
import uuid

SUPERVISOR_COMMIT = 'bccb7bd810827f81602b1bb6aa9e2b5b388fdb71'
SUPERVISOR_SHA = '6dc5e150149f8d36cc7f0ff0a170e9cd05c9f78aae1313db205feef9e3f761de'
AUTH_PRIMARY_RECEIPT = 'consolidation-auth-primary-source-receipt.json'
AUTH_PRIMARY_RECEIPT_SHA = '3c87c4b3b4ca1a902c11568aba718fb045c57dd577a1360d0071a9f9c10e0725'
PROVENANCE_SHA = '5427f2378b85ada8bdcc48738ec7f5c3ae794a68ad79dc28fa6b11cbda92e07a'
PRIMARY_SHA = '22421f1bad4316f91479e5beaf7e4a24a68c38fe544e785e722a3602a9ec6563'
SCENARIOS = ('selected-tag-shared', 'selected-commit-custom', 'captured-custom',
             'pinned-refusal', 'source-owner-refusal', 'tag-movement')
# Counted direct starts, including gh version/setup, every refused CLI and both
# kill/restart pairs. Executor independently enforces its unchanged32 admission.
START_BUDGETS = {'selected-tag-shared': 22, 'selected-commit-custom': 20,
                 'captured-custom': 14, 'pinned-refusal': 3,
                 'source-owner-refusal': 7, 'tag-movement': 3}
DISPOSITIONS = {'selected': 'selected-revision-retain-original',
                'approved': 'restore-approved-retain-original'}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        while data := stream.read(65536):
            value.update(data)
    return value.hexdigest()


def load_supervisor(path):
    require(path.is_absolute() and path.resolve(strict=True) == path
            and stat.S_ISREG(path.lstat().st_mode) and path.stat().st_size < 1024 * 1024,
            'Exact reviewed bccb7bd supervisor is required; no latest substitution')
    with path.open('rb') as stream:
        source = stream.read(1024 * 1024 + 1)
    require(len(source) <= 1024 * 1024 and hashlib.sha256(source).hexdigest() == SUPERVISOR_SHA,
            'Exact reviewed bccb7bd supervisor is required; no latest substitution')
    module = ModuleType('frozen_bccb7bd_consolidation_supervisor')
    module.__file__ = str(path)
    # Execute the admitted bytes themselves, not a second pathname/pyc read.
    # Only reviewed definitions run; the dependency's main stays guarded.
    exec(compile(source, str(path), 'exec'), module.__dict__)
    require((module.CASE_SECONDS, module.OVERALL_SECONDS, module.MAX_EVENTS, module.MAX_STARTS,
             module.PROCESS_BYTES) == (180, 1800, 200, 32, 1024 * 1024), 'Supervisor bounds changed')
    return module


def verify_inputs(path, expected):
    require(path.is_absolute() and path.resolve(strict=True) == path and stat.S_ISREG(path.lstat().st_mode)
            and path.stat().st_size <= 1024 * 1024, 'Bounded frozen bindings mismatch')
    with path.open('rb') as stream:
        raw = stream.read(1024 * 1024 + 1)
    require(len(raw) <= 1024 * 1024 and hashlib.sha256(raw).hexdigest() == expected,
            'Bounded frozen bindings mismatch')
    value = json.loads(raw.decode('utf-8'))
    require(set(value) == {'product', 'proof', 'git', 'files'}, 'Unexpected runtime binding fields')
    require(isinstance(value['files'], list) and 4 <= len(value['files']) <= 512, 'Runtime input count bound')
    files, total = {}, 0
    for row in value['files']:
        require(set(row) == {'path', 'sha256'}, 'Unexpected runtime file binding')
        name, expected_hash = row['path'], row['sha256']
        require(isinstance(name, str) and Path(name).is_absolute() and name not in files, 'Duplicate/relative runtime input')
        require(isinstance(expected_hash, str) and len(expected_hash) == 64
                and all(c in '0123456789abcdef' for c in expected_hash), 'Invalid runtime hash')
        item = Path(name)
        require(item.resolve(strict=True) == item and stat.S_ISREG(item.lstat().st_mode), 'Noncanonical/nonregular runtime input')
        total += item.stat().st_size
        require(total <= 512 * 1024 * 1024 and digest(item) == expected_hash, 'Runtime byte/hash bound')
        files[name] = expected_hash
    for key in ('product', 'proof'):
        require(isinstance(value[key], list) and 1 <= len(value[key]) <= 2
                and all(isinstance(name, str) and name in files for name in value[key]), 'Bound actual App/proof command required')
    require(Path(value['product'][-1]).name in ('skillview', 'skillview.dll')
            and Path(value['proof'][-1]).name in ('SkillView.MaintenanceProof', 'SkillView.MaintenanceProof.dll'),
            'Bind the actual App and proof outputs; Tests/Proof cannot substitute for the product')
    require(value['git'] in files, 'Bound native Git is required')
    for name in {value['product'][0], value['proof'][0], value['git']}:
        with Path(name).open('rb') as stream:
            require(stream.read(4) in (b'\x7fELF', b'\xcf\xfa\xed\xfe'), 'Script/shim host is not admitted')
    return value


def primary_inputs(provenance, sources):
    for item in (provenance, sources):
        require(item.is_absolute() and item.resolve(strict=True) == item
                and stat.S_ISREG(item.lstat().st_mode) and item.stat().st_size <= 1024 * 1024,
                'Bounded canonical regular primary receipts required')
    with sources.open('rb') as stream:
        raw = stream.read(1024 * 1024 + 1)
    require(len(raw) <= 1024 * 1024 and digest(provenance) == PROVENANCE_SHA
            and hashlib.sha256(raw).hexdigest() == PRIMARY_SHA,
            'Exact retained official-release and primary-source receipts required')
    source_rows = json.loads(raw.decode('utf-8'))
    require(len(source_rows) == 8, 'Retain both gh install/discovery/installer/lockfile sources')
    for row in source_rows:
        item = Path(row['cached'])
        require(item.is_absolute() and item.resolve(strict=True) == item and stat.S_ISREG(item.lstat().st_mode)
                and item.stat().st_size == row['bytes'] <= 1024 * 1024
                and digest(item) == row['sha256'], 'Retained primary source changed')
    return {'ghProvenanceSha256': PROVENANCE_SHA, 'primarySourceBindingsSha256': PRIMARY_SHA,
            'sources': source_rows}


def proof_arguments(command, root, gh, plan, prefix, checkpoint):
    require(prefix in DISPOSITIONS and checkpoint in ('retain-moved', 'install-moved'), 'Unsupported typed pause')
    return [*command, str(root), str(gh), plan['id'], plan['planFingerprint'], 'apply-' + prefix,
            checkpoint, '--yes', '--disposition=' + DISPOSITIONS[prefix]]


def raw_install(gh, destination, *, shared=False, pinned=False, repo='fixture/source', force=False):
    args = [str(gh), 'skill', 'install']
    args += ['--agent', 'universal', '--scope', 'user'] if shared else ['--dir', str(destination)]
    if pinned:
        args += ['--pin=v1']
    if force:
        args += ['--force']
    return [*args, '--', repo, 'demo']


def cloned_values(rows):
    """Clone comparison: bytes/types/mode/uid/gid and actually observed xattrs.

    Existing metadata policy explicitly regenerates clone identities/times;
    exact original objects/times are compared separately on retain/undo.
    Native product metadata admission stays required.
    """
    return [{key: row[key] for key in ('path', 'bytes', 'sha256', 'target', 'observedXattrs', 'xattrObservationError') if key in row}
            | {'native': {key: row['native'][key] for key in ('mode', 'uid', 'gid')}} for row in rows]


def repositories(helper, moving=False):
    class SelectedRepository(helper.Repository):
        def __init__(self, versions):
            super().__init__(versions)
            self.selected_tag_reads = 0
            self.blocked = False

        def route(self, path, query):
            if self.blocked:
                return None  # Any post-freeze API request is a fatal fixture error.
            commits = {repo.commit for repo in self.versions.values()}
            if not query and path.startswith('git/ref/') and path[len('git/ref/'):].split('/', 1)[0] in ('heads', 'tags'):
                kind, identifier = path[len('git/ref/'):].split('/', 1)
                if identifier in commits:
                    return 404, {'message': 'Not Found'}  # Actual gh's branch→tag→full-commit probes only.
            if path == 'git/ref/tags/v2' and not query and 'v2' in self.versions:
                self.selected_tag_reads += 1
                if moving and self.selected_tag_reads >= 3:
                    return 200, {'ref': 'refs/tags/v2', 'object': {'sha': self.versions['v1'].commit, 'type': 'commit'}}
            return super().route(path, query)

    result = {name: SelectedRepository(repo.versions) for name, repo in helper.repositories().items()}
    return result


class Case:
    def __init__(self, helper, root, output, bindings, env, executor, repos, scenario):
        self.h, self.root, self.output, self.bindings = helper, root, output, bindings
        self.env, self.executor, self.repos, self.scenario = env, executor, repos, scenario
        self.shared = scenario in ('selected-tag-shared', 'captured-custom', 'pinned-refusal', 'source-owner-refusal')
        self.path = root / ('home-0/.agents/skills/demo' if self.shared else 'custom/demo')
        self.registry = root / 'home-0/.agents/.skill-lock.json'
        self.original_registry = None
        self.references = []

    def cli(self, command, *args, ok=True, contains=None):
        code, text, stderr = self.executor.run([*self.bindings['product'], '--scan-root', str(self.root / 'custom'),
            command, *map(str, args), '--allow-hidden-dirs', '--json'], self.env, self.root)
        try:
            value = json.loads(text)
        except json.JSONDecodeError as error:
            raise RuntimeError('Actual App did not return a JSON report: ' + stderr) from error
        require(isinstance(value, dict) and (code == 0) == ok, 'Unexpected actual CLI result: ' + str(value))
        if ok:
            require(value.get('status') != 'refused' and not value.get('partial', False), 'Incomplete actual CLI result: ' + str(value))
        else:
            require(value.get('status') == 'refused' and contains and contains in (value.get('reason') or value.get('error') or ''),
                    'Refusal did not reach its exact expected boundary: ' + str(value))
        return value

    def observed(self):
        return self.h.snapshot(self.path), self.h.registry_evidence(self.registry)

    def private_state(self, name):
        path = self.root / 'state/SkillView' / name
        try:
            observed = path.lstat()
        except FileNotFoundError:
            return None
        require(stat.S_ISDIR(observed.st_mode), 'Unexpected private state object')
        return self.h.snapshot(path)

    def unchanged(self, before):
        require(self.observed() == before, 'Refused/observation operation changed installed bytes/native objects or actual owner file')

    def setup(self, version):
        for relative in ('home-0', 'state', 'tmp', 'gh-config', 'empty-ca', 'dotnet-home', 'system', 'custom'):
            (self.root / relative).mkdir(mode=0o700, parents=True, exist_ok=True)
        (self.root / 'empty-git-config').write_bytes(b'')
        (self.root / 'disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        (self.root / 'disposable-proof-marker').chmod(0o600)
        code, text, _ = self.executor.run([self.env['GH_PATH'], '--version'], self.env, self.root)
        require(code == 0 and text.startswith('gh version ' + version + ' '), 'Actual retained gh version differs')
        self.h.save(self.output / 'observed-gh-version.json', {'report': text})
        code, _, stderr = self.executor.run(raw_install(self.env['GH_PATH'], self.root / 'custom',
            shared=self.shared, pinned=self.scenario == 'pinned-refusal'), self.env, self.root)
        require(code == 0, 'Actual gh setup failed, no retry/repair allowed: ' + stderr)
        self.h.check_installed(self.path, 'fixture/source', self.repos['fixture/source'].versions['v1'],
                               pinned=self.scenario == 'pinned-refusal')
        self.original_registry = self.h.registry_evidence(self.registry)
        require(self.original_registry is not None, 'Every actual --dir install also writes its real HOME global record')
        registry = json.loads(self.registry.read_bytes())
        entry = registry['skills']['demo']
        require(registry['version'] == 3 and set(registry['skills']) == {'demo'}
                and entry['source'] == 'fixture/source' and entry['sourceType'] == 'github'
                and entry['skillFolderHash'] == self.repos['fixture/source'].versions['v1'].directories['skills/demo']
                and entry.get('pinnedRef') == ('v1' if self.scenario == 'pinned-refusal' else None),
                'Actual global record differs from independent initial source/pin')
        self.initial = self.observed()
        self.h.save(self.output / 'actual-initial.json', {'payload': self.initial[0], 'globalRecord': self.initial[1],
            'environment': self.env, 'shared': self.shared, 'tracking': self.h.tracking(self.path / 'SKILL.md')[0]})

    def reference(self, result, schema, purpose):
        plan = result['operation']
        require(plan['subject']['installationId'] and plan['originalPath'] == str(self.path), 'Wrong exact installed row')
        saved = json.loads(Path(plan['planPath']).read_text())
        require(saved['schemaVersion'] == schema and saved['purpose'] == purpose and saved['sourceReview'], 'Wrong actual producer schema/purpose/source contract')
        reference = self.root / ('reference-' + plan['id'] + '.json')
        reference.write_text(json.dumps(result) + '\n')
        self.references.append((reference, reference.read_bytes(), Path(plan['planPath']), Path(plan['planPath']).read_bytes()))
        require(saved['reviewFingerprint'] == plan['planFingerprint'], 'Plan/reference hash differs')
        return plan, reference, saved

    def acknowledge(self, prefix, plan):
        report = plan['sourceReview']
        self.h.save(self.output / ('source-declaration-' + plan['id'] + '.json'), {
            'report': report, 'declaration': 'Explicit inert-fixture source declaration only; no authenticity/safety/session verdict.'})
        before = self.observed()
        result = self.cli(prefix + '-ack-source', '--id', plan['id'], '--expect', plan['planFingerprint'],
                          '--review', report['fingerprint'], '--yes')
        require(result['operation']['sourceReviewAcknowledged'] and result['diskVerification'] == 'not-performed; source-review acknowledgement only', 'Source receipt was not independent')
        self.unchanged(before)

    def apply(self, plan, reference, prefix, checkpoint=None):
        if checkpoint is None:
            return self.cli('apply', '--plan-file', reference, '--expect', plan['planFingerprint'], '--yes',
                            '--disposition', DISPOSITIONS[prefix])['operation']
        before = self.h.snapshot(self.path)
        candidate = self.h.snapshot(Path(plan['candidatePath']))
        owner_before = self.h.registry_evidence(self.registry)
        self.executor.run(proof_arguments(self.bindings['proof'], self.root, self.env['GH_PATH'], plan, prefix, checkpoint),
                          self.env, self.root, pause=checkpoint)
        require((checkpoint == 'install-moved') == os.path.lexists(self.path), 'Killed native position differs from selected durable checkpoint')
        require(Path(plan['retainedPath']).is_dir(), 'Killed apply did not retain the exact original')
        retained = self.h.snapshot(Path(plan['retainedPath']))
        require(self.h.stable(retained, renamed=True) == self.h.stable(before, renamed=True),
                'Killed boundary lost exact original native objects/bytes/metadata')
        if checkpoint == 'install-moved':
            require(self.h.stable(self.h.snapshot(self.path), renamed=True) == self.h.stable(candidate, renamed=True),
                    'Killed install did not publish its exact previously captured candidate object')
        require(self.h.registry_evidence(self.registry) == owner_before, 'Retain/install checkpoint unexpectedly wrote an owner record')
        self.h.save(self.output / ('killed-native-' + plan['id'] + '.json'), {
            'checkpoint': checkpoint, 'before': before, 'candidate': candidate, 'retained': retained,
            'installed': self.h.snapshot(self.path) if os.path.lexists(self.path) else None, 'ownerBefore': owner_before})
        inspected = self.cli('inspect', '--plan-file', reference)['operation']
        result = self.cli('repair', '--plan-file', reference, '--expect', inspected['reviewFingerprint'], '--yes')['operation']
        require(result['status'] == 'installed', 'Fresh actual CLI recovery did not reconcile installation/owner')
        return result

    def undo(self, reference, before, registry_bytes):
        inspected = self.cli('inspect', '--plan-file', reference)['operation']
        result = self.cli('undo', '--plan-file', reference, '--expect', inspected['reviewFingerprint'], '--yes')['operation']
        require(result['status'] == 'restored', 'Actual CLI undo did not reconcile original')
        require(self.h.stable(self.h.snapshot(self.path), renamed=True) == self.h.stable(before, renamed=True),
                'Undo lost original native objects/bytes/modes/uid/gid/observed attributes')
        actual = self.h.registry_evidence(self.registry)
        require(actual['rawBase64'] == registry_bytes['rawBase64'], 'Undo did not restore the exact actual global record bytes')
        require(cloned_values(actual['observation']) == cloned_values(registry_bytes['observation']),
                'Undo changed owner-file mode/uid/gid or observed attributes')
        for key in ('device', 'inode', 'mode', 'uid', 'gid'):
            require(actual['observation'][0]['native'][key] == registry_bytes['observation'][0]['native'][key], 'Owner data-file object/metadata changed')

    def capture(self, selected_reference=None):
        before = self.observed()
        preview = self.cli('approval-inspect', '--path', self.path)
        args = ['--path', self.path, '--expect-payload', preview['payloadFingerprint'], '--yes']
        if selected_reference is not None:
            args += ['--selected-plan-file', selected_reference]
        capture = self.cli('approval-capture', *args)
        require(not capture['desiredPolicyChanged'] and not capture['payloadMutation'], 'Private capture implied desired intent')
        require(capture['approval']['kind'] == ('verified-selected-result' if selected_reference else 'captured-custom'), 'Captured provenance was relabeled')
        self.unchanged(before)
        approval = capture['approvalReference']
        inspected = self.cli('approval-inspect', '--path', self.path, '--id', approval['id'], '--expect', approval['fingerprint'])
        return approval, inspected['policyFingerprint']

    def policy(self, approval, expected, negative=False):
        args = ['--path', self.path, '--mode', 'approved-immutable', '--approval-id', approval['id'],
                '--approval-expect', approval['fingerprint'], '--expect', expected]
        before = self.observed()
        if negative:
            state = self.private_state('desired-state')
            self.cli('policy-set', *args, ok=False, contains='requires --yes --expect')
            require(self.private_state('desired-state') == state, 'Missing independent policy consent changed private intent')
        result = self.cli('policy-set', *args, '--yes')
        require(result['desiredPolicy']['approvalId'] == approval['id'] and result['desiredPolicy']['mode'] == 'approved-immutable', 'Desired consent was not bound to the archive')
        self.unchanged(before)

    def approved(self, approval):
        result = self.cli('plan', '--purpose', 'restore-approved', '--path', self.path,
            '--approval-id', approval['id'], '--approval-expect', approval['fingerprint'])
        return self.reference(result, 8, 'restore-approved')

    def missing_source(self, plan, reference, prefix, full=False):
        before = self.observed()
        self.cli('apply', '--plan-file', reference, '--expect', plan['planFingerprint'], '--yes',
                 '--disposition', DISPOSITIONS[prefix], ok=False, contains='source-review acknowledgement')
        if full:
            current = self.cli('inspect', '--plan-file', reference)['operation']
            self.cli(prefix + '-recover', '--id', plan['id'], '--expect', current['reviewFingerprint'], '--yes',
                     ok=False, contains='source-review acknowledgement')
            self.cli(prefix + '-ack-source', '--id', plan['id'], '--expect', plan['planFingerprint'], '--review', '0' * 64,
                     '--yes', ok=False, contains='review')
            self.cli('approved-ack-source' if prefix == 'selected' else 'selected-ack-source', '--id', plan['id'],
                     '--expect', plan['planFingerprint'], '--review', plan['sourceReview']['fingerprint'],
                     '--yes', ok=False, contains='purpose')
        require(not os.path.lexists(plan['retainedPath']), 'Missing receipt populated the retained slot')
        self.unchanged(before)

    def selected(self):
        full = self.scenario == 'selected-tag-shared'
        original = self.h.snapshot(self.path)
        if full:
            self.cli('plan', '--purpose', 'selected-revision', '--path', self.path, '--revision-kind', 'commit',
                     '--revision', 'abcd', ok=False, contains='40-character')
        kind = 'tag' if full else 'commit'
        selected = self.repos['fixture/source'].versions['v2']
        result = self.cli('plan', '--purpose', 'selected-revision', '--path', self.path, '--revision-kind', kind,
            '--revision', 'v2' if full else selected.commit, *(['--pin'] if full else []))
        plan, reference, saved = self.reference(result, 7, 'selected-revision')
        self.unchanged(self.initial)
        witness = saved['selection']['provider']
        require(witness['selection']['commit'] == selected.commit and witness['selection']['tree'] == selected.directories['skills/demo'], 'Typed identity differs from independent Git source')
        require(witness['selection']['ref'] == ('refs/tags/v2' if full else None), 'Invented symbolic commit ref')
        require(witness['binarySha256'] == self.h.TOOLS[self.executor.output.name.split('-', 1)[0]], 'Provider binary witness differs')
        require(witness['arguments'][4] == '--pin=' + ('v2' if full else selected.commit)
                and witness['arguments'][5:] == ['--', 'fixture/source', 'skills/demo'], 'Actual private provider argv differs')
        require(bool(saved['revision'].get('owner')) == self.shared, 'Actual registry was assigned to the wrong physical target')
        self.h.check_installed(Path(plan['planPath']).parent / 'recorded-base', 'fixture/source', self.repos['fixture/source'].versions['v1'])
        # Source transport is no longer available after immutable capture; keep
        # identical environment so a changed proxy cannot invalidate the context.
        requests = self.executor.server.total_events
        for repo in self.repos.values():
            repo.blocked = True
        if full:
            preview = self.cli('approval-inspect', '--path', self.path)
            state = self.private_state('approved-payloads')
            self.cli('approval-capture', '--path', self.path, '--expect-payload', preview['payloadFingerprint'],
                     ok=False, contains='Capture requires separate --yes')
            require(self.private_state('approved-payloads') == state, 'Missing capture consent wrote private approval state')
            self.cli('approval-capture', '--path', self.path, '--expect-payload', preview['payloadFingerprint'], '--yes',
                     '--selected-plan-file', reference, ok=False, contains='completed')
            partial = self.private_state('approved-payloads')
            require(partial is not None and not any(row['path'].split('/')[-1] == 'approved.json' for row in partial),
                    'Ready selected preparation was published as a completed approved result')
            self.h.save(self.output / 'incomplete-selected-approval-attempt.json', partial)
        self.missing_source(plan, reference, 'selected', full=full)
        self.acknowledge('selected', plan)
        if full:
            self.cli('apply', '--plan-file', reference, '--expect', plan['planFingerprint'], '--yes',
                     '--disposition', DISPOSITIONS['approved'], ok=False, contains='disposition')
        self.apply(plan, reference, 'selected', 'retain-moved' if full else 'install-moved')
        require('Later custom' not in (self.path / 'SKILL.md').read_text(), 'Wrong selected payload')
        actual_tracking, _ = self.h.tracking(self.path / 'SKILL.md')
        require(actual_tracking['github-tree-sha'] == selected.directories['skills/demo']
                and actual_tracking.get('github-pinned', '') == ('v2' if full else ''), 'Selected actual pin/tree differs')
        _, actual_body = self.h.tracking(self.path / 'SKILL.md')
        require(actual_body == selected.files['skills/demo/SKILL.md'].split(b'\n---\n', 1)[1].decode(),
                'Selected authored body differs from immutable source')
        for name, data in selected.files.items():
            if name != 'skills/demo/SKILL.md':
                require((self.path / name[len('skills/demo/'):]).read_bytes() == data,
                        'Selected resource differs from independently computed Git blob')
        approved_bytes = self.h.snapshot(self.path)
        approved, policy_fingerprint = self.capture(reference)
        if full:
            # No later transaction may silently refresh this plan's old native
            # generations. Prove schema7's exact owner/native undo now.
            self.undo(reference, original, self.original_registry)
            require(self.executor.server.total_events == requests, 'Schema7 saved lifecycle revisited source/API')
            return
        self.policy(approved, policy_fingerprint, negative=full)
        # User-authored change to current I, no tracking/owner/header synthesis.
        with (self.path / 'SKILL.md').open('ab') as stream:
            stream.write(b'\nLater custom instructions; same authored version.\n')
        (self.path / 'later-only').write_bytes(b'\x00\xffretain exactly on schema8 undo')
        before_restore, owner_before_restore = self.observed()
        require(next(row['sha256'] for row in before_restore if row['path'] == 'SKILL.md')
                != next(row['sha256'] for row in approved_bytes if row['path'] == 'SKILL.md'),
                'Schema8 fixture did not create genuinely different current instruction bytes')
        restored, restored_ref, _ = self.approved(approved)
        self.missing_source(restored, restored_ref, 'approved')
        self.acknowledge('approved', restored)
        self.apply(restored, restored_ref, 'approved', 'install-moved' if full else 'retain-moved')
        require(self.h.stable(self.h.snapshot(self.path), renamed=True) != self.h.stable(before_restore, renamed=True), 'Approved restore did not change customized I')
        require(cloned_values(self.h.snapshot(self.path)) == cloned_values(approved_bytes),
                'Approved bytes/modes/owner/observed attributes differ from completed selected capture')
        store = Path(approved['storeAddress'])
        store.rename(self.root / 'completed-approvals-unavailable')
        self.undo(restored_ref, before_restore, owner_before_restore)
        require(self.executor.server.total_events == requests, 'Saved selected/approved lifecycle revisited the controlled source/API')

    def custom(self):
        with (self.path / 'SKILL.md').open('ab') as stream:
            stream.write(b'\nLocally approved custom instructions; version retained.\n')
        (self.path / 'custom.bin').write_bytes(b'\x00\xff\x07 custom approval')
        self.path.joinpath('custom-link').symlink_to('custom.bin')
        approved_snapshot = self.h.snapshot(self.path)
        approved, expected = self.capture()
        requests = self.executor.server.total_events
        for repo in self.repos.values():
            repo.blocked = True
        self.policy(approved, expected, negative=True)
        with (self.path / 'SKILL.md').open('ab') as stream:
            stream.write(b'\nLater installed body, identical tracking and authored version.\n')
        before, owner = self.observed()
        require(next(row['sha256'] for row in before if row['path'] == 'SKILL.md')
                != next(row['sha256'] for row in approved_snapshot if row['path'] == 'SKILL.md'),
                'Custom restore fixture cannot pass as identical/no-op instructions')
        plan, reference, _ = self.approved(approved)
        self.missing_source(plan, reference, 'approved')
        self.acknowledge('approved', plan)
        # The legitimate receipt is made unavailable, never rewritten or forged.
        receipt = Path(plan['planPath']).parent / 'desired-policy.json'
        held = self.root / 'original-policy-receipt-unavailable.json'
        receipt.rename(held)
        self.cli('apply', '--plan-file', reference, '--expect', plan['planFingerprint'], '--yes',
                 '--disposition', DISPOSITIONS['approved'], ok=False, contains='desired-policy receipt')
        self.unchanged((before, owner)); held.rename(receipt)
        self.apply(plan, reference, 'approved')
        require(cloned_values(self.h.snapshot(self.path)) == cloned_values(approved_snapshot),
                'Custom I bytes/modes/owner/observed attributes were relabeled or lost')
        Path(approved['storeAddress']).rename(self.root / 'custom-approval-unavailable')
        self.undo(reference, before, owner)
        require(self.executor.server.total_events == requests, 'Custom approved lifecycle contacted controlled source/API')

    def negative(self):
        before = self.observed()
        requests = self.executor.server.total_events
        if self.scenario == 'pinned-refusal':
            self.cli('plan', '--purpose', 'selected-revision', '--path', self.path, '--revision-kind', 'tag',
                     '--revision', 'v2', ok=False, contains='cannot implicitly clear an installed pin')
        elif self.scenario == 'tag-movement':
            self.cli('plan', '--purpose', 'selected-revision', '--path', self.path, '--revision-kind', 'tag',
                     '--revision', 'v2', '--pin', ok=False, contains='selected revision moved during capture')
            require(self.repos['fixture/source'].selected_tag_reads == 3, 'Tag did not move at the final observed recheck')
        else:
            approved, _ = self.capture()
            code, _, _ = self.executor.run(raw_install(self.env['GH_PATH'], self.root / 'custom', shared=True,
                repo='different-author/source', force=True), self.env, self.root)
            require(code == 0, 'Actual different-author source/owner setup failed')
            self.h.check_installed(self.path, 'different-author/source', self.repos['different-author/source'].versions['v1'])
            before = self.observed()
            requests = self.executor.server.total_events
            self.cli('plan', '--purpose', 'restore-approved', '--path', self.path, '--approval-id', approved['id'],
                     '--approval-expect', approved['fingerprint'], ok=False, contains='Approval source, installation, owner namespace')
        self.unchanged(before)
        if self.scenario != 'tag-movement':
            require(self.executor.server.total_events == requests, 'Pin/source admission refusal unexpectedly resolved remote source')
        state = self.root / 'state/SkillView/replacements'
        try:
            observed = state.lstat()
        except FileNotFoundError:
            rows = []  # Only definite missing, never access/I/O as absent.
        else:
            require(stat.S_ISDIR(observed.st_mode), 'Unexpected replacement state object after refusal')
            rows = self.h.snapshot(state)
        require(not any(row['path'].split('/')[-1] == 'plan.json' for row in rows), 'Refused preparation published a saved plan')
        self.h.save(self.output / 'refused-private-replacement-state.json', rows)

    def finish(self):
        for reference, expected, plan, expected_plan in self.references:
            require(reference.read_bytes() == expected and plan.read_bytes() == expected_plan, 'Immutable plan/reference bytes changed across processes')
        self.h.save(self.output / 'after.json', {'payload': self.h.snapshot(self.path),
            'globalRecord': self.h.registry_evidence(self.registry), 'directStarts': self.executor.starts})
        require(self.executor.starts == START_BUDGETS[self.scenario], 'Scenario differs from its reviewed complete direct-start count')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute-root-approved-existing-target-proof', action='store_true')
    parser.add_argument('--supervisor', required=True, type=Path)
    parser.add_argument('--bindings', required=True, type=Path)
    parser.add_argument('--bindings-sha256', required=True)
    parser.add_argument('--gh-provenance', required=True, type=Path)
    parser.add_argument('--primary-source-bindings', required=True, type=Path)
    parser.add_argument('--certificate', required=True, type=Path)
    parser.add_argument('--private-key', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    require(args.execute_root_approved_existing_target_proof, 'Source only: separate root-reviewed runtime approval required')
    require(sys.version_info >= (3, 11), 'Reviewed cleanup uses Python3.11+ sys.exception; no older-runtime fallback')
    require(os.name == 'posix' and hasattr(signal, 'pthread_sigmask'), 'POSIX process-group supervisor required; Windows remains a separate native proof obligation')
    require(os.uname().sysname == 'Linux' and os.uname().machine == 'aarch64',
            'This exact official tool packet is Linux ARM64 only; other native lanes require separately reviewed inputs')
    h = load_supervisor(args.supervisor)
    bindings = verify_inputs(args.bindings, args.bindings_sha256)
    sources = primary_inputs(args.gh_provenance, args.primary_source_bindings)
    for item in (args.certificate, args.private_key):
        require(item.is_absolute() and item.resolve(strict=True) == item
                and stat.S_ISREG(item.lstat().st_mode), 'External canonical native regular CA files required')
    require(digest(args.certificate) == h.CA_SHA, 'Exact reviewed external CA required')
    require(str(args.private_key) not in {row['path'] for row in bindings['files']}, 'Never freeze/copy private CA material')
    key = args.private_key.stat()
    require(all((Path(row['path']).stat().st_dev, Path(row['path']).stat().st_ino) != (key.st_dev, key.st_ino)
                for row in bindings['files']), 'Never bind an alias/hardlink of private CA material')
    output = args.output
    require(output.is_absolute() and output.parent.resolve(strict=True) == output.parent
            and not os.path.lexists(output), 'Fresh canonical private output required; no overwrite/retry')
    output.mkdir(mode=0o700)
    report = {'status': 'running', 'privateProductSourceBase': 'c64034209119abd35e63b70ed51e584768178381',
        'scriptSha256': digest(Path(__file__)), 'supervisorCommit': SUPERVISOR_COMMIT,
        'supervisorSha256': SUPERVISOR_SHA, 'bindingsSha256': args.bindings_sha256,
        'authPrimaryProvenance': {'trackedReceipt': AUTH_PRIMARY_RECEIPT, 'sha256': AUTH_PRIMARY_RECEIPT_SHA,
            'role': 'Source-review provenance only; historical capture paths are not runtime inputs.'},
        'primaryEvidence': sources, 'cases': [], 'nativePositivePolicy': 'all required; no automatic metadata skip',
        'limits': {'freshCases': 12, 'caseSeconds': 180, 'overallSeconds': 1800, 'events': 200,
                   'directStarts': 32, 'combinedProcessBytes': 1048576},
        'qualifications': ['Controlled inert Git source using actual gh binaries, not live GitHub/authentication.',
            'Actual App CLI across fresh processes; Proof host only pauses existing typed native apply boundaries.',
            'No client/model/skill execution, runtime activation, publisher authenticity or full M17 acceptance.',
            'Python observed attributes do not establish complete native metadata authority.',
            'Existing targets only; no missing-root/profile/new-owner/new-exposure publication.']}
    previous = {kind: signal.getsignal(kind) for kind in h.OWN_SIGNALS}
    for kind in previous:
        signal.signal(kind, h.interrupt)
    overall = time.monotonic() + h.OVERALL_SECONDS
    protected = h.protected_tmp_git()
    try:
        for version, expected in h.TOOLS.items():
            gh = Path('/tmp/skillview-unpin-tools/extracted') / version / 'gh'
            require(stat.S_ISREG(gh.lstat().st_mode) and digest(gh) == expected, 'Exact official retained gh binary required')
            for scenario in SCENARIOS:
                deadline = min(overall, time.monotonic() + h.CASE_SECONDS)
                require(time.monotonic() < deadline, 'Whole proof deadline exhausted')
                signal.setitimer(signal.ITIMER_REAL, max(.001, deadline - time.monotonic()))
                verify_inputs(args.bindings, args.bindings_sha256)
                primary_inputs(args.gh_provenance, args.primary_source_bindings)
                require(digest(args.supervisor) == SUPERVISOR_SHA, 'Frozen supervisor changed')
                case_output = output / (version + '-' + scenario); case_output.mkdir()
                root = Path('/var/tmp').resolve(strict=True) / ('skillview-desired-proof-' + uuid.uuid4().hex)
                root.mkdir(mode=0o700)
                repos = repositories(h, moving=scenario == 'tag-movement')
                h.save(case_output / 'independent-git-objects.json', {name: repo.evidence() for name, repo in repos.items()})
                server = h.Server(repos, case_output / 'requests.json', deadline, args.certificate, args.private_key)
                thread = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .02}, daemon=False)
                executor = h.Executor(case_output, deadline, server)
                env = h.child_env(root, gh, 'http://127.0.0.1:' + str(server.server_address[1]), args.certificate)
                env['PATH'] = str(gh.parent) + ':' + str(Path(bindings['git']).parent) + ':/usr/bin:/bin'
                env['GIT_CEILING_DIRECTORIES'] = str(root)  # Never discover an ambient ancestor repository.
                case_report = {'version': version, 'scenario': scenario, 'fixture': str(root), 'status': 'running'}
                report['cases'].append(case_report)
                try:
                    mask = signal.pthread_sigmask(signal.SIG_BLOCK, h.OWN_SIGNALS)
                    try:
                        thread.start()
                    finally:
                        signal.pthread_sigmask(signal.SIG_SETMASK, mask)
                    case = Case(h, root, case_output, bindings, env, executor, repos, scenario)
                    case.setup(version)
                    if scenario.startswith('selected-'):
                        case.selected()
                    elif scenario == 'captured-custom':
                        case.custom()
                    else:
                        case.negative()
                    case.finish()
                    require(not server.failures and h.protected_tmp_git() == protected, 'REST/protected sentinel changed')
                    verify_inputs(args.bindings, args.bindings_sha256)
                    primary_inputs(args.gh_provenance, args.primary_source_bindings)
                    require(digest(gh) == expected and digest(args.certificate) == h.CA_SHA, 'Retained tool/CA changed')
                    case_report['status'] = 'passed-required-native-case'
                except BaseException as error:
                    case_report.update(status='stopped-no-retry', failure=type(error).__name__ + ': ' + str(error))
                    raise
                finally:
                    h.finish_case(server, thread, executor, case_report, case_output, output, report)
        require(len(report['cases']) == 12 and all(case['status'] == 'passed-required-native-case' for case in report['cases']), 'All12 native cases are required')
        report['status'] = 'passed-twelve-existing-target-native-cases'
    except BaseException as error:
        report.update(status='stopped-no-retry', failure=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        for kind, handler in previous.items():
            signal.signal(kind, handler)
        report['protectedTmpGitUnchanged'] = h.protected_tmp_git() == protected
        h.save(output / 'summary.json', report)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
