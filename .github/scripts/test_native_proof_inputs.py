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


def audit_text(source):
    message = "Package 'Microsoft.DotNet.ILCompiler' 10.0.13 has a known high severity vulnerability, " + native.ADVISORY
    return ('  ' + str(source / native.APP) + ' : warning NU1903: ' + message + '\n'
            + '  ' + str(source / native.CORE) + ' : error NU1903: Warning As Error: ' + message + '\n'
            + 'Build FAILED.\n    1 Warning(s)\n    1 Error(s)\nTime Elapsed 00:00:01.23\n')


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
        patch.object(native, 'git', side_effect=lambda *args, **kwargs: '' if args[0] == 'status' else (self.spec['sourceCommit'] if kwargs.get('cwd') == self.frozen else 'b' * 40)).start()
        patch.dict(os.environ, GITHUB_SHA='b' * 40, GITHUB_RUN_ID='123', GITHUB_RUN_ATTEMPT='2',
                   GITHUB_OUTPUT=str(self.root / 'output'), RUNNER_TEMP=self.temporary.name).start()
        self.write('global.json', json.dumps({'sdk': {'version': '10.0.403'}}))
        self.frozen = Path(self.temporary.name) / 'frozen'
        self.frozen.mkdir()
        (self.frozen / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.113'}}))
        (self.frozen / 'packages.lock.json').write_text('frozen lock')
        patch.object(native, 'checkout', return_value=self.frozen).start()
        self.binding = dict(recipeVersion=native.AUDIT_RECIPE, dispositionSha256='d' * 64,
                            propsSha256=__import__('hashlib').sha256(native.PROPS).hexdigest(), scope='historical-ci-fixture-only/non-release')
        patch.object(native, 'exception_binding', return_value=self.binding).start()
        patch.object(native, 'audit_preflight', side_effect=lambda spec, source, rid: self.audit(spec, source, rid)).start()

    def write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        return path

    def audit(self, spec, source, rid='linux-x64'):
        directory = native.audit_directory(spec)
        directory.mkdir(parents=True, exist_ok=True)
        text = audit_text(source)
        (directory / 'stdout').write_text(text)
        (directory / 'stderr').write_text('')
        native.write_json(directory / 'command.json', dict(argv=['dotnet', 'restore', native.APP, '--locked-mode', '--verbosity', 'normal'], cwd=str(source), pid=1, deadlineSeconds=600))
        value = dict(schema=1, sourceCommit=spec['sourceCommit'], sourceInputs=native.source_inputs(source),
            sdkActual=native.selected_sdk(source), exception=self.binding, rid=rid, currentCommit='b' * 40,
            run='123', exitCode=1, stdoutSha256=native.digest(directory / 'stdout'),
            stderrSha256=native.digest(directory / 'stderr'), findings=native.audit_findings(text, '', 1, source))
        native.write_json(directory / 'audit.json', value)
        return value

    def receipt(self):
        binary = self.write('.ci-native/baselines/legacy/skillview', 'frozen tool')
        binary.chmod(0o755)
        value = dict(schema=3, sourceCommit=self.spec['sourceCommit'], trackedClean=True, binarySha256=native.digest(binary),
                     cacheKey='cache-key', rid='linux-x64', currentSdk='10.0.403', sdk='10.0.113',
                     sourceInputs=native.source_inputs(self.frozen), recipe=list(native.PUBLISH), exception=self.binding)
        native.write_json(binary.parent / 'source-receipt.json', value)
        self.audit(self.spec, self.frozen)
        return binary, value

    def plan_value(self):
        return dict(schema=3, cacheKey='cache-key', inputs=dict(rid='linux-x64', sdk='10.0.403',
            dependencies={'current': native.source_inputs(self.root)['dependencies']},
            producers={'legacy': native.source_inputs(self.frozen)}, exceptions={'legacy': self.binding}), sources=[self.spec])

    def archive(self):
        self.receipt()
        self.write('.ci-native/baseline-plan.json', json.dumps(self.plan_value()))
        for relative in ('dist/app/skillview', 'dist/ext/gh-skillview',
                         'tests/SkillView.MaintenanceProof/bin/Release/net10.0/SkillView.MaintenanceProof.dll'):
            self.write(relative, relative).chmod(0o755)
        for version, name in [('2.102.0', 'minimum'), ('2.103.3', 'current')]:
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
        self.assertEqual(native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64', native.source_inputs(self.frozen)), binary)
        for field, bad in [('sourceCommit', 'c' * 40), ('trackedClean', False), ('cacheKey', 'old-key'),
                           ('rid', 'osx-arm64'), ('sdk', '10.0.101'), ('currentSdk', '10.0.113'),
                           ('schema', 1), ('schema', 2), ('sourceInputs', {}), ('recipe', []), ('binarySha256', '0' * 64)]:
            with self.subTest(field=field):
                value = {**receipt, field: bad}
                native.write_json(binary.parent / 'source-receipt.json', value)
                with self.assertRaises(ValueError):
                    native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64', native.source_inputs(self.frozen))
        native.write_json(binary.parent / 'source-receipt.json', receipt)
        binary.write_text('corrupt cache bytes')
        with self.assertRaises(ValueError):
            native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64', native.source_inputs(self.frozen))

    def test_plan_key_changes_with_lock_and_runner_image(self):
        source = self.frozen
        lock = source / 'packages.lock.json'
        lock.write_text('first lock')
        with patch.object(native, 'checkout', return_value=source), patch.object(
                native.subprocess, 'check_output', side_effect=lambda *args, **kwargs:
                    native.selected_sdk(kwargs['cwd']) + '\n'):
            native.plan('linux-x64')
            first = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey']
            lock.write_text('new lock')
            native.plan('linux-x64')
            second = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey']
            self.assertNotEqual(first, second)
            with patch.dict(os.environ, ImageVersion='new-image'):
                native.plan('linux-x64')
            self.assertNotEqual(second, json.loads((self.root / '.ci-native/baseline-plan.json').read_text())['cacheKey'])
        with patch.object(native.subprocess, 'check_output', return_value='10.0.402\n'):
            with self.assertRaises(ValueError):
                native.plan('linux-x64')

    def test_cached_prepare_rechecks_source_receipt_and_runs_version_without_publish(self):
        self.receipt()
        self.write('.ci-native/baseline-plan.json', json.dumps(self.plan_value()))
        with patch.object(native.subprocess, 'check_output', side_effect=lambda *args, **kwargs:
                          native.selected_sdk(kwargs['cwd']) + '\n') as selected, patch.object(native.subprocess, 'run') as run:
            native.prepare('linux-x64')
            self.assertEqual([call.kwargs['cwd'] for call in selected.call_args_list], [self.root, self.frozen])
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
        self.write('.ci-native/native-inputs-manifest.json', json.dumps(dict(schema=3, currentSdk='10.0.403',
            cacheKey='cache-key', exceptions={spec['family']: self.binding for spec in families}, producerInputs={spec['family']: native.source_inputs(self.frozen) for spec in families})))
        for spec in families:
            source = self.root / ('checkout-' + spec['family'])
            source.mkdir()
            for name in ('global.json', 'packages.lock.json'):
                (source / name).write_bytes((self.frozen / name).read_bytes())
        environment = self.root / 'environment'
        path_file = self.root / 'path'
        def checkout(spec):
            return self.root / ('checkout-' + spec['family'])
        for spec in families:
            self.audit(spec, checkout(spec))
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

    def test_uncached_prepare_uses_frozen_cwd_and_binds_both_sdks_without_rewriting_inputs(self):
        self.write('.ci-native/baseline-plan.json', json.dumps(self.plan_value()))
        before = native.source_inputs(self.frozen)
        def run(argv, **kwargs):
            if argv[0] == 'dotnet':
                self.assertEqual(kwargs['cwd'], self.frozen)
                self.assertEqual(argv[1], 'publish')
                self.assertEqual(argv[3:3 + len(native.PUBLISH)], list(native.PUBLISH))
                output = self.frozen / 'dist/app/skillview'
                output.parent.mkdir(parents=True)
                output.write_text('exact frozen binary')
                output.chmod(0o755)
        with patch.object(native.subprocess, 'check_output', side_effect=lambda *args, **kwargs:
                          native.selected_sdk(kwargs['cwd']) + '\n'), patch.object(native.subprocess, 'run', side_effect=run):
            native.prepare('linux-x64')
        receipt = json.loads((self.root / '.ci-native/baselines/legacy/source-receipt.json').read_text())
        self.assertEqual(receipt['schema'], 3)
        self.assertEqual(receipt['sdk'], '10.0.113')
        self.assertEqual(receipt['currentSdk'], '10.0.403')
        self.assertEqual(receipt['sourceInputs'], before)
        self.assertEqual(native.source_inputs(self.frozen), before)

    def test_wrong_actual_frozen_sdk_or_changed_input_refuses_before_publish(self):
        self.write('.ci-native/baseline-plan.json', json.dumps(self.plan_value()))
        with patch.object(native.subprocess, 'check_output', return_value='10.0.403\n'), patch.object(native.subprocess, 'run') as run:
            with self.assertRaises(ValueError):
                native.prepare('linux-x64')
            run.assert_not_called()
        (self.frozen / 'packages.lock.json').write_text('changed lock')
        with patch.object(native.subprocess, 'check_output', return_value='10.0.403\n'), patch.object(native.subprocess, 'run') as run:
            with self.assertRaises(ValueError):
                native.prepare('linux-x64')
            run.assert_not_called()

    def test_frozen_sdk_export_is_validated_and_changes_cache_identity(self):
        with patch.object(native.subprocess, 'check_output', side_effect=lambda *args, **kwargs:
                    native.selected_sdk(kwargs['cwd']) + '\n'):
            native.plan('linux-x64')
            first = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())
            self.assertEqual(first['schema'], 3)
            self.assertTrue(first['cacheKey'].startswith('native-producers-v3-'))
            self.assertEqual(first['inputs']['sdk'], '10.0.403')
            self.assertEqual(first['inputs']['producers']['legacy']['sdk'], '10.0.113')
            self.assertIn('frozen-sdks<<FROZEN_SDK_LIST\n10.0.113\nFROZEN_SDK_LIST', (self.root / 'output').read_text())
            (self.frozen / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.402'}}))
            native.plan('linux-x64')
            second = json.loads((self.root / '.ci-native/baseline-plan.json').read_text())
            self.assertNotEqual(first['cacheKey'], second['cacheKey'])
            (self.frozen / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.402\nmalicious'}}))
            with self.assertRaises(ValueError):
                native.plan('linux-x64')

    def test_transfer_rejects_old_schema_or_forged_frozen_sdk_and_lock_bindings(self):
        original = self.archive()
        for field, bad in [('schema', 1), ('currentSdk', '10.0.113'), ('producerInputs', {})]:
            with self.subTest(field=field):
                def mutate(member, content):
                    if member.name.endswith('native-inputs-manifest.json'):
                        manifest = json.loads(content)
                        manifest[field] = bad
                        content = json.dumps(manifest).encode()
                    return member, content
                with self.assertRaises(ValueError):
                    self.extract(self.rewrite(original, mutate))
        (self.frozen / 'packages.lock.json').write_text('new historical lock is forbidden')
        with self.assertRaises(ValueError):
            self.extract(original)

    def test_cache_rejects_missing_fresh_audit_and_mixed_disposition(self):
        binary, receipt = self.receipt()
        audit = native.audit_directory(self.spec) / 'audit.json'
        audit.unlink()
        with self.assertRaises(FileNotFoundError):
            native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64', native.source_inputs(self.frozen))
        self.audit(self.spec, self.frozen)
        for bad in [{}, {**self.binding, 'propsSha256': '0' * 64}, {**self.binding, 'scope': 'release'},
                    {**self.binding, 'recipeVersion': 'old'}, {**self.binding, 'dispositionSha256': '0' * 64}]:
            native.write_json(binary.parent / 'source-receipt.json', {**receipt, 'exception': bad})
            with self.assertRaises(ValueError):
                native.verify_producer(binary.parent, self.spec, 'cache-key', 'linux-x64', native.source_inputs(self.frozen))

    def test_transfer_and_stage_reject_mixed_exception_or_audit_records(self):
        original = self.archive()
        def mutate(member, content):
            if member.name.endswith('native-inputs-manifest.json'):
                value = json.loads(content)
                value['exceptions'] = {}
                content = json.dumps(value).encode()
            return member, content
        with self.assertRaises(ValueError):
            self.extract(self.rewrite(original, mutate))
        native.write_json(self.root / '.ci-native/native-inputs-manifest.json', dict(schema=3, currentSdk='10.0.403',
            cacheKey='cache-key', producerInputs={'legacy': native.source_inputs(self.frozen)}, exceptions={}))
        with self.assertRaises(ValueError):
            native.stage('linux-x64', 'owners')

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


class HistoricalAuditExceptionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'current'
        self.source = Path(self.temporary.name) / 'historical'
        self.root.mkdir()
        self.source.mkdir()
        self.addCleanup(patch.stopall)
        patch.object(native, 'ROOT', self.root).start()
        patch.dict(os.environ, RUNNER_TEMP=self.temporary.name, GITHUB_RUN_ID='123').start()
        (self.root / 'global.json').write_text('{"sdk":{"version":"10.0.403"}}')
        (self.source / 'global.json').write_text('{"sdk":{"version":"10.0.113"}}')
        for name in native.LOCK_PATHS:
            p = self.source / name
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text('{"frozen":"' + name + '"}')
        self.policy, self.policy_sha = native.disposition()
        self.spec = dict(family='reviewed-merge', sourceCommit=native.APPROVED_COMMITS['reviewed-merge'][0])
        self.identity = native.source_inputs(self.source)
        self.policy['families']['reviewed-merge']['dependencies'] = self.identity['dependencies']
        patch.object(native, 'checkout', return_value=self.source).start()
        patch.object(native, 'git', side_effect=lambda *a, **k: '' if a[0] == 'status' else (
            self.spec['sourceCommit'] if k.get('cwd') == self.source else 'b' * 40)).start()
        patch.object(native.subprocess, 'check_output', return_value='10.0.113\n').start()

    def policy_context(self):
        return patch.object(native, 'disposition', return_value=(self.policy, self.policy_sha))

    def fake_child(self, argv, **kwargs):
        self.assertEqual(argv[1:7], ['restore', native.APP, '--locked-mode', '--verbosity', 'normal'])
        self.assertNotIn('-r', argv)
        self.assertFalse(any('CustomBefore' in a for a in argv))
        self.assertEqual(kwargs['cwd'], self.source)
        kwargs['stdout'].write(audit_text(self.source).encode())
        from types import SimpleNamespace
        return SimpleNamespace(pid=1234, wait=lambda **k: 1)

    def preflight(self):
        with self.policy_context(), patch.object(native.subprocess, 'Popen', side_effect=self.fake_child):
            return native.audit_preflight(self.spec, self.source, 'linux-x64')

    def test_actual_owner_data_has_only_five_exact_sources_sdk_seven_inputs(self):
        value, checksum = native.disposition()
        self.assertEqual(value['scope'], 'historical-ci-fixture-only/non-release')
        self.assertEqual(value['package'], 'Microsoft.DotNet.ILCompiler')
        self.assertEqual(value['version'], '10.0.13')
        self.assertEqual(value['advisory'], 'https://github.com/advisories/GHSA-p3jp-9v83-76w9')
        self.assertEqual(set(value['families']), {'legacy', 'reviewed-merge', 'unpin', 'reimport', 'source-switch'})
        self.assertEqual(len(checksum), 64)
        for family, row in value['families'].items():
            self.assertEqual((row['sourceCommit'], row['sdk']), native.APPROVED_COMMITS[family])
            self.assertEqual(set(row['dependencies']), {'global.json', *native.LOCK_PATHS})

    def test_source_scope_rejects_wrong_sdk_commit_lock_family_and_current(self):
        with self.policy_context():
            self.assertEqual(native.guarded_source(self.spec, self.source), self.identity)
            for identity in [{**self.identity, 'sdk': '10.0.403'}, {**self.identity, 'dependencies': {}},
                             {**self.identity, 'dependencies': {**self.identity['dependencies'], 'extra': '0' * 64}}]:
                with self.subTest(identity=identity), self.assertRaises(ValueError):
                    native.exception_binding(self.spec, identity)
            for spec in [{**self.spec, 'sourceCommit': 'a' * 40}, {**self.spec, 'family': 'current'}]:
                with self.subTest(spec=spec), self.assertRaises(ValueError):
                    native.exception_binding(spec, self.identity)
            with self.assertRaises(ValueError):
                native.guarded_source(self.spec, self.root)
            with patch.object(native, 'git', return_value='dirty'), self.assertRaises(ValueError):
                native.guarded_source(self.spec, self.source)

    def test_parser_accepts_only_complete_original_policy_app_core_known_high(self):
        text = audit_text(self.source)
        self.assertEqual(native.audit_findings(text, '', 1, self.source)['projects'], {'App': 'warning', 'Core': 'error'})
        self.assertEqual(native.audit_findings(text, 'Welcome to .NET 10.0!\nSDK Version: 10.0.113\n', 1, self.source)['severity'], 'high')
        with self.assertRaises(ValueError):
            native.audit_findings('', text, 1, self.source)
        for bad in [text.replace('NU1903', 'NU1902'), text.replace('10.0.13', '10.0.14'),
                    text.replace('Microsoft.DotNet.ILCompiler', 'Other.Package'), text.replace('known high', 'known moderate'),
                    text.replace('GHSA-p3jp-9v83-76w9', 'GHSA-aaaa-bbbb-cccc'),
                    text + 'error NU1900: vulnerability feed unavailable\n',
                    text + 'error NU1004: changed graph\n', text + 'warning NEW0001: other warning\n',
                    text.replace(' : warning NU1903:', ' : error NU1903:'), text.replace('Warning As Error: ', ''),
                    text.replace('Build FAILED.', ''), text.replace('1 Warning(s)', '0 Warning(s)'), text[:-2],
                    text.replace(str(self.source / native.CORE), '/foreign/SkillView.Core.csproj'),
                    text.replace('1 Error(s)', '2 Error(s)')]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                native.audit_findings(bad, '', 1, self.source)
        for code in [0, 2, None, True]:
            with self.subTest(code=code), self.assertRaises(ValueError):
                native.audit_findings(text, '', code, self.source)

    def test_extra_audit_diagnostics_reach_specific_refusal_with_complete_footer(self):
        text = audit_text(self.source)
        self.assertEqual(native.audit_findings(text, '', 1, self.source)['severity'], 'high')
        for diagnostic in ['error NU1900: vulnerability feed unavailable',
                           'error NU1004: changed graph', 'warning NEW0001: other warning']:
            bad = text.replace('Build FAILED.', diagnostic + '\nBuild FAILED.')
            self.assertTrue(bad.endswith('Time Elapsed 00:00:01.23\n'))
            with self.subTest(diagnostic=diagnostic), self.assertRaisesRegex(ValueError, '^Unexpected audit diagnostic$'):
                native.audit_findings(bad, '', 1, self.source)

    def test_props_reject_comma_and_percent_before_reading(self):
        for character in [',', '%']:
            path = Path(self.temporary.name) / ('unsafe' + character + '.props')
            path.write_bytes(native.PROPS)
            with self.subTest(character=character), patch.object(native, 'regular_bytes', wraps=native.regular_bytes) as read:
                with self.assertRaisesRegex(ValueError, '^Unsafe exception props path$'):
                    native.check_props(path, self.source)
                read.assert_not_called()

    def test_props_are_exact_regular_outside_sources_no_extra_item_or_property(self):
        with self.policy_context():
            p = native.exception_props(self.spec, self.source, self.identity)
            self.assertEqual(p.read_bytes(), native.PROPS)
            self.assertNotIn(self.source, p.parents)
            self.assertEqual(native.PROPS.count(b'NuGetAuditSuppress'), 1)
            for raw in [native.PROPS + b' ', native.PROPS.replace(b'</ItemGroup>', b'<NuGetAuditSuppress Include="other" /></ItemGroup>')]:
                p.write_bytes(raw)
                with self.assertRaises(ValueError):
                    native.check_props(p, self.source)
            p.write_bytes(native.PROPS)
            q = self.source / 'inside.props'
            q.write_bytes(native.PROPS)
            with self.assertRaises(ValueError):
                native.check_props(q, self.source)
            p.unlink()
            with self.assertRaises(FileNotFoundError):
                native.check_props(p, self.source)
            if os.name != 'nt':
                p.symlink_to(q)
                with self.assertRaises(ValueError):
                    native.check_props(p, self.source)

    def test_preflight_records_actual_failure_and_cache_cannot_supply_its_result(self):
        record = self.preflight()
        with self.policy_context():
            self.assertEqual(native.verify_audit(self.spec, self.identity, 'linux-x64'), record)
            self.assertFalse((self.root / '.ci-native/baselines').exists())
            path = native.audit_directory(self.spec) / 'audit.json'
            for field, bad in [('sourceCommit', 'a' * 40), ('sourceInputs', {}), ('sdkActual', '10.0.403'),
                               ('exception', {}), ('rid', 'osx-arm64'), ('run', 'older'), ('currentCommit', 'a' * 40),
                               ('exitCode', 0), ('stdoutSha256', '0' * 64), ('extra', True)]:
                with self.subTest(field=field):
                    native.write_json(path, {**record, field: bad})
                    with self.assertRaises(ValueError):
                        native.verify_audit(self.spec, self.identity, 'linux-x64')
            native.write_json(path, record)
            (path.parent / 'stdout').write_text(audit_text(self.source).replace('NU1903', 'NU1900'))
            with self.assertRaises(ValueError):
                native.verify_audit(self.spec, self.identity, 'linux-x64')

    def publish_arguments(self, props):
        return ['dotnet', 'publish', str(self.source / native.APP), *native.PUBLISH,
                '-p:CustomBeforeMicrosoftCommonProps=' + str(props), '-r', 'linux-x64',
                '-o', str(self.source / 'dist/app')]

    def test_publish_nuget_diagnostics_are_not_accepted_even_with_exit_zero(self):
        for code in ['NU1900', 'NU1903', 'NU1004']:
            for stream in ['stdout', 'stderr']:
                directory = self.root / ('audit-' + code + '-' + stream)
                with self.subTest(code=code, stream=stream), patch.object(native, 'audit_directory', return_value=directory):
                    self.preflight()
                    reached = []
                    def run(argv, **kwargs):
                        reached.append(True)
                        kwargs[stream].write(('prefix ' + code + ': diagnostic\n').encode())
                    with self.policy_context():
                        props = native.exception_props(self.spec, self.source, self.identity)
                        with patch.object(native.subprocess, 'run', side_effect=run), self.assertRaisesRegex(
                                ValueError, '^Historical publish emitted an unexpected NuGet diagnostic$'):
                            native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                    self.assertEqual(reached, [True])

    def test_publish_preserves_original_compiler_linker_warning_policy(self):
        self.preflight()
        def run(argv, **kwargs):
            self.assertTrue(kwargs['check'])
            for flag in ['-warnaserror:IL2026', '-warnaserror:IL3050', '-warnaserror:IL3053']:
                self.assertIn(flag, argv)
            kwargs['stdout'].write(b'file.cs(1,1): warning CS1234: original App policy\n')
            kwargs['stderr'].write(b'ld: warning: native note\nclang: warning: native note\nLINK : warning LNK1234: native note\n')
        with self.policy_context():
            props = native.exception_props(self.spec, self.source, self.identity)
            with patch.object(native.subprocess, 'run', side_effect=run) as child:
                native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                child.assert_called_once()

    def test_publish_nonzero_still_fails_with_original_check_true(self):
        self.preflight()
        def run(argv, **kwargs):
            self.assertTrue(kwargs['check'])
            raise native.subprocess.CalledProcessError(7, argv)
        with self.policy_context():
            props = native.exception_props(self.spec, self.source, self.identity)
            with patch.object(native.subprocess, 'run', side_effect=run) as child:
                with self.assertRaises(native.subprocess.CalledProcessError) as failure:
                    native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                self.assertEqual(failure.exception.returncode, 7)
                child.assert_called_once()

    def test_final_prepublish_props_check_has_positive_and_missing_altered_controls(self):
        reached = []
        for state in ['valid', 'missing', 'altered']:
            directory = self.root / ('audit-final-props-' + state)
            with self.subTest(state=state), patch.object(native, 'audit_directory', return_value=directory):
                self.preflight()
                with self.policy_context():
                    props = native.exception_props(self.spec, self.source, self.identity)
                    if state == 'missing':
                        props.unlink()
                    elif state == 'altered':
                        props.write_bytes(native.PROPS + b' ')
                    with patch.object(native, 'check_props', wraps=native.check_props) as final_check, patch.object(
                            native.subprocess, 'run', side_effect=lambda *a, **k: reached.append(state)) as child:
                        if state == 'valid':
                            native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                            child.assert_called_once()
                        elif state == 'missing':
                            with self.assertRaises(FileNotFoundError):
                                native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                            child.assert_not_called()
                        else:
                            with self.assertRaisesRegex(ValueError, '^Missing, altered or additional audit exception props$'):
                                native.publish_frozen(self.publish_arguments(props), self.source, self.spec)
                            child.assert_not_called()
                        final_check.assert_called_once_with(props, self.source)
        self.assertEqual(reached, ['valid'])

    def test_publish_cannot_apply_exception_to_current_source_or_add_properties(self):
        with patch.object(native.subprocess, 'run') as publish:
            with self.assertRaises(ValueError):
                native.publish_frozen(['dotnet', 'publish'], self.root, self.spec)
            with self.assertRaises(ValueError):
                native.publish_frozen(['dotnet', 'publish', '-p:NuGetAudit=false'], self.source, self.spec)
            publish.assert_not_called()

    def test_disposition_validator_rejects_extra_advisory_source_or_fields(self):
        path = Path(native.__file__).with_name('frozen-audit-exception.json')
        original = path.read_bytes()
        value = json.loads(original)
        for key, bad in [('advisory', 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc'),
                         ('package', 'Other'), ('version', '10.0.14'), ('severity', 'critical'), ('schema', True),
                         ('scope', 'release'), ('extra', True), ('families', {})]:
            data = json.dumps({**value, key: bad}).encode()
            with self.subTest(key=key), patch.object(native, 'regular_bytes', return_value=data), self.assertRaises(ValueError):
                native.disposition()

    def test_preflight_bad_default_does_not_reach_publish_or_write_acceptance(self):
        for index, diagnostic in enumerate(['error NU1900: unknown feed', 'error NU1004: changed graph',
                                            'warning NEW0001: other warning']):
            directory = self.root / ('audit-extra-' + str(index))
            def bad(argv, **kwargs):
                from types import SimpleNamespace
                text = audit_text(self.source).replace('Build FAILED.', diagnostic + '\nBuild FAILED.')
                kwargs['stdout'].write(text.encode())
                return SimpleNamespace(pid=1234, wait=lambda **k: 1)
            with self.subTest(diagnostic=diagnostic), self.policy_context(), patch.object(
                    native, 'audit_directory', return_value=directory), patch.object(
                    native.subprocess, 'Popen', side_effect=bad) as child, patch.object(native.subprocess, 'run') as publish:
                with self.assertRaisesRegex(ValueError, '^Unexpected audit diagnostic$'):
                    native.audit_preflight(self.spec, self.source, 'linux-x64')
                child.assert_called_once()
                publish.assert_not_called()
                self.assertFalse((directory / 'audit.json').exists())

    def test_always_receipts_select_only_exact_named_audit_leaves_including_failures(self):
        workflow = Path(native.__file__).parents[1] / 'workflows/ci.yml'
        text = workflow.read_text()
        block = text.split('      - name: Retain frozen source and recipe receipts\n', 1)[1].split('\n  aot-proof:', 1)[0]
        self.assertIn('        if: always()\n', block)
        self.assertIn('          include-hidden-files: true\n', block)
        self.assertIn('          retention-days: 14\n', block)
        self.assertIn('          if-no-files-found: warn\n', block)
        path_block = block.split('          path: |\n', 1)[1].split('          include-hidden-files:', 1)[0]
        paths = {line.strip() for line in path_block.splitlines() if line.strip()}
        expected = {'.ci-native/baseline-plan.json', '.ci-native/baselines/*/source-receipt.json',
                    '.ci-native/native-inputs-manifest.json'}
        expected |= {'.ci-native/frozen-audits/*/' + leaf for leaf in [
            'stdout', 'stderr', 'publish.stdout', 'publish.stderr', 'command.json', 'exit.json', 'audit.json', 'STOP.json']}
        self.assertEqual(paths, expected)
        self.assertTrue(all('**' not in path for path in paths))



if __name__ == '__main__':
    unittest.main()
