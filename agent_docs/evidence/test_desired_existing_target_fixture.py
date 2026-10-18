"""Pure argv/routing/immutable-input protocols; no subprocess or server start."""
import contextlib
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('desired_existing_target_fixture',
    Path(__file__).with_name('prove-desired-existing-target.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class DesiredProtocolTests(unittest.TestCase):
    def test_raw_custom_and_shared_setup_keep_real_home_and_trusted_flags(self):
        custom = proof.raw_install('/bound/gh', Path('/private/custom'))
        self.assertEqual(['/bound/gh', 'skill', 'install', '--dir', '/private/custom', '--', 'fixture/source', 'demo'], custom)
        shared = proof.raw_install('/bound/gh', Path('/unused'), shared=True, pinned=True)
        self.assertEqual(['/bound/gh', 'skill', 'install', '--agent', 'universal', '--scope', 'user',
                          '--pin=v1', '--', 'fixture/source', 'demo'], shared)
        changed = proof.raw_install('/bound/gh', Path('/unused'), shared=True,
                                    repo='different-author/source', force=True)
        self.assertIn('--force', changed[:changed.index('--')])
        self.assertEqual(['different-author/source', 'demo'], changed[changed.index('--') + 1:])
        self.assertNotIn('HOME', custom + shared + changed)  # No fake --home/provider address.

    def test_typed_pause_carries_exact_saved_identity_and_explicit_retention(self):
        plan = {'id': 'a' * 32, 'planFingerprint': 'b' * 64}
        for prefix in ('selected', 'approved'):
            for checkpoint in ('retain-moved', 'install-moved'):
                argv = proof.proof_arguments(['/bound/dotnet', '/bound/SkillView.MaintenanceProof.dll'],
                    Path('/private'), '/bound/gh', plan, prefix, checkpoint)
                self.assertEqual(['/bound/dotnet', '/bound/SkillView.MaintenanceProof.dll', '/private', '/bound/gh',
                    'a' * 32, 'b' * 64, 'apply-' + prefix, checkpoint, '--yes',
                    '--disposition=' + proof.DISPOSITIONS[prefix]], argv)
        for prefix, checkpoint in (('update', 'retain-moved'), ('approved', 'owner-written'), ('selected', 'workflow')):
            with self.assertRaises(RuntimeError):
                proof.proof_arguments(['/bound/proof'], Path('/private'), '/bound/gh', plan, prefix, checkpoint)

    def repositories(self, moving=False):
        versions = {'v1': SimpleNamespace(commit='a' * 40), 'v2': SimpleNamespace(commit='b' * 40)}
        class FrozenRepository:
            def __init__(self, values):
                self.versions = dict(values)
            def route(self, path, query):
                if path == 'git/ref/tags/v2' and not query:
                    return 200, {'ref': 'refs/tags/v2', 'object': {'sha': self.versions['v2'].commit, 'type': 'commit'}}
                return None
        helper = SimpleNamespace(Repository=FrozenRepository,
            repositories=lambda: {'fixture/source': FrozenRepository(versions)})
        return proof.repositories(helper, moving)['fixture/source']

    def test_only_known_full_commit_branch_tag_probes_receive_404(self):
        repo = self.repositories()
        for kind in ('heads', 'tags'):
            for known in ('a' * 40, 'b' * 40):
                self.assertEqual(404, repo.route('git/ref/' + kind + '/' + known, {})[0])
            for unknown in ('c' * 40, 'main', 'abcd', '--flag'):
                self.assertIsNone(repo.route('git/ref/' + kind + '/' + unknown, {}))
        self.assertIsNone(repo.route('git/ref/heads/' + 'a' * 40, {'unexpected': ['1']}))
        self.assertIsNone(repo.route('commits/abcd', {}))

    def test_moving_tag_changes_only_final_recheck_and_preserves_fixed_sources(self):
        repo = self.repositories(moving=True)
        before = {tag: version.commit for tag, version in repo.versions.items()}
        replies = [repo.route('git/ref/tags/v2', {})[1]['object']['sha'] for _ in range(3)]
        self.assertEqual(['b' * 40, 'b' * 40, 'a' * 40], replies)
        self.assertEqual(before, {tag: version.commit for tag, version in repo.versions.items()})
        self.assertEqual(3, repo.selected_tag_reads)
        repo.blocked = True
        self.assertIsNone(repo.route('git/ref/tags/v2', {}))
        self.assertIsNone(repo.route('git/ref/heads/' + 'a' * 40, {}))

    def test_mutable_latest_supervisor_cannot_import_before_hash_admission(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary).resolve() / 'unreviewed.py'
            path.write_text('raise AssertionError("Unreviewed import must never run")\n')
            with self.assertRaisesRegex(RuntimeError, 'Exact reviewed be20 supervisor'):
                proof.load_supervisor(path)

    def test_clone_values_keep_policy_metadata_separate_from_regenerated_object_times(self):
        before = {'path': 'resource', 'bytes': 3, 'sha256': 'a' * 64, 'observedXattrs': {'user.fixture': {'bytes': 1, 'sha256': 'b' * 64}},
                  'native': {'device': 1, 'inode': 2, 'ctimeNs': 3, 'mtimeNs': 4, 'mode': 0o100640, 'uid': 5, 'gid': 6}}
        cloned = json.loads(json.dumps(before))
        cloned['native'].update(inode=7, ctimeNs=8)
        self.assertEqual(proof.cloned_values([before]), proof.cloned_values([cloned]))
        cloned['native']['mode'] = 0o100600
        self.assertNotEqual(proof.cloned_values([before]), proof.cloned_values([cloned]))
        cloned['native']['mode'] = before['native']['mode']
        cloned['observedXattrs'] = {}
        self.assertNotEqual(proof.cloned_values([before]), proof.cloned_values([cloned]))

    def test_tests_or_proof_cannot_be_labeled_as_actual_app_binding(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            files = []
            for name, data in (('dotnet', b'\x7fELF inert'), ('git', b'\x7fELF inert'),
                               ('SkillView.Tests.dll', b'not an actual product'),
                               ('SkillView.MaintenanceProof.dll', b'inert proof bytes')):
                path = root / name; path.write_bytes(data)
                files.append({'path': str(path), 'sha256': hashlib.sha256(data).hexdigest()})
            value = {'product': [str(root / 'dotnet'), str(root / 'SkillView.Tests.dll')],
                'proof': [str(root / 'dotnet'), str(root / 'SkillView.MaintenanceProof.dll')],
                'git': str(root / 'git'), 'files': files}
            path = root / 'bindings.json'; path.write_text(json.dumps(value))
            expected = hashlib.sha256(path.read_bytes()).hexdigest()
            with self.assertRaisesRegex(RuntimeError, 'Tests/Proof cannot substitute'):
                proof.verify_inputs(path, expected)

    @contextlib.contextmanager
    def replace_binding_after_read(self, path, replacement):
        original_open = Path.open
        reads = []

        @contextlib.contextmanager
        def swapping_open(item, mode='r', *args, **kwargs):
            with original_open(item, mode, *args, **kwargs) as stream:
                yield stream
            if item == path and mode == 'rb':
                reads.append(item)
                with original_open(path, 'wb') as stream:
                    stream.write(replacement)

        with mock.patch.object(Path, 'open', swapping_open):
            yield
        self.assertEqual([path], reads)  # Exercise the actual admitted raw read.

    def test_runtime_binding_parses_the_hashed_bytes_after_valid_locator_swap(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            files = []
            for relative, data in (('dotnet', b'\x7fELF inert'), ('git', b'\x7fELF inert'),
                                   ('approved/skillview.dll', b'approved actual-App locator fixture'),
                                   ('replacement/skillview.dll', b'different valid actual-App locator fixture'),
                                   ('SkillView.MaintenanceProof.dll', b'inert proof bytes')):
                item = root / relative
                item.parent.mkdir(parents=True, exist_ok=True)
                item.write_bytes(data)
                files.append({'path': str(item), 'sha256': hashlib.sha256(data).hexdigest()})
            approved = {'product': [str(root / 'dotnet'), str(root / 'approved/skillview.dll')],
                        'proof': [str(root / 'dotnet'), str(root / 'SkillView.MaintenanceProof.dll')],
                        'git': str(root / 'git'), 'files': files}
            replacement = dict(approved, product=[str(root / 'dotnet'), str(root / 'replacement/skillview.dll')])
            original = json.dumps(approved).encode('utf-8')
            changed = json.dumps(replacement).encode('utf-8')
            path = root / 'bindings.json'
            path.write_bytes(original)
            with self.replace_binding_after_read(path, changed):
                actual = proof.verify_inputs(path, hashlib.sha256(original).hexdigest())
            self.assertEqual(approved, actual)
            self.assertEqual(changed, path.read_bytes())
            self.assertNotEqual(approved['product'], replacement['product'])
            # Both locators are fully listed and pass the remaining binding checks.
            self.assertEqual(replacement, proof.verify_inputs(path, hashlib.sha256(changed).hexdigest()))

    def test_primary_source_manifest_parses_hashed_rows_after_valid_manifest_swap(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            rows = {}
            for group in ('approved', 'replacement'):
                rows[group] = []
                for index in range(8):
                    raw = (group + '-retained-primary-source-' + str(index)).encode('utf-8')
                    item = root / (group + '-' + str(index) + '.go')
                    item.write_bytes(raw)
                    rows[group].append({'cached': str(item), 'bytes': len(raw),
                                        'sha256': hashlib.sha256(raw).hexdigest()})
            provenance = root / 'provenance.json'
            provenance.write_bytes(b'{"fixture":"inert retained provenance"}')
            original = json.dumps(rows['approved']).encode('utf-8')
            changed = json.dumps(rows['replacement']).encode('utf-8')
            sources = root / 'sources.json'
            sources.write_bytes(original)
            with mock.patch.object(proof, 'PROVENANCE_SHA', hashlib.sha256(provenance.read_bytes()).hexdigest()), \
                 mock.patch.object(proof, 'PRIMARY_SHA', hashlib.sha256(original).hexdigest()):
                with self.replace_binding_after_read(sources, changed):
                    actual = proof.primary_inputs(provenance, sources)
                self.assertEqual(rows['approved'], actual['sources'])
                self.assertEqual(changed, sources.read_bytes())
                self.assertNotEqual(rows['approved'], rows['replacement'])
                with mock.patch.object(proof, 'PRIMARY_SHA', hashlib.sha256(changed).hexdigest()):
                    self.assertEqual(rows['replacement'], proof.primary_inputs(provenance, sources)['sources'])

    def test_actual_cli_wrong_refusal_cannot_count_as_expected_negative(self):
        class NoProcess:
            def run(self, *args):
                return 2, json.dumps({'status': 'refused', 'reason': 'An unrelated refusal'}), ''
        case = proof.Case.__new__(proof.Case)
        case.root, case.env = Path('/private'), {}
        case.bindings, case.executor = {'product': ['/bound/skillview']}, NoProcess()
        with self.assertRaisesRegex(RuntimeError, 'exact expected boundary'):
            case.cli('plan', '--purpose', 'selected-revision', ok=False, contains='cannot implicitly clear')

    def test_reference_or_plan_byte_changes_refuse_before_final_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            reference, plan = root / 'reference.json', root / 'plan.json'
            reference.write_bytes(b'original reference'); plan.write_bytes(b'original immutable plan')
            case = proof.Case.__new__(proof.Case)
            case.references = [(reference, reference.read_bytes(), plan, plan.read_bytes())]
            plan.write_bytes(b'changed immutable plan')
            with self.assertRaisesRegex(RuntimeError, 'Immutable plan/reference bytes changed'):
                case.finish()

    def test_case_count_and_each_declared_process_budget_fit_frozen_supervisor(self):
        self.assertEqual(6, len(proof.SCENARIOS))
        self.assertEqual(set(proof.SCENARIOS), set(proof.START_BUDGETS))
        self.assertTrue(all(0 < count <= 32 for count in proof.START_BUDGETS.values()))
        self.assertEqual('be20fbb48e8231133adf965f49e4572e17becdda', proof.SUPERVISOR_COMMIT)
        self.assertEqual(64, len(proof.SUPERVISOR_SHA))


if __name__ == '__main__':
    unittest.main()
