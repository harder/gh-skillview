"""Credential-free fixtures for private command evidence and install templates."""
import ast
import contextlib
import copy
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch

import registry_proof_budget as budget

ROOT = Path(__file__).resolve().parents[2]
START = '12:34:56.789 Info  [startup] SkillView invocation=Standalone dispatch=Cli\n'


def api(gh, quoted=False):
    exe = '"' + str(gh) + '"' if quoted else str(gh)
    return '12:34:56.790 Debug [subprocess] exec: ' + exe + ' api repos/private/secret-endpoint\n'


class LogsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.home = self.root / 'home'; self.home.mkdir()
        self.env = dict(HOME=str(self.home), XDG_CACHE_HOME=str(self.root / 'cache'), LOCALAPPDATA=str(self.root / 'local'))
        base = Path(self.env['LOCALAPPDATA']) if budget.sys.platform == 'win32' else self.home / 'Library/Caches' if budget.sys.platform == 'darwin' else Path(self.env['XDG_CACHE_HOME'])
        self.logs = base / 'SkillView/logs'; self.logs.mkdir(parents=True)
        self.file = self.logs / 'skillview-2026-10-28.log'
        self.gh = self.root / 'gh with spaces' / 'gh'
        self.diagnostics = {}; self.counter = budget.ProductCommands(self.diagnostics)

    def append(self, text, file=None):
        with (file or self.file).open('a', encoding='utf-8') as stream:
            stream.write(text)

    def observe(self, text, role='app', kind='corrupt-yaml'):
        with self.counter.observe(self.env, self.gh, role, kind, self.root):
            self.append(text)
        return self.diagnostics['productInvocations'][-1]['count']

    def test_exact_quoted_and_unquoted_paths_mixed_commands_and_append_offsets(self):
        self.assertEqual(self.observe(START + api(self.gh) + api(self.gh, True)
                                     + '12:34:56.791 Debug [subprocess] exec: git show "' + str(self.gh) + '" api ignored\n'
                                     + '12:34:56.792 Debug [subprocess] exec: ' + str(self.gh) + ' skill list --json fields\n'), 2)
        self.assertEqual(self.observe(START + '12:34:56.793 Debug [subprocess] exec: git status\n'), 0)
        self.assertEqual(self.diagnostics['productGhApiCommands'], 2)
        self.assertEqual(self.diagnostics['byHost']['app']['count'], 2)
        self.assertEqual(self.diagnostics['byCaseKind']['corrupt-yaml']['count'], 2)
        self.assertNotIn(str(self.root), json.dumps(self.diagnostics))
        self.assertNotIn('secret-endpoint', json.dumps(self.diagnostics))
        self.assertIsNone(self.diagnostics['httpRequests'])

    def test_rollover_and_new_date_files_are_counted_once_without_recounting_prior_cases(self):
        self.assertEqual(self.observe(START + api(self.gh)), 1)
        with self.counter.observe(self.env, self.gh, 'extension', 'missing-empty', self.root):
            self.append(START, self.logs / 'skillview-2026-10-28-001.log')
            self.append(api(self.gh), self.logs / 'skillview-2026-10-29.log')
        self.assertEqual(self.diagnostics['productInvocations'][-1]['count'], 1)
        self.assertEqual(self.diagnostics['productGhApiCommands'], 2)
        self.assertEqual(self.diagnostics['byHost']['extension']['count'], 1)

    def test_missing_new_startup_or_proofhost_evidence_never_inherits_prior_cli_count(self):
        self.assertIsNone(self.observe(''))
        self.assertEqual(self.observe(START + api(self.gh)), 1)
        with self.counter.observe(self.env, self.gh, 'proof-host', 'missing-empty', self.root):
            pass
        self.assertIsNone(self.diagnostics['productInvocations'][-1]['count'])
        self.assertIsNone(self.diagnostics['productGhApiCommands'])
        self.assertEqual(self.diagnostics['knownProductGhApiCommands'], 1)
        self.assertIsNone(self.diagnostics['byHost']['proof-host']['count'])
        self.assertEqual(self.diagnostics['byHost']['app']['knownCommands'], 1)
        self.assertIsNone(self.observe('', 'old-app'))

    def test_truncation_disappearance_unreadable_oversize_and_ambiguous_records_are_unknown(self):
        for fault in ('truncate', 'remove', 'read', 'oversize', 'ambiguous'):
            with self.subTest(fault=fault):
                self.file.write_text(START + api(self.gh))
                reader = patch.object(budget, '_regular_bytes', side_effect=OSError('private secret path')) if fault == 'read' else contextlib.nullcontext()
                with reader, self.counter.observe(self.env, self.gh, 'app', 'missing-empty', self.root):
                    if fault == 'truncate': self.file.write_text('changed\n')
                    elif fault == 'remove': self.file.unlink()
                    elif fault == 'oversize': self.file.write_bytes(b'x' * (budget.MAX_LOG_FILE + 1))
                    elif fault == 'ambiguous': self.append('unframed private-log sentinel\n')
                self.assertIsNone(self.diagnostics['productInvocations'][-1]['count'])
        self.assertNotIn('sentinel', json.dumps(self.diagnostics))
        self.assertNotIn(str(self.root), json.dumps(self.diagnostics))

    def test_wrong_executable_partial_line_multiple_startups_and_parser_budget_refuse_false_zero(self):
        for text in [START + api(Path('/other/gh')), START + api(self.gh).rstrip('\n'), START + START,
                     START + '12:34:56.790 Debug [subprocess] exec: "broken gh api private\n']:
            self.file.unlink(missing_ok=True)
            self.assertIsNone(self.observe(text))
        self.file.unlink(missing_ok=True)
        with patch.object(budget, 'MAX_LINES', 1):
            self.assertIsNone(self.observe(START + api(self.gh)))
        self.file.unlink(missing_ok=True)
        for i in range(budget.MAX_FILES + 1):
            (self.logs / f'skillview-2026-10-28-{i+1:03}.log').write_text('')
        self.assertIsNone(self.observe(START))

    def test_instability_and_diagnostic_exceptions_do_not_mask_primary_exception(self):
        with patch.object(budget, '_log_snapshot', side_effect=ValueError('private data')):
            with self.assertRaisesRegex(RuntimeError, '^primary$'):
                with self.counter.observe(self.env, self.gh, 'app', 'missing-empty', self.root):
                    raise RuntimeError('primary')
        self.assertIsNone(self.diagnostics['productGhApiCommands'])
        with patch.object(budget, 'MAX_INVOCATIONS', 1):
            self.observe(START)
        self.assertTrue(self.diagnostics['invocationBudgetExceeded'])
        self.assertIsNone(self.diagnostics['productGhApiCommands'])

    def test_only_exact_cli_roles_enable_debug_and_publication_guard_is_fixed(self):
        raw = b'HTTP 403 API rate limit exceeded /private/token ENDPOINT Debug sentinel'
        for role in ('app', 'extension', 'old-app'):
            env = budget.product_environment(self.env, role)
            self.assertEqual(env['SKILLVIEW_LOG'], 'debug')
            self.assertNotIn('SKILLVIEW_LOG', self.env)
            self.assertEqual(budget.public_stderr(raw, role), 'Private stderr retained in proof receipt')
        for role in (None, 'proof-host'):
            self.assertIs(budget.product_environment(self.env, role), self.env)
            self.assertEqual(budget.public_stderr(raw, role), raw.decode())
        app = Path('/private/skillview'); old = Path('/old/skillview'); proof = Path('/proof/host')
        self.assertEqual(budget.cli_role(app, app, old, proof), 'app')
        self.assertIsNone(budget.cli_role(Path('/other/skillview'), app, old, proof))

    def test_actual_restore_failure_keeps_private_bytes_and_redacts_only_product_cli(self):
        source = ast.parse((ROOT / 'tests/native/prove-registry-recorded-restore.py').read_text())
        command = copy.deepcopy(next(n for n in ast.walk(source) if isinstance(n, ast.FunctionDef) and n.name == 'command'))
        command.body = [ast.Global(names=n.names) if isinstance(n, ast.Nonlocal) else n for n in command.body]
        raw = b'Debug secret-token /private/HOME repos/private/endpoint'
        calls = []
        def fake_run(*args, **kwargs):
            calls.append(kwargs)
            return types.SimpleNamespace(returncode=1, stdout=b'{}', stderr=raw)
        env = dict(self.env)
        namespace = dict(sequence=0, gh=self.gh, receipt_dir=self.root, env=env, case_root=self.root,
                         app=Path('/product/app'), args=types.SimpleNamespace(old_app=None, proof_host=None),
                         damage='corrupt-yaml', report={'commandDiagnostics': {'directGhCommands': dict(skillInstall=0, api=0, other=0)}},
                         subprocess=types.SimpleNamespace(run=fake_run, DEVNULL=subprocess.DEVNULL, TimeoutExpired=subprocess.TimeoutExpired),
                         json=json, cli_role=budget.cli_role, product_environment=budget.product_environment,
                         public_stderr=budget.public_stderr, product_counts=self.counter)
        exec(compile(ast.fix_missing_locations(ast.Module(body=[command], type_ignores=[])), '<actual-wrapper>', 'exec'), namespace)
        with self.assertRaises(AssertionError) as failure:
            namespace['command']([Path('/product/app'), 'registry-restore-plan'])
        self.assertNotIn('secret-token', str(failure.exception))
        self.assertNotIn('endpoint', str(failure.exception))
        self.assertEqual((self.root / '0001.stderr').read_bytes(), raw)
        self.assertEqual((self.root / '0001.stdout').read_bytes(), b'{}')
        self.assertEqual(calls[-1]['env']['SKILLVIEW_LOG'], 'debug')
        with self.assertRaises(AssertionError) as direct:
            namespace['command']([self.gh, 'skill', 'install'])
        self.assertIn('secret-token', str(direct.exception))
        self.assertIs(calls[-1]['env'], env)


class TemplateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.cache = budget.InstallTemplates(self.root / 'templates')
        self.gh = self.root / 'gh'; self.gh.write_bytes(b'fake gh version A')
        self.home = self.root / 'first-home'; self.home.mkdir()

    def install(self, home, context=None, link=False):
        payload = home / '.agents/skills/gh-skill'; payload.mkdir(parents=True)
        (payload / 'SKILL.md').write_bytes(b'actual unit fixture source bytes\n')
        (payload / 'resource').write_bytes(b'\x00exact resource\xff')
        (home / '.agents/.skill-lock.json').write_text(json.dumps({'source': 'cli/cli', 'context': context}))
        if os.name != 'nt':
            (payload / 'SKILL.md').chmod(0o640)
            payload.chmod(0o750)
        if link:
            (payload / 'relative-link').symlink_to('resource')

    def capture(self):
        before = self.cache.before(self.home)
        self.install(self.home)
        self.assertTrue(self.cache.capture(self.gh, True, 'skills/gh-skill', self.home, self.root, self.root, before))

    def test_same_key_reuses_once_with_exact_bytes_kind_mode_and_new_objects(self):
        self.capture()
        target = self.root / 'second-home'; target.mkdir()
        label = self.cache.reuse(self.gh, True, 'skills/gh-skill', target)
        self.assertRegex(label, r'^template:[0-9a-f]{64}$')
        self.assertEqual(budget._home_manifest(target), budget._home_manifest(self.home))
        if os.name != 'nt':
            self.assertNotEqual((target / '.agents/skills/gh-skill/SKILL.md').stat().st_ino,
                                (self.home / '.agents/skills/gh-skill/SKILL.md').stat().st_ino)
        third = self.root / 'third-home'; third.mkdir()
        self.assertIsNone(self.cache.reuse(self.gh, True, 'skills/gh-skill', third))
        other = budget.InstallTemplates(self.root / 'other-invocation')
        self.assertIsNone(other.reuse(self.gh, True, 'skills/gh-skill', third))

    def test_different_binary_hash_pin_or_source_never_reuses(self):
        self.capture()
        other_gh = self.root / 'other-gh'; other_gh.write_bytes(b'fake gh version B')
        for index, (gh, pin, source) in enumerate([(other_gh, True, 'skills/gh-skill'), (self.gh, False, 'skills/gh-skill'),
                                                 (self.gh, True, '.github/skills/cli-exercise')]):
            target = self.root / str(index); target.mkdir()
            self.assertIsNone(self.cache.reuse(gh, pin, source, target))
            self.assertEqual(list(target.iterdir()), [])

    def test_tamper_bytes_extra_entries_and_manifest_refuse_without_touching_new_home(self):
        for index, corruption in enumerate(('bytes', 'extra', 'manifest')):
            with self.subTest(corruption=corruption):
                source = self.root / ('source-' + str(index)); source.mkdir(); self.install(source)
                cache = budget.InstallTemplates(self.root / ('cache-' + str(index)))
                self.assertTrue(cache.capture(self.gh, True, 'skills/gh-skill', source, self.root, self.root, {}))
                key = cache.key(self.gh, True, 'skills/gh-skill'); parent = cache.root / key
                if corruption == 'bytes': (parent / 'home/.agents/skills/gh-skill/resource').write_bytes(b'changed')
                elif corruption == 'extra': (parent / 'home/.agents/skills/gh-skill/extra').write_text('unknown')
                else: (parent / 'manifest.json').write_text('{}')
                target = self.root / ('new-' + str(index)); target.mkdir()
                self.assertIsNone(cache.reuse(self.gh, True, 'skills/gh-skill', target))
                self.assertEqual(list(target.iterdir()), [])

    @unittest.skipIf(os.name == 'nt', 'Windows payload Unix mode evidence is explicitly unknown')
    def test_mode_tampering_refuses_and_readonly_directory_modes_copy_after_children(self):
        self.install(self.home)
        (self.home / '.agents/skills/gh-skill').chmod(0o550)
        self.assertTrue(self.cache.capture(self.gh, True, 'skills/gh-skill', self.home, self.root, self.root, {}))
        key = self.cache.key(self.gh, True, 'skills/gh-skill')
        target = self.root / 'new'; target.mkdir()
        self.assertIsNotNone(self.cache.reuse(self.gh, True, 'skills/gh-skill', target))
        self.assertEqual(stat.S_IMODE((target / '.agents/skills/gh-skill').stat().st_mode), 0o550)
        # A fresh cache exercises mode tamper before its one reuse.
        second = budget.InstallTemplates(self.root / 'second-cache')
        self.assertTrue(second.capture(self.gh, False, 'skills/gh-skill', self.home, self.root, self.root, {}))
        key = second.key(self.gh, False, 'skills/gh-skill')
        (second.root / key / 'home/.agents/skills/gh-skill/resource').chmod(0o400)
        empty = self.root / 'new-empty'; empty.mkdir()
        self.assertIsNone(second.reuse(self.gh, False, 'skills/gh-skill', empty))

    def test_registry_home_cwd_caseroot_context_and_nonempty_before_refuse(self):
        for index, context in enumerate([str(self.home), str(self.root / 'work'), str(self.root)]):
            source = self.root / ('context-' + str(index)); source.mkdir(); self.install(source, context=context)
            self.assertFalse(self.cache.capture(self.gh, True, 'skills/gh-skill', source, self.root / 'work', self.root, {}))
        self.install(self.home)
        self.assertFalse(self.cache.capture(self.gh, True, 'skills/gh-skill', self.home, self.root, self.root, {'existing': {}}))
        (self.home / '.config').mkdir()
        self.assertFalse(self.cache.capture(self.gh, True, 'skills/gh-skill', self.home, self.root, self.root, {}))

    @unittest.skipIf(os.name == 'nt', 'No symlink creation privilege assumed')
    def test_internal_links_copy_as_links_absolute_context_and_external_links_refuse(self):
        self.install(self.home, link=True)
        self.assertTrue(self.cache.capture(self.gh, True, 'skills/gh-skill', self.home, self.root, self.root, {}))
        target = self.root / 'new'; target.mkdir()
        self.assertIsNotNone(self.cache.reuse(self.gh, True, 'skills/gh-skill', target))
        link = target / '.agents/skills/gh-skill/relative-link'
        self.assertTrue(link.is_symlink())
        self.assertEqual(os.readlink(link), 'resource')
        for index, link_target in enumerate([str(self.home), '../../../../outside']):
            source = self.root / ('links-' + str(index)); source.mkdir(); self.install(source)
            (source / '.agents/skills/gh-skill/bad').symlink_to(link_target)
            self.assertFalse(self.cache.capture(self.gh, False, 'skills/gh-skill', source, self.root, self.root, {}))

    def test_copy_failure_preserves_partial_home_and_allows_one_real_fallback(self):
        self.capture()
        target = self.root / 'target'; target.mkdir()
        real_copy = budget._copy_entries
        def partial(source, destination, manifest):
            (destination / 'partial').write_text('retained evidence')
            raise OSError('private copy failure')
        with patch.object(budget, '_copy_entries', side_effect=partial):
            self.assertIsNone(self.cache.reuse(self.gh, True, 'skills/gh-skill', target))
        self.assertEqual(list(target.iterdir()), [])
        retained = list(self.root.glob('refused-template-home-*'))
        self.assertEqual(len(retained), 1)
        self.assertEqual((retained[0] / 'partial').read_text(), 'retained evidence')
        self.install(target)  # The single fallback setup remains a real caller action.
        self.assertTrue((target / '.agents/skills/gh-skill/SKILL.md').exists())

    def test_actual_production_install_block_saves_exactly_four_of_full_sixteen(self):
        source = ast.parse((ROOT / 'tests/native/prove-registry-recorded-restore.py').read_text())
        damage = next(n for n in ast.walk(source) if isinstance(n, ast.For) and isinstance(n.target, ast.Name) and n.target.id == 'damage')
        start = next(i for i,n in enumerate(damage.body) if isinstance(n,ast.Assign) and isinstance(n.targets[0],ast.Name) and n.targets[0].id=='before_install')
        nodes = copy.deepcopy(damage.body[start:start+3])
        function = ast.FunctionDef(name='setup', args=ast.arguments(posonlyargs=[], args=[ast.arg(arg=n) for n in ['templates','gh','pinned','source','home','case_root','damage','command','install']], kwonlyargs=[], kw_defaults=[], defaults=[]), body=nodes+[ast.Return(ast.Name(id='install_source',ctx=ast.Load()))], decorator_list=[])
        namespace = {}; exec(compile(ast.fix_missing_locations(ast.Module(body=[function],type_ignores=[])),'<actual-install-block>','exec'),namespace)
        calls = []; labels = []
        tools = [self.gh, self.root / 'gh-b']; tools[1].write_bytes(b'gh version B')
        for tool_index, gh in enumerate(tools):
            for pin in (True,False):
                for kind in ('corrupt-yaml','missing-empty','missing-corroborated'):
                    case = self.root / f'case-{tool_index}-{pin}-{kind}'; case.mkdir(); home=case/'home';home.mkdir()
                    source = '.github/skills/cli-exercise' if kind=='missing-corroborated' else 'skills/gh-skill'
                    def command(argv):
                        calls.append(argv)
                        self.install(home)
                    label=namespace['setup'](self.cache,gh,pin,source,home,case,kind,command,[gh,'skill','install'])
                    labels.append(label)
            # The independent-context installs remain real and never enter setup/cache.
            calls.extend([[gh,'skill','install','independent-a'],[gh,'skill','install','independent-b']])
        self.assertEqual(len(labels),12)
        self.assertEqual(sum(label.startswith('template:') for label in labels),4)
        self.assertEqual(len(calls),12)
        self.assertEqual(2*2*3+2*2-len(calls),4)


if __name__ == '__main__':
    unittest.main()
