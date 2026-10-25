"""Offline contracts for the CI cache, artifact and completion boundaries."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location('native_inputs', Path(__file__).with_name('native_proof_inputs.py'))
native = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(native)


class NativeProofInputsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'producer'
        self.root.mkdir()
        self.addCleanup(patch.stopall)
        patch.object(native, 'ROOT', self.root).start()
        self.spec = dict(family='legacy', sourceCommit='a' * 40, directory='baseline', environment='BASELINE')
        patch.object(native, 'specs', return_value=[self.spec]).start()
        patch.object(native, 'git', return_value='b' * 40).start()
        patch.dict(os.environ, GITHUB_SHA='b' * 40, GITHUB_RUN_ID='123', GITHUB_RUN_ATTEMPT='2',
                   GITHUB_OUTPUT=str(self.root / 'output'), RUNNER_TEMP=self.temporary.name).start()
        self.write('global.json', json.dumps({'sdk': {'version': '10.0.100'}}))

    def write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        return path

    def receipt(self):
        binary = self.write('.ci-native/baselines/legacy/skillview', 'frozen tool')
        binary.chmod(0o755)
        value = dict(sourceCommit=self.spec['sourceCommit'], trackedClean=True, binarySha256=native.digest(binary),
                     cacheKey='cache-key', rid='linux-x64', sdk='10.0.100', recipe=list(native.PUBLISH))
        native.write_json(binary.parent / 'source-receipt.json', value)
        return binary, value

    def archive(self):
        self.receipt()
        self.write('.ci-native/baseline-plan.json', json.dumps({'cacheKey': 'cache-key'}))
        for relative in ('dist/app/skillview', 'dist/ext/gh-skillview',
                         'tests/SkillView.MaintenanceProof/bin/Release/net10.0/SkillView.MaintenanceProof.dll'):
            self.write(relative, relative).chmod(0o755)
        for version, name in [('2.97.0', 'minimum'), ('2.102.0', 'current')]:
            binary = self.write('.ci-native/gh-' + name + '/gh', version)
            binary.chmod(0o755)
            native.write_json(binary.parent / 'source.json', dict(version=version, rid='linux-x64',
                                                                 binarySha256=native.digest(binary)))
        native.pack('linux-x64')
        return (self.root / '.ci-native/native-proof-inputs.tar').read_bytes()

    def extract(self, data):
        destination = Path(self.temporary.name) / 'consumer'
        destination.mkdir(exist_ok=True)
        (destination / '.ci-native').mkdir(exist_ok=True)
        (destination / '.ci-native/native-proof-inputs.tar').write_bytes(data)
        (destination / 'global.json').write_text((self.root / 'global.json').read_text())
        with patch.object(native, 'ROOT', destination):
            native.unpack('linux-x64')
        return destination

    @staticmethod
    def rewrite(data, change):
        output = io.BytesIO()
        with tarfile.open(fileobj=io.BytesIO(data)) as source, tarfile.open(fileobj=output, mode='w') as target:
            for member in source.getmembers():
                content = source.extractfile(member).read()
                member, content = change(member, content)
                member.size = len(content)
                target.addfile(member, io.BytesIO(content))
        return output.getvalue()

    def test_cache_receipt_requires_exact_source_recipe_sdk_and_bytes(self):
        binary, receipt = self.receipt()
        self.assertEqual(native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64'), binary)
        for field, bad in [('sourceCommit', 'c' * 40), ('trackedClean', False), ('cacheKey', 'old-key'),
                           ('rid', 'osx-arm64'), ('sdk', '10.0.101'), ('recipe', []), ('binarySha256', '0' * 64)]:
            with self.subTest(field=field):
                value = {**receipt, field: bad}
                native.write_json(binary.parent / 'source-receipt.json', value)
                with self.assertRaises(ValueError):
                    native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64')
        native.write_json(binary.parent / 'source-receipt.json', receipt)
        binary.write_text('corrupt cache bytes')
        with self.assertRaises(ValueError):
            native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64')

    def test_plan_key_changes_with_lock_and_runner_image(self):
        source = self.root / 'frozen'
        source.mkdir()
        (source / 'global.json').write_text('{}')
        lock = source / 'packages.lock.json'
        lock.write_text('first lock')
        with patch.object(native, 'checkout', return_value=source), patch.object(
                native.subprocess, 'check_output', return_value='10.0.100\n'):
            native.plan('linux-x64')
            first = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey']
            lock.write_text('new lock')
            native.plan('linux-x64')
            second = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey']
            self.assertNotEqual(first, second)
            with patch.dict(os.environ, ImageVersion='new-image'):
                native.plan('linux-x64')
            self.assertNotEqual(second, json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey'])
        with patch.object(native.subprocess, 'check_output', return_value='10.0.101\n'):
            with self.assertRaises(ValueError):
                native.plan('linux-x64')

    def test_cached_prepare_rechecks_source_receipt_and_runs_version_without_publish(self):
        self.receipt()
        self.write('.ci-native/baseline-plan.json', json.dumps(dict(
            cacheKey='cache-key', inputs={'rid': 'linux-x64', 'sdk': '10.0.100'}, sources=[self.spec])))
        with patch.object(native, 'checkout', return_value=self.root), patch.object(native.subprocess, 'run') as run:
            native.prepare('linux-x64')
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.args[0][-1], '--version')
        with patch.object(native, 'checkout', side_effect=ValueError('dirty frozen source')):
            with self.assertRaises(ValueError):
                native.prepare('linux-x64')

    def test_artifact_round_trip_binds_file_bytes_and_preserves_executable_modes(self):
        destination = self.extract(self.archive())
        self.assertEqual((destination / 'dist/app/skillview').read_text(), 'dist/app/skillview')
        if os.name != 'nt':
            for relative in ('dist/app/skillview', '.ci-native/baselines/legacy/skillview', '.ci-native/gh-current/gh'):
                self.assertEqual((destination / relative).stat().st_mode & 0o777, 0o755)

    def test_artifact_rejects_wrong_commit_rid_file_set_or_checksum(self):
        original = self.archive()
        cases = [('commit', 'c' * 40), ('rid', 'osx-arm64'), ('files', {})]
        for field, value in cases:
            with self.subTest(field=field):
                def mutate(member, content):
                    if member.name.endswith('native-inputs-manifest.json'):
                        manifest = json.loads(content)
                        manifest[field] = value
                        content = json.dumps(manifest).encode()
                    return member, content
                with self.assertRaises(ValueError):
                    self.extract(self.rewrite(original, mutate))
        corrupted = self.rewrite(original, lambda member, content: (
            member, b'corrupted' if member.name == 'dist/app/skillview' else content))
        with self.assertRaises(ValueError):
            self.extract(corrupted)

    def test_artifact_rejects_traversal_links_and_unsafe_names_before_writes(self):
        original = self.archive()
        for name in ('../outside', '/absolute', 'dist/app/../outside', 'dist\\app\\tool',
                     'dist/app/C:tool', 'src/tool', '.ci-native//tool'):
            with self.subTest(name=name):
                self.assertFalse(native.safe_member(name))
                def mutate(member, content):
                    member.name = name
                    return member, content
                with self.assertRaises(ValueError):
                    self.extract(self.rewrite(original, mutate))
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.DIRTYPE):
            def mutate(member, content):
                member.type = kind
                member.linkname = 'outside'
                return member, content
            with self.assertRaises(ValueError):
                self.extract(self.rewrite(original, mutate))

    def test_stage_selects_exact_historical_families_and_preserves_verified_bytes(self):
        binary, receipt = self.receipt()
        families = [dict(family=family, sourceCommit=self.spec['sourceCommit'], directory=directory, environment=environment)
                    for family, _, _, directory, environment in native.FAMILIES]
        for spec in families:
            self.write('.ci-native/baselines/' + spec['family'] + '/skillview', binary.read_text())
            native.write_json(self.root / '.ci-native/baselines' / spec['family'] / 'source-receipt.json', receipt)
        self.write('.ci-native/native-inputs-manifest.json', json.dumps({'cacheKey': 'cache-key'}))
        environment = self.root / 'environment'
        path_file = self.root / 'path'
        def checkout(spec):
            return self.root / ('checkout-' + spec['family'])
        for shard, expected in [('owners', families[1:2]), ('sources', families), ('inspection-unpin', families[:2])]:
            with self.subTest(shard=shard), patch.object(native, 'specs', return_value=families), patch.object(
                    native, 'checkout', side_effect=checkout), patch.dict(
                    os.environ, GITHUB_ENV=str(environment), GITHUB_PATH=str(path_file)):
                environment.write_text('')
                path_file.write_text('')
                native.stage('linux-x64', shard)
                self.assertEqual(environment.read_text().splitlines(),
                                 [spec['environment'] + '=' + str(checkout(spec)) for spec in expected])
                self.assertEqual(path_file.read_text(), str(self.root / '.ci-native/gh-current') + '\n')
                for spec in expected:
                    self.assertEqual((checkout(spec) / 'dist/app/skillview').read_bytes(), binary.read_bytes())

    def test_completion_requires_all_shards_from_same_run_commit_and_current_or_earlier_attempt(self):
        for shard in native.SHARDS:
            native.completion('linux-x64', shard)
        native.verify_completions('linux-x64')
        path = self.root / 'dist/proof-completion/linux-x64-owners.json'
        valid = json.loads(path.read_text())
        native.write_json(path, {**valid, 'attempt': '1'})
        native.verify_completions('linux-x64')  # A successful shard retained by rerun-failed-jobs.
        for field, bad in [('run', 'other'), ('attempt', '3'), ('attempt', '0'), ('attempt', True),
                           ('attempt', 'unknown'), ('commit', 'c' * 40), ('rid', 'win-x64')]:
            with self.subTest(field=field):
                native.write_json(path, {**valid, field: bad})
                with self.assertRaises(ValueError):
                    native.verify_completions('linux-x64')
        path.unlink()
        with self.assertRaises(ValueError):
            native.verify_completions('linux-x64')


if __name__ == '__main__':
    unittest.main()
