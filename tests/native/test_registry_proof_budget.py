"""Credential-free fixtures for private command evidence and install templates."""
import ast
import contextlib
import copy
import importlib.util
import hashlib
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
COMPLETE = '12:34:56.799 Debug [startup] CLI dispatch completed in 10ms (exit 0)\n'


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
        base = Path(self.env['LOCALAPPDATA']) if budget.IS_WINDOWS else self.home / 'Library/Caches' if budget.IS_DARWIN else Path(self.env['XDG_CACHE_HOME'])
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
                                     + '12:34:56.792 Debug [subprocess] exec: ' + str(self.gh) + ' skill list --json fields\n' + COMPLETE), 2)
        self.assertEqual(self.observe(START + '12:34:56.793 Debug [subprocess] exec: git status\n' + COMPLETE), 0)
        self.assertEqual(self.diagnostics['productGhApiCommands'], 2)
        self.assertEqual(self.diagnostics['byHost']['app']['count'], 2)
        self.assertEqual(self.diagnostics['byCaseKind']['corrupt-yaml']['count'], 2)
        self.assertNotIn(str(self.root), json.dumps(self.diagnostics))
        self.assertNotIn('secret-endpoint', json.dumps(self.diagnostics))
        self.assertIsNone(self.diagnostics['httpRequests'])

    def test_rollover_and_new_date_files_are_counted_once_without_recounting_prior_cases(self):
        self.assertEqual(self.observe(START + api(self.gh) + COMPLETE), 1)
        with self.counter.observe(self.env, self.gh, 'extension', 'missing-empty', self.root):
            self.append(START, self.logs / 'skillview-2026-10-28-001.log')
            self.append(api(self.gh) + COMPLETE, self.logs / 'skillview-2026-10-29.log')
        self.assertEqual(self.diagnostics['productInvocations'][-1]['count'], 1)
        self.assertEqual(self.diagnostics['productGhApiCommands'], 2)
        self.assertEqual(self.diagnostics['byHost']['extension']['count'], 1)

    def test_missing_new_startup_or_proofhost_evidence_never_inherits_prior_cli_count(self):
        self.assertIsNone(self.observe(''))
        self.assertEqual(self.observe(START + api(self.gh) + COMPLETE), 1)
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



class FileDomainsTests(unittest.TestCase):
    @staticmethod
    def info(**changes):
        values = dict(st_dev=11, st_ino=22, st_mode=stat.S_IFREG | 0o644,
                      st_size=3, st_mtime_ns=100, st_ctime_ns=200, st_file_attributes=0)
        values.update(changes)
        return types.SimpleNamespace(**values)

    def read(self, windows=False, name=None, opened=None, names=None, firsts=None, seconds=None,
             data=b'abc', close_error=False):
        from unittest.mock import Mock
        name = name or self.info(); opened = opened or name
        path = Mock(); path.lstat.side_effect = names or [name] * 4
        first = Mock(); first.fileno.return_value = 101; first.read.return_value = data
        second = Mock(); second.fileno.return_value = 102
        if close_error: first.close.side_effect = OSError('SECRET cleanup')
        stats = {101: iter(firsts or [opened] * 3), 102: iter(seconds or [opened] * 2)}
        self.first, self.second, self.path = first, second, path
        with patch.object(budget, 'IS_WINDOWS', windows), patch.object(budget, '_open_bytes', side_effect=[first, second]) as opens, patch.object(budget.os, 'fstat', side_effect=lambda fd: next(stats[fd])):
            self.opens = opens
            return budget._regular_bytes(path, 100)

    def refuse(self, reason, fields=(), **kwargs):
        with self.assertRaises(budget.ObservationError) as caught:
            self.read(**kwargs)
        self.assertEqual(caught.exception.reason, reason)
        self.assertEqual(caught.exception.fields, tuple(sorted(fields)))
        self.assertEqual(str(caught.exception), reason)
        return caught.exception

    def test_all_cross_domain_fields_posix_refuses_windows_keeps_domains_separate(self):
        for field, attr, value in [('dev','st_dev',12),('ino','st_ino',23),('mode','st_mode',stat.S_IFREG | 0o755),
                                    ('size','st_size',4),('mtimeNs','st_mtime_ns',101),('ctimeNs','st_ctime_ns',201)]:
            with self.subTest(field=field):
                name = self.info(**{attr: value})
                self.refuse('nh', [field], name=name, opened=self.info())
                try:
                    data, sig = self.read(windows=True, name=name, opened=self.info())
                except budget.ObservationError as error:
                    self.fail('Stable Windows domains unexpectedly refused: ' + error.reason)
                self.assertEqual(data, b'abc')
                self.assertEqual(hashlib.sha256(data).hexdigest(), hashlib.sha256(b'abc').hexdigest())
                self.assertEqual(sig, budget._signature(name))
                self.second.read.assert_not_called()
                self.first.close.assert_called_once(); self.second.close.assert_called_once()

    def test_zero_handle_refusal_and_weak_name_flip_domains(self):
        for attrs, fields in [({'st_dev':0},['dev']),({'st_ino':0},['ino']),({'st_dev':0,'st_ino':0},['dev','ino'])]:
            with self.subTest(fields=fields):
                self.refuse('hi', fields, windows=True, opened=self.info(**attrs))
                self.first.read.assert_not_called()
        zero = self.info(st_dev=0, st_ino=0)
        self.assertEqual(self.read(windows=True, name=zero, opened=self.info())[0], b'abc')
        for attr, field in [('st_dev','dev'),('st_ino','ino')]:
            for a,b in [(0,99),(99,0)]:
                initial=self.info(**{attr:a}); changed=self.info(**{attr:b})
                self.refuse('nc',[field],windows=True,name=initial,opened=self.info(),names=[initial,changed])
                before={'x':(budget._signature(initial),b'a')}; after={'x':(budget._signature(changed),b'ab')}
                with self.assertRaises(budget.ObservationError) as caught: budget._new_api_commands(before,after,'gh')
                self.assertEqual(caught.exception.reason,'na')

    def test_own_handle_and_name_full_stability_and_reopen_subset(self):
        attrs=[('dev','st_dev',12),('ino','st_ino',23),('mode','st_mode',stat.S_IFREG|0o600),('size','st_size',4),('mtimeNs','st_mtime_ns',101),('ctimeNs','st_ctime_ns',201)]
        initial=self.info()
        for field,attr,value in attrs:
            changed=self.info(**{attr:value})
            with self.subTest(field=field):
                self.refuse('hc',[field],windows=True,firsts=[initial,changed])
                self.refuse('nc',[field],windows=True,names=[initial,changed])
                self.refuse('hc',[field],windows=True,seconds=[initial,changed])
                self.refuse('hc',[field],windows=True,firsts=[initial,initial,changed])
                self.refuse('nc',[field],windows=True,names=[initial,initial,initial,changed])
                if field in ('dev','ino','size','mtimeNs'):
                    self.refuse('ro',[field],windows=True,seconds=[changed,changed])
                    self.second.read.assert_not_called()
                else:
                    self.assertEqual(self.read(windows=True,seconds=[changed,changed])[0],b'abc')
        self.refuse('hc',['dev'],windows=True,firsts=[initial,self.info(st_dev=0)])
        zero_ino = self.info(st_ino=0)
        self.refuse('ro',['ino'],windows=True,seconds=[zero_ino,zero_ino])

    def test_type_reparse_caps_lengths_and_replacement_prevent_reads(self):
        from unittest.mock import Mock
        for info,reason in [(self.info(st_mode=stat.S_IFLNK|0o777),'nr'),(self.info(st_mode=stat.S_IFDIR|0o700),'nr'),
                            (self.info(st_file_attributes=0x400),'rp'),(self.info(st_size=-1),'sz'),(self.info(st_size=101),'sz')]:
            with self.subTest(reason=reason):
                self.refuse(reason,['size'] if reason=='sz' else [],windows=True,name=info)
                self.opens.assert_not_called()
                self.refuse(reason,['size'] if reason=='sz' else [],windows=True,opened=info)
                self.first.read.assert_not_called()
        self.refuse('sz',['size'],data=b'x'*101)
        self.refuse('ln',['size'],data=b'ab')
        changed_name = self.info(st_file_attributes=0x400)
        self.refuse('rp',windows=True,names=[self.info(),self.info(),changed_name,changed_name])
        self.assertEqual(self.opens.call_count,1)
        replacement_ino = self.info(st_ino=99)
        self.refuse('ro',['ino'],windows=True,seconds=[replacement_ino,replacement_ino])
        self.second.read.assert_not_called()
        path=Mock(); fd_error=OSError('SECRET fdopen')
        with patch.object(budget.os,'open',return_value=555) as opened, patch.object(budget.os,'fdopen',side_effect=fd_error), patch.object(budget.os,'close') as close:
            with self.assertRaises(OSError) as caught: budget._open_bytes(path)
            self.assertIs(caught.exception,fd_error); close.assert_called_once_with(555)
            self.assertTrue(opened.call_args.args[1] & getattr(os,'O_NOFOLLOW',0) or not hasattr(os,'O_NOFOLLOW'))

    def test_cleanup_primary_precedence_and_fixed_redaction(self):
        self.refuse('io',close_error=True)
        self.first.close.assert_called_once()
        self.refuse('hc',['ctimeNs'],firsts=[self.info(),self.info(st_ctime_ns=999)],close_error=True)
        self.first.close.assert_called_once()
        try:
            raise RuntimeError('outer handled exception')
        except RuntimeError:
            self.refuse('io',close_error=True)
        for typ in budget._EXCEPTION_TYPE_LABELS:
            error = typ('utf-8', b'\xff', 0, 1, 'SECRET') if typ is UnicodeDecodeError else typ('SECRET')
            self.assertEqual(budget._exception_label(error),typ.__name__)
        class SecretSubclass(ValueError): pass
        self.assertEqual(budget._exception_label(SecretSubclass('SECRET')),'other')
        self.assertEqual(budget._exception_label(budget.ObservationError('nc',['ino'])),'other')
        self.assertNotIn('SECRET',str(budget.ObservationError('SECRET',['SECRET'])))


class PrecisionLogTests(unittest.TestCase):
    setUp = LogsTests.setUp
    append = LogsTests.append
    observe = LogsTests.observe

    def reason(self, text, expected, **kwargs):
        self.file.unlink(missing_ok=True)
        result=self.observe(text,**kwargs)
        self.assertIsNone(result)
        self.assertEqual(self.diagnostics['productInvocations'][-1]['reason'],expected)

    def test_exact_markers_warning_truncation_and_lower_bound_unit(self):
        self.assertEqual(self.observe(START+COMPLETE),0)
        self.assertEqual(self.diagnostics['productGhApiCommandsUnit'],'retained-prestart-exec-records-lower-bound')
        self.assertIsNone(self.diagnostics['httpRequests'])
        self.assertEqual(self.diagnostics['httpRequestsReason'],'transport-unobserved')
        warning=f'12:34:56.791 {"Warning":<5} [subprocess] process failed to start\n'
        self.assertEqual(self.observe(START+api(self.gh)+warning+COMPLETE),1)
        for text,reason in [(START,'ii'),(COMPLETE,'im'),(START+START+COMPLETE,'ia'),(START+COMPLETE+COMPLETE,'ia'),
                            (START.replace('dispatch=Cli','dispatch=Tui')+COMPLETE,'im'),
                            (START+START.replace('dispatch=Cli','dispatch=Tui')+COMPLETE,'ia'),
                            (START+COMPLETE.replace('completed','BROKEN'),'ii'),
                            (START+'12:34:56.790 Warn  [x] no\n'+COMPLETE,'gr'),
                            (START+api(self.gh).rstrip('\n')+'… truncated\n'+COMPLETE,'gr'),
                            (START+'12:34:56.790 Debug [subprocess] exec: unknown api [truncated]\n'+COMPLETE,'gr'),
                            (START+'12:34:56.790 Debug [subprocess] exec: unknown api\n'+COMPLETE,'ex')]:
            with self.subTest(expected=reason): self.reason(text,reason)
        self.reason(START,'ii',role='old-app')
        self.assertEqual(self.observe(START+COMPLETE,role='old-app'),0)

    def parse_reason(self,chunks,maximum=None):
        after={name:((11,22,0,0,0,0),data) for name,data in chunks}
        with contextlib.ExitStack() as stack:
            if maximum is not None:stack.enter_context(patch.object(budget,'MAX_LINES',maximum))
            try:budget._new_api_commands({},after,self.gh)
            except budget.ObservationError as error:return error.reason
        self.fail('Expected a precise parser refusal')

    def test_rf2_whole_chunk_before_line_caps_and_sorted_file_precedence(self):
        long=b'x'*32769+b'\n'
        # Both repaired members remain valid UTF8 and retain exactly one final LF.
        self.assertEqual(self.parse_reason([('a',long+b'\xff\n')]),'gr')
        self.assertEqual(self.parse_reason([('a',long+b'v\n')]),'be')
        lines=(START+COMPLETE).encode()
        self.assertEqual(self.parse_reason([('a',lines[:-1])],maximum=1),'gr')
        self.assertEqual(self.parse_reason([('a',lines)],maximum=1),'be')
        for chunks in [[('a',long),('b',b'\xff\n')],[('b',b'\xff\n'),('a',long)]]:
            self.assertEqual(self.parse_reason(chunks),'be')
        self.assertEqual(self.parse_reason([('a',(START+api(Path('/unknown/gh'))+COMPLETE).encode()),('b',b'invalid\n')]),'gr')

    def test_namespace_sorted_validation_read_and_nonappend_precedence(self):
        from unittest.mock import Mock
        def ordered(reverse):
            real=budget.os.scandir
            def scan(path):
                with real(path) as entries: items=list(entries)
                return contextlib.nullcontext(iter(sorted(items,key=lambda x:x.name,reverse=reverse)))
            return scan
        a=self.logs/'skillview-2026-10-28-001.log'; b=self.logs/'skillview-2026-10-28-002.log'
        a.write_bytes(b'invalid\n'); b.write_bytes(b'')
        for reverse in (False,True):
            scan=ordered(reverse)
            with patch.object(budget.os,'scandir',side_effect=scan),patch.object(budget,'_regular_bytes',side_effect=OSError('SECRET')):
                with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot(self.env,self.root)
                self.assertEqual(caught.exception.reason,'lr')
            foreign=self.logs/'foreign';foreign.write_bytes(b'')
            with patch.object(budget.os,'scandir',side_effect=scan),patch.object(budget,'MAX_LOG_FILE',0),patch.object(budget,'_regular_bytes') as reader:
                with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot(self.env,self.root)
                self.assertEqual(caught.exception.reason,'lr');reader.assert_not_called()
            foreign.unlink()
        foreign=self.logs/'foreign';foreign.write_bytes(b'')
        with patch.object(budget,'MAX_FILES',1):
            with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot(self.env,self.root)
            self.assertEqual(caught.exception.reason,'be')
        before={'a':((1,1,0,0,0,0),b'old')}; after={'a':((1,1,0,0,0,0),b'invalid\n')}
        with self.assertRaises(budget.ObservationError) as caught:budget._new_api_commands(before,after,self.gh)
        self.assertEqual(caught.exception.reason,'na')

    def test_private_lookup_states_unsafe_ancestors_and_zero_snapshot_recheck(self):
        from unittest.mock import Mock
        self.assertEqual(budget._log_snapshot(self.env,self.root).state,'present')
        other=dict(self.env,XDG_CACHE_HOME=str(self.root/'absent'),LOCALAPPDATA=str(self.root/'absent'),HOME=str(self.root/'absent'))
        self.assertEqual(budget._log_snapshot(other,self.root).state,'absent')
        outside=dict(self.env,XDG_CACHE_HOME='/OUTSIDE',LOCALAPPDATA='/OUTSIDE',HOME='/OUTSIDE')
        with patch.object(Path,'lstat',side_effect=AssertionError('must not look outside')):
            with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot(outside,self.root)
            self.assertEqual(caught.exception.reason,'oc')
        with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot({},self.root)
        self.assertEqual(caught.exception.reason,'le')
        self.file.write_bytes(b'')
        real=budget._log_entries
        calls=[]
        def entries(path):
            value=real(path);calls.append(1)
            if len(calls)==2:
                sig=list(value[self.file.name]);sig[0]=0 if sig[0] else 99;value[self.file.name]=tuple(sig)
            return value
        with patch.object(budget,'_log_entries',side_effect=entries):
            with self.assertRaises(budget.ObservationError) as caught:budget._log_snapshot(self.env,self.root)
            self.assertEqual(caught.exception.reason,'lr')
        with patch.object(budget,'_log_snapshot',side_effect=AssertionError('proof host must not read logs')):
            with self.counter.observe(self.env,self.gh,'proof-host','kind',self.root):pass
        self.assertEqual(self.diagnostics['productInvocations'][-1]['reason'],'ur')

    def test_208_rows_43_contexts_dense_counters_and_publication_no_duplicate(self):
        for index in range(208):self.counter._record('old-app' if index%3==0 else 'app','case',0)
        self.assertEqual(len(self.counter.rows),208)
        self.assertEqual(self.diagnostics['productGhApiCommands'],0)
        self.assertFalse(self.diagnostics['invocationBudgetExceeded'])
        self.assertEqual(sum(sum(m.values()) for m in self.counter._reasons.values()),208)
        historical={}; counts=budget.ProductCommands(historical)
        for role,n in [('app',188),('old-app',12),('proof-host',8)]:
            for _ in range(n):counts._record(role,'case',None if role=='proof-host' else 1)
        self.assertEqual(len(counts.rows),208);self.assertEqual(historical['knownProductGhApiCommands'],200)
        self.assertIsNone(historical['productGhApiCommands']);self.assertFalse(historical['invocationBudgetExceeded'])
        self.assertEqual(43*14,602);self.assertLess(602,budget.MAX_INVOCATIONS)
        for _ in range(43):
            with counts.observe({},self.gh,'proof-host','retirement',self.root):pass
        self.assertTrue(counts)
        with patch.object(self.counter,'_publish',side_effect=OSError('SECRET')):
            primary=RuntimeError('primary')
            with self.assertRaises(RuntimeError) as caught:
                with self.counter.observe(self.env,self.gh,'app','case',self.root):raise primary
            self.assertIs(caught.exception,primary)
        self.assertEqual(len(self.counter.rows),209)
        self.assertTrue(self.diagnostics['productObservationPublicationFailed'])
        self.assertNotIn('SECRET',json.dumps(self.diagnostics))
        with patch.object(budget,'MAX_INVOCATIONS',209):self.counter._record('app','case',0)
        self.assertTrue(self.counter.overflow);self.assertEqual(len(self.counter.rows),209)
        self.assertEqual(len(self.diagnostics['productObservationReasonCounts']),4)
        self.assertEqual(len(self.diagnostics['productObservationReasonCounts']['app']),20)


class DecisionTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
        self.home=self.root/'home';self.home.mkdir();self.gh=self.root/'gh';self.gh.write_bytes(b'fake gh')
        self.diagnostics={'directGhCommands':{'skillInstall':1}}
        self.cache=budget.InstallTemplates(self.root/'templates',self.diagnostics)
    install=TemplateTests.install
    def row(self):return self.diagnostics['templateDecisions']['rows'][-1]
    def capture(self):
        self.install(self.home)
        self.assertTrue(self.cache.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{}))
    def test_initial_empty_enabled_coordinates_and_exact_complete_totals(self):
        empty={};budget.InstallTemplates(self.root/'empty',empty)
        self.assertIn('templateDecisions',empty)
        self.assertEqual(empty['templateDecisions']['totals'],dict(accepted=0,refused=0,error=0))
        self.capture();row=self.row();self.assertEqual(row['phase'],'capture');self.assertNotIn('fallbackRequired',row)
        self.assertEqual((row['toolOrdinal'],row['keyOrdinal'],row['installCount']),(1,1,1))
        target=self.root/'target';target.mkdir()
        self.assertIsNone(self.cache.reuse(self.gh,False,'skills/gh-skill',target))
        self.assertEqual(self.row()['reason'],'tm');self.assertTrue(self.row()['fallbackRequired'])
        c=self.diagnostics['templateDecisions']
        self.assertEqual(c['observedDecisions'],2);self.assertEqual(c['totals'],dict(accepted=1,refused=1,error=0))
        self.assertEqual(len(c['reasonCounts']),28);self.assertEqual(len(c['fieldCounts']),6);self.assertEqual(len(c['exceptionTypeCounts']),10)
        self.assertNotIn(str(self.root),json.dumps(c));self.assertNotIn('fake gh',json.dumps(c))

    def event(self,reason='ok',fields=(),error=None):
        self.cache._decisions.observe('capture',self.gh,True,'key',reason,fields,'done','empty',error)

    def test_rf1_overflow_events_counters_totals_and_separate_encodings(self):
        for _ in range(129):self.event()
        c=self.diagnostics['templateDecisions'];self.assertEqual(c['observedDecisions'],129)
        self.assertEqual(c['outcomeCounts']['accepted'],129);self.assertEqual(c['reasonCounts']['ok'],129)
        self.assertLessEqual(len(c['rows']),128);self.assertTrue(c['truncated']);self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))
        wrapper={'commandDiagnostics':{'templateDecisions':c}}
        self.assertLessEqual(len(json.dumps(wrapper,separators=(',',':')).encode()),32768)
        self.assertLessEqual(len(json.dumps(wrapper,indent=2).encode()),32768)
        other={'directGhCommands':{'skillInstall':0}}; small=budget.InstallTemplates(self.root/'small',other)
        with patch.object(budget,'MAX_TEMPLATE_DECISIONS',1):
            for _ in range(2):small._decisions.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        self.assertEqual(len(other['templateDecisions']['rows']),1)
        self.assertEqual(other['templateDecisions']['observedDecisions'],2)
        # Prime private counters, never add a public range override.
        d=self.cache._decisions;d._observed=2**63-1;d._reasons['ok']=2**63-1
        self.event();c=self.diagnostics['templateDecisions']
        self.assertIsNone(c['observedDecisions']);self.assertIsNone(c['reasonCounts']['ok'])
        self.assertTrue(c['counterOverflow']);self.assertIsNotNone(c['outcomeCounts']['accepted'])
        self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))

    def test_byte_guard_indentation_not_whole_product_report_and_minimal_failure(self):
        self.event();d=self.cache._decisions;c=self.diagnostics['templateDecisions']
        compact=len(json.dumps({'commandDiagnostics':{'templateDecisions':c}},separators=(',',':')).encode())
        indented=len(json.dumps({'commandDiagnostics':{'templateDecisions':c}},indent=2).encode())
        self.assertGreater(indented,compact)
        with patch.object(budget,'MAX_TEMPLATE_DIAGNOSTIC_BYTES',(compact+indented)//2):self.event()
        c=self.diagnostics['templateDecisions'];self.assertTrue(c['truncated'])
        self.assertLessEqual(len(json.dumps({'commandDiagnostics':{'templateDecisions':c}},indent=2).encode()),(compact+indented)//2)
        with patch.object(d,'_component',side_effect=OSError('SECRET')):d._safe_publish()
        c=self.diagnostics['templateDecisions'];self.assertTrue(c['publicationFailed']);self.assertIsNone(c['reasonCounts']);self.assertIsNone(c['totals'])
        self.assertNotIn('SECRET',json.dumps(c))

    def test_fallback_receipt_precedes_failed_actual_caller_and_quarantine_is_fatal(self):
        # Original install AST is exercised unchanged by TemplateTests. This failing
        # wrapper uses that exact same three-statement block and nine parameters.
        module=ast.parse((ROOT/'tests/native/prove-registry-recorded-restore.py').read_text())
        damage=next(n for n in ast.walk(module) if isinstance(n,ast.For) and isinstance(n.target,ast.Name) and n.target.id=='damage')
        start=next(i for i,n in enumerate(damage.body) if isinstance(n,ast.Assign) and isinstance(n.targets[0],ast.Name) and n.targets[0].id=='before_install')
        nodes=copy.deepcopy(damage.body[start:start+3]);params=['templates','gh','pinned','source','home','case_root','damage','command','install']
        fn=ast.FunctionDef(name='setup',args=ast.arguments(posonlyargs=[],args=[ast.arg(arg=n) for n in params],kwonlyargs=[],kw_defaults=[],defaults=[]),body=nodes,decorator_list=[])
        ns={};exec(compile(ast.fix_missing_locations(ast.Module(body=[fn],type_ignores=[])),'<fake-fallback>','exec'),ns)
        primary=RuntimeError('primary')
        def command(argv):
            self.assertEqual(self.row()['reason'],'tm');self.assertTrue(self.row()['fallbackRequired']);self.assertEqual(self.row()['installCount'],1)
            self.diagnostics['directGhCommands']['skillInstall']+=1
            raise primary
        with self.assertRaises(RuntimeError) as caught:ns['setup'](self.cache,self.gh,True,'skills/gh-skill',self.home,self.root,'missing-empty',command,[])
        self.assertIs(caught.exception,primary);self.assertEqual(self.diagnostics['directGhCommands']['skillInstall'],2)
        self.capture();target=self.root/'target';target.mkdir()
        def partial(source,destination,manifest):
            (destination/'partial').write_bytes(b'untouched failure evidence')
            raise OSError('SECRET COPY')
        with patch.object(budget,'_copy_entries',side_effect=partial),patch.object(Path,'rename',side_effect=OSError('SECRET QUARANTINE')):
            with self.assertRaisesRegex(RuntimeError,'^Template quarantine failed$') as caught:self.cache.reuse(self.gh,True,'skills/gh-skill',target)
        self.assertTrue(caught.exception.__suppress_context__)
        self.assertEqual((target/'partial').read_bytes(),b'untouched failure evidence')
        rows=self.diagnostics['templateDecisions']['rows'];self.assertEqual(rows[-2]['reason'],'cf');self.assertFalse(rows[-2]['fallbackRequired'])
        self.assertEqual(rows[-1]['reason'],'qf');self.assertNotIn('fallbackRequired',rows[-1]);self.assertNotIn('SECRET',json.dumps(rows))

    def test_recording_fault_does_not_change_copy_registration_used_or_primary(self):
        with patch.object(self.cache._decisions,'observe',side_effect=OSError('SECRET')):
            try:
                self.capture()
                target=self.root/'target';target.mkdir()
                self.assertIsNotNone(self.cache.reuse(self.gh,True,'skills/gh-skill',target))
            except Exception:
                self.fail('Diagnostic exception escaped the result boundary')
        self.assertEqual(len(self.cache.used),1)
        self.assertEqual(budget._home_manifest(target),budget._home_manifest(self.home))
        self.assertTrue(self.diagnostics['templateDecisions']['publicationFailed'])
        self.assertEqual(self.diagnostics['templateDecisions']['totals'],dict(accepted=None,refused=None,error=None))
        self.assertNotIn('SECRET',json.dumps(self.diagnostics))

    def test_reachable_refusals_have_fixed_stage_home_state_and_no_source_emitter(self):
        self.assertFalse(self.cache.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,None))
        self.assertEqual((self.row()['reason'],self.row()['stage'],self.row()['homeState']),('hu','before-home','unobserved'))
        self.assertFalse(self.cache.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{'x':{}}))
        self.assertEqual(self.row()['reason'],'hn')
        before=self.diagnostics['templateDecisions']['observedDecisions']
        self.assertIsNone(self.cache.reuse(self.gh,True,'OTHER SOURCE',self.home))
        self.assertEqual(self.diagnostics['templateDecisions']['observedDecisions'],before)
        self.install(self.home,context=str(self.root))
        self.assertFalse(self.cache.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{}))
        self.assertEqual((self.row()['reason'],self.row()['stage']),('rc','context'))
        for reason in budget._TEMPLATE_REASONS:
            self.event(reason,('ino',) if reason=='nc' else (),ValueError('SECRET') if reason=='ot' else None)
            self.assertEqual(self.row()['reason'],reason)
            self.assertEqual(self.row()['outcome'],budget._outcome(reason))
        self.assertNotIn('SECRET',json.dumps(self.diagnostics['templateDecisions']))



class AdditionalGuardPairsTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
        self.home=self.root/'home';self.home.mkdir();self.gh=self.root/'gh';self.gh.write_bytes(b'fake gh')
        self.diagnostics={'directGhCommands':{'skillInstall':1}}
        self.cache=budget.InstallTemplates(self.root/'templates',self.diagnostics)
    install=TemplateTests.install
    def row(self):return self.diagnostics['templateDecisions']['rows'][-1]
    def capture(self):
        return self.cache.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{})

    def test_capture_layout_registry_key_copy_and_existing_capacity_sites(self):
        self.assertFalse(self.capture());self.assertEqual((self.row()['reason'],self.row()['stage']),('ly','required-layout'))
        self.install(self.home)
        lock=self.home/'.agents/.skill-lock.json'; good=lock.read_bytes();lock.write_bytes(b'INVALID JSON')
        self.assertFalse(self.capture());self.assertEqual((self.row()['reason'],self.row()['stage']),('rg','registry-json'))
        lock.write_bytes(good)
        with patch.object(budget,'_regular_bytes',side_effect=budget.ObservationError('nh',['ctimeNs'])):
            self.assertFalse(self.capture())
        self.assertEqual((self.row()['reason'],self.row()['fields'],self.row()['stage']),('nh',['ctimeNs'],'key'))
        real=budget._regular_bytes;reads=[]
        def raw(path,maximum):
            if path==lock:
                reads.append(1)
                if len(reads)==2:raise budget.ObservationError('nc',['ino'])
            return real(path,maximum)
        with patch.object(budget,'_regular_bytes',side_effect=raw):self.assertFalse(self.capture())
        self.assertEqual((self.row()['reason'],self.row()['fields'],self.row()['stage']),('nc',['ino'],'registry-read'))
        with patch.object(budget,'_copy_entries',side_effect=OSError('SECRET')):self.assertFalse(self.capture())
        self.assertEqual((self.row()['reason'],self.row()['stage']),('cf','copy'))
        # Failed capture store is retained; a fresh cache can still capture once.
        self.cache=budget.InstallTemplates(self.root/'fresh-cache',self.diagnostics)
        self.assertTrue(self.capture());self.assertFalse(self.capture());self.assertEqual(self.row()['reason'],'te')
        other=budget.InstallTemplates(self.root/'capacity',self.diagnostics);other.templates={str(i):None for i in range(4)}
        self.assertFalse(other.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{}))
        self.assertEqual(self.row()['reason'],'tc')
        self.assertNotIn('SECRET',json.dumps(self.diagnostics))

    def test_reuse_store_manifest_used_nonempty_and_typed_home_sites(self):
        self.install(self.home);self.assertTrue(self.capture())
        key=self.cache.key(self.gh,True,'skills/gh-skill');parent=self.cache.root/key
        target=self.root/'target';target.mkdir();(target/'.agents').mkdir()
        self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',target));self.assertEqual(self.row()['reason'],'hn')
        empty=self.root/'empty';empty.mkdir()
        with patch.object(budget,'_home_manifest',side_effect=budget.ObservationError('lk')):
            self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',empty))
        self.assertEqual((self.row()['reason'],self.row()['stage'],self.row()['homeState']),('lk','home','unobserved'))
        real_stat=Path.lstat
        def store_stat(path):
            if path==parent:raise OSError('SECRET store')
            return real_stat(path)
        with patch.object(Path,'lstat',store_stat):
            self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',empty))
        self.assertEqual((self.row()['reason'],self.row()['stage'],self.row()['outcome']),('cf','store','error'))
        extra=parent/'extra';extra.write_bytes(b'never copied')
        self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',empty));self.assertEqual(self.row()['reason'],'ts');extra.unlink()
        raw=(parent/'manifest.json').read_bytes();(parent/'manifest.json').write_bytes(b'{}')
        self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',empty));self.assertEqual(self.row()['reason'],'mm')
        (parent/'manifest.json').write_bytes(raw)
        self.assertIsNotNone(self.cache.reuse(self.gh,True,'skills/gh-skill',empty));second=self.root/'second';second.mkdir()
        self.assertIsNone(self.cache.reuse(self.gh,True,'skills/gh-skill',second));self.assertEqual(self.row()['reason'],'tu')

    def test_home_bounds_special_type_and_links_keep_fixed_reasons(self):
        self.install(self.home)
        with patch.object(budget,'MAX_TEMPLATE_ENTRIES',1):
            with self.assertRaises(budget.ObservationError) as caught:budget._home_manifest(self.home)
            self.assertEqual(caught.exception.reason,'tc')
        with patch.object(budget,'MAX_TEMPLATE_BYTES',0):
            with self.assertRaises(budget.ObservationError) as caught:budget._home_manifest(self.home)
            self.assertEqual(caught.exception.reason,'sz')
        real=Path.lstat;resource=self.home/'.agents/skills/gh-skill/resource'
        def lstat(path):return FileDomainsTests.info(st_mode=stat.S_IFSOCK) if path==resource else real(path)
        with patch.object(Path,'lstat',lstat):
            with self.assertRaises(budget.ObservationError) as caught:budget._home_manifest(self.home)
            self.assertEqual(caught.exception.reason,'en')
        if os.name!='nt':
            bad=self.home/'.agents/skills/gh-skill/bad';bad.symlink_to('/OUTSIDE SECRET')
            self.assertFalse(self.capture());self.assertEqual(self.row()['reason'],'lk')
            bad.unlink()
        (self.home/'.config').mkdir()
        self.assertFalse(self.capture());self.assertEqual(self.row()['reason'],'ly')

    def test_quarantine_failures_mode_rename_mkdir_preserve_evidence_without_fallback(self):
        self.install(self.home);self.assertTrue(self.capture())
        real_lstat=Path.lstat;real_mkdir=Path.mkdir
        for name in ('mode','rename','mkdir'):
            target=self.root/('target-'+name);target.mkdir()
            def partial(source,destination,manifest):
                (destination/'partial').write_bytes(b'failure-original')
                raise OSError('SECRET COPY')
            def lstat(path):
                if path==target:raise OSError('SECRET MODE')
                return real_lstat(path)
            def mkdir(path,*args,**kwargs):
                if path==target:raise OSError('SECRET MKDIR')
                return real_mkdir(path,*args,**kwargs)
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(budget,'_copy_entries',side_effect=partial))
                # Mode failure must occur only after copy begins, not HOME admission.
                if name=='mode':
                    def partial_mode(source,destination,manifest):
                        (destination/'partial').write_bytes(b'failure-original')
                        stack.enter_context(patch.object(Path,'lstat',lstat))
                        raise OSError('SECRET COPY')
                    stack.enter_context(patch.object(budget,'_copy_entries',side_effect=partial_mode))
                elif name=='rename':stack.enter_context(patch.object(Path,'rename',side_effect=OSError('SECRET RENAME')))
                else:stack.enter_context(patch.object(Path,'mkdir',mkdir))
                with self.assertRaisesRegex(RuntimeError,'^Template quarantine failed$'):self.cache.reuse(self.gh,True,'skills/gh-skill',target)
            rows=self.diagnostics['templateDecisions']['rows'];self.assertEqual(rows[-1]['reason'],'qf');self.assertFalse(rows[-2]['fallbackRequired'])
            self.assertFalse(self.cache.used)
            originals=list(self.root.glob('refused-template-home-*')) if name=='mkdir' else [target]
            self.assertTrue(any((p/'partial').read_bytes()==b'failure-original' for p in originals))
        self.assertNotIn('SECRET',json.dumps(self.diagnostics))

    def test_private_counter_scalars_and_publication_failure_keep_totals_unknown(self):
        d=self.cache._decisions
        d._outcomes['accepted']=2**63-1;d._fields['ino']=2**63-1;d._types['ValueError']=2**63-1
        d.observe('capture',self.gh,True,'key','ok',['ino'],'done','empty',ValueError('SECRET'))
        c=self.diagnostics['templateDecisions'];self.assertTrue(c['counterOverflow'])
        self.assertIsNone(c['outcomeCounts']['accepted']);self.assertIsNone(c['fieldCounts']['ino']);self.assertIsNone(c['exceptionTypeCounts']['ValueError'])
        self.assertEqual(c['observedDecisions'],1);self.assertEqual(c['reasonCounts']['ok'],1)
        self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))
        product={};counts=budget.ProductCommands(product);counts._reasons['app']['ok']=2**63-1;counts._record('app','kind',0)
        self.assertIsNone(product['productObservationReasonCounts']['app']['ok']);self.assertIsNone(product['productGhApiCommands'])


class MoreFileAndLogPairsTests(unittest.TestCase):
    def test_raw_read_fstat_open_close_faults_never_publish_data_and_keep_cleanup(self):
        from unittest.mock import Mock
        info=FileDomainsTests.info();path=Mock();path.lstat.return_value=info
        for failure in ('open','read','fstat','second-fstat','second-close'):
            first=Mock();first.fileno.return_value=10;first.read.return_value=b'abc'
            second=Mock();second.fileno.return_value=20
            def fstat(fd):
                if failure=='fstat' or failure=='second-fstat' and fd==20:raise OSError('SECRET fstat')
                return info
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(budget,'IS_WINDOWS',True))
                opens=stack.enter_context(patch.object(budget,'_open_bytes',side_effect=OSError('SECRET open') if failure=='open' else [first,second]))
                stack.enter_context(patch.object(budget.os,'fstat',side_effect=fstat))
                if failure=='read':first.read.side_effect=OSError('SECRET read')
                if failure=='second-close':second.close.side_effect=OSError('SECRET close')
                with self.assertRaises(budget.ObservationError) as caught:budget._regular_bytes(path,100)
                self.assertEqual(caught.exception.reason,'io');self.assertNotIn('SECRET',str(caught.exception))
            if failure!='open':first.close.assert_called_once()
            if failure in ('second-fstat','second-close'):second.close.assert_called_once()
            second.read.assert_not_called()

    def test_zero_flips_namespace_and_all_precedence_are_enumeration_invariant(self):
        with tempfile.TemporaryDirectory() as name:
            root=Path(name);logs=root/'cache/SkillView/logs';logs.mkdir(parents=True)
            env=dict(HOME=str(root/'home'),XDG_CACHE_HOME=str(root/'cache'),LOCALAPPDATA=str(root/'cache'))
            (root/'home').mkdir()
            a=logs/'skillview-2026-10-28-001.log';b=logs/'skillview-2026-10-28-002.log'
            real_scan=budget.os.scandir; real_reader=budget._regular_bytes; real_snapshot=budget._log_snapshot
            for order in (False,True):
                def scan(path):
                    with real_scan(path) as children:items=sorted(list(children),key=lambda x:x.name,reverse=order)
                    return contextlib.nullcontext(iter(items))
                for replace, fault, expected in ((False,True,'lr'),(False,False,'gr'),(True,True,'lr'),(True,False,'na')):
                    with self.subTest(reverse=order,replace=replace,fault=fault):
                        a.write_bytes(b'old retained bytes\n');b.write_bytes(b'')
                        phase=dict(body=False,before=0,after=0,afterFault=0)
                        def snapshot(env,case_root):
                            phase['after' if phase['body'] else 'before']+=1
                            return real_snapshot(env,case_root)
                        def reader(path,maximum):
                            if path==b:
                                self.assertTrue(phase['body'])
                                phase['afterFault']+=1
                                raise OSError('SECRET later read')
                            return real_reader(path,maximum)
                        with patch.object(budget,'IS_WINDOWS',False),patch.object(budget,'IS_DARWIN',False),patch.object(budget.os,'scandir',side_effect=scan),patch.object(budget,'_log_snapshot',side_effect=snapshot):
                            c={};counter=budget.ProductCommands(c)
                            # Outer stack deliberately outlives observe's finally.
                            with contextlib.ExitStack() as after_patch:
                                with counter.observe(env,'gh','app','kind',root):
                                    self.assertEqual(phase['before'],1)
                                    self.assertEqual(phase['after'],0)
                                    phase['body']=True
                                    if fault:after_patch.enter_context(patch.object(budget,'_regular_bytes',side_effect=reader))
                                    if replace:a.write_bytes(b'nonappend grammar\n')
                                    else:
                                        with a.open('ab') as stream:stream.write(b'bad grammar\n')
                        self.assertEqual((phase['before'],phase['after']),(1,1))
                        self.assertEqual(phase['afterFault'],1 if fault else 0)
                        self.assertEqual(c['productInvocations'][-1]['reason'],expected)
                        self.assertEqual(c['productObservationReasonCounts']['app'][expected],1)
                        if fault:
                            self.assertEqual(c['productInvocations'][-1]['reason'],'lr')
                            self.assertEqual(c['productObservationReasonCounts']['app']['lr'],1)
                        self.assertNotIn('SECRET',json.dumps(c));self.assertNotIn(str(root),json.dumps(c))



class SourceReviewContractTests(unittest.TestCase):
    setUp = LogsTests.setUp
    append = LogsTests.append

    @staticmethod
    def expected_maps():
        roles=('app','extension','old-app','proof-host')
        reasons=('ok','ln','nh','hc','nc','ro','hi','ml','le','oc','lr','be','na','gr','ex','im','ia','ii','ur','ot')
        fields=('ctimeNs','dev','ino','mode','mtimeNs','size')
        types_=('OSError','PermissionError','FileNotFoundError','NotADirectoryError','IsADirectoryError','ValueError','TypeError','UnicodeError','UnicodeDecodeError','other')
        return ({r:{k:0 for k in reasons} for r in roles},
                {r:{k:0 for k in fields} for r in roles},
                {r:{k:0 for k in types_} for r in roles})

    def assert_maps(self, diagnostics, expected):
        for key,want,cardinality in zip(('productObservationReasonCounts','productObservationFieldCounts','productObservationExceptionTypeCounts'),expected,(80,24,40)):
            actual=diagnostics[key]
            self.assertEqual(actual,want)
            self.assertEqual(set(actual),{'app','extension','old-app','proof-host'})
            self.assertEqual(sum(len(group) for group in actual.values()),cardinality)
            for role,group in actual.items():
                self.assertEqual(set(group),set(want[role]))
                for value in group.values():self.assertIs(type(value),int)
        self.assertIs(type(diagnostics['invocationBudgetExceeded']),bool)
        self.assertIs(type(diagnostics['productObservationPublicationFailed']),bool)
        self.assertIsNone(diagnostics['httpRequests'])
        self.assertEqual(diagnostics['httpRequestsReason'],'transport-unobserved')
        self.assertNotIn(str(self.root),json.dumps(diagnostics))
        self.assertNotIn('SECRET',json.dumps(diagnostics))

    def test_observe_absent_absent_and_created_positive_window(self):
        real=budget._log_snapshot
        for create in (False,True):
            with self.subTest(create=create):
                self.file.unlink(missing_ok=True)
                if self.logs.exists():self.logs.rmdir()  # Exact owned test leaf only.
                states=[]
                def snapshot(env,case_root):
                    value=real(env,case_root);states.append(value.state);return value
                diagnostics={};counts=budget.ProductCommands(diagnostics)
                with patch.object(budget,'_log_snapshot',side_effect=snapshot):
                    with counts.observe(self.env,self.gh,'app','created',self.root):
                        self.assertEqual(states,['absent'])
                        if create:
                            self.logs.mkdir(mode=0o700)
                            self.append(START+api(self.gh)+COMPLETE)
                self.assertEqual(states,['absent','present' if create else 'absent'])
                row=diagnostics['productInvocations'][-1]
                self.assertEqual(row['reason'],'ok' if create else 'ml')
                self.assertEqual(diagnostics['productObservationReasonCounts']['app'][row['reason']],1)
                if create:
                    self.assertEqual(row['count'],1);self.assertIs(type(row['count']),int)
                    self.assertEqual(diagnostics['knownProductGhApiCommands'],1)
                    self.assertEqual(diagnostics['productGhApiCommands'],1)
                else:self.assertIsNone(row['count'])
                self.assertEqual(diagnostics['productGhApiCommandsUnit'],'retained-prestart-exec-records-lower-bound')
                self.assertIsNone(diagnostics['httpRequests'])

    def test_observe_old_markers_are_not_new_evidence_or_recounted(self):
        old=(START+api(self.gh)+COMPLETE).encode();self.file.write_bytes(old)
        states=[];real=budget._log_snapshot
        def snapshot(env,case_root):
            value=real(env,case_root);states.append(value.state);return value
        with patch.object(budget,'_log_snapshot',side_effect=snapshot):
            with self.counter.observe(self.env,self.gh,'app','old-window',self.root):pass
        self.assertEqual(states,['present','present'])
        row=self.diagnostics['productInvocations'][-1]
        self.assertEqual(row['reason'],'im');self.assertIsNone(row['count'])
        self.assertEqual(self.diagnostics['productObservationReasonCounts']['app']['im'],1)
        self.assertEqual(self.diagnostics['knownProductGhApiCommands'],0)
        self.assertEqual(self.file.read_bytes(),old)
        with self.counter.observe(self.env,self.gh,'app','new-window',self.root):self.append(START+api(self.gh)+COMPLETE)
        self.assertEqual(self.diagnostics['productInvocations'][-1]['count'],1)
        self.assertEqual(self.diagnostics['knownProductGhApiCommands'],1)
        self.assertEqual(self.diagnostics['productObservationReasonCounts']['app']['ok'],1)
        self.assertIsNone(self.diagnostics['httpRequests'])

    def test_dense_typed_product_maps_actual_fields_all_roles_and_error_level(self):
        self.file.write_bytes(b'')
        expected=self.expected_maps();real_snapshot=budget._log_snapshot
        ordinal=0
        for role in ('app','extension','old-app'):
            for reason,fields in [('hc',('ctimeNs','mode')),('nc',('ino',)),('nh',('dev','mode')),('ro',('ino','mtimeNs')),('hi',('dev','ino')),('sz',('size',))]:
                with self.subTest(role=role,reason=reason):
                    phase=dict(body=False,before=0,after=0,fault=0)
                    def snapshot(env,case_root):
                        phase['after' if phase['body'] else 'before']+=1
                        return real_snapshot(env,case_root)
                    def refused(path,maximum):
                        self.assertTrue(phase['body']);phase['fault']+=1
                        raise budget.ObservationError(reason,fields)
                    with patch.object(budget,'_log_snapshot',side_effect=snapshot),contextlib.ExitStack() as stack:
                        with self.counter.observe(self.env,self.gh,role,'typed',self.root):
                            self.assertEqual(phase['before'],1);self.assertEqual(phase['after'],0)
                            phase['body']=True
                            stack.enter_context(patch.object(budget,'_regular_bytes',side_effect=refused))
                    self.assertEqual((phase['before'],phase['after'],phase['fault']),(1,1,1))
                    published='be' if reason=='sz' else reason
                    expected[0][role][published]+=1
                    for field in fields:expected[1][role][field]+=1
                    expected[2][role]['other']+=1
                    self.assert_maps(self.diagnostics,expected)
                    ordinal+=1;row=self.diagnostics['productInvocations'][-1]
                    self.assertEqual(row['ordinal'],ordinal);self.assertIs(type(row['ordinal']),int)
                    self.assertEqual(row['reason'],published);self.assertIsNone(row['count'])
        with patch.object(budget,'_log_snapshot',side_effect=AssertionError('proof-host must not read logs')):
            with self.counter.observe(self.env,self.gh,'proof-host','typed',self.root):pass
        expected[0]['proof-host']['ur']+=1
        self.assert_maps(self.diagnostics,expected)
        self.assertEqual(self.diagnostics['productObservationFieldCounts']['proof-host'],dict(ctimeNs=0,dev=0,ino=0,mode=0,mtimeNs=0,size=0))
        # Every produced level, including Error, uses the real minimum width5.
        text=START+api(self.gh)+f'12:34:56.795 {"Warning":<5} [subprocess] start failed\n'+f'12:34:56.796 {"Error":<5} [subprocess] completed failure\n'+COMPLETE
        with self.counter.observe(self.env,self.gh,'app','levels',self.root):self.append(text)
        expected[0]['app']['ok']+=1;self.assert_maps(self.diagnostics,expected)
        row=self.diagnostics['productInvocations'][-1];self.assertEqual(row['count'],1);self.assertIs(type(row['count']),int)
        self.assertIs(type(self.diagnostics['knownProductGhApiCommands']),int)

    def test_raw_allowlisted_after_exceptions_have_exact_product_type_counters(self):
        self.file.write_bytes(b'');expected=self.expected_maps();real=budget._log_snapshot
        errors=[OSError('SECRET'),PermissionError('SECRET'),FileNotFoundError('SECRET'),NotADirectoryError('SECRET'),IsADirectoryError('SECRET'),ValueError('SECRET'),TypeError('SECRET'),UnicodeError('SECRET'),UnicodeDecodeError('utf-8',b'\xff',0,1,'SECRET')]
        labels=['OSError','PermissionError','FileNotFoundError','NotADirectoryError','IsADirectoryError','ValueError','TypeError','UnicodeError','UnicodeDecodeError']
        for error,label in zip(errors,labels):
            with self.subTest(label=label):
                phase=dict(before=0,after=0)
                def before(env,case_root):phase['before']+=1;return real(env,case_root)
                def after(env,case_root):phase['after']+=1;raise error
                with patch.object(budget,'_log_snapshot',side_effect=before),contextlib.ExitStack() as stack:
                    with self.counter.observe(self.env,self.gh,'app','raw-types',self.root):
                        self.assertEqual(phase['before'],1)
                        stack.enter_context(patch.object(budget,'_log_snapshot',side_effect=after))
                self.assertEqual(phase,dict(before=1,after=1))
                reason='lr' if isinstance(error,OSError) else 'ot'
                expected[0]['app'][reason]+=1;expected[2]['app'][label]+=1
                self.assert_maps(self.diagnostics,expected)
                self.assertEqual(self.diagnostics['productInvocations'][-1]['reason'],reason)

    def test_number_bool_rejection_published_nulls_and_normal_exact_types(self):
        self.assertIsNone(budget._number(True));self.assertIsNone(budget._number(False))
        self.assertIsNone(budget._number(-1));self.assertIsNone(budget._number(2**63))
        self.assertEqual(budget._number(0),0);self.assertIs(type(budget._number(0)),int)
        self.file.write_bytes(b'')
        with self.counter.observe(self.env,self.gh,'app','normal',self.root):self.append(START+api(self.gh)+COMPLETE)
        row=self.diagnostics['productInvocations'][-1]
        for value in (row['count'],row['ordinal'],self.diagnostics['productGhApiCommands'],self.diagnostics['knownProductGhApiCommands'],self.diagnostics['unobservedInvocations']):self.assertIs(type(value),int)
        for value in (self.diagnostics['invocationBudgetExceeded'],self.diagnostics['productObservationPublicationFailed']):self.assertIs(type(value),bool)
        for counter,key,public in [(self.counter._reasons['app'],'ok','productObservationReasonCounts'),(self.counter._fields['app'],'ino','productObservationFieldCounts'),(self.counter._types['app'],'OSError','productObservationExceptionTypeCounts')]:
            counter[key]=True;self.counter._safe_publish()
            self.assertIsNone(self.diagnostics[public]['app'][key])
        diag={'directGhCommands':{'skillInstall':0}};cache=budget.InstallTemplates(self.root/'types',diag)
        cache._decisions.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        component=diag['templateDecisions'];decision=component['rows'][0]
        self.assertIs(type(decision['pinned']),bool);self.assertIs(type(decision['installCount']),int)
        for key in ('truncated','publicationFailed','counterOverflow'):self.assertIs(type(component[key]),bool)
        for key in ('observedDecisions',):self.assertIs(type(component[key]),int)
        for key in ('reasonCounts','outcomeCounts','fieldCounts','exceptionTypeCounts','totals'):
            for value in component[key].values():self.assertIs(type(value),int)

    def test_nullable_install_count_rows_survive_uncertainty_and_keep_authority(self):
        class Unreadable(dict):
            blocked=True
            def __getitem__(self,key):
                if key=='directGhCommands' and self.blocked:raise OSError('SECRET unreadable counter')
                return super().__getitem__(key)
        cases=[{},Unreadable(),{'directGhCommands':{'skillInstall':-1}},{'directGhCommands':{'skillInstall':2**63}},{'directGhCommands':{'skillInstall':True}},{'directGhCommands':{'skillInstall':False}}]
        for i,diag in enumerate(cases):
            with self.subTest(case=i):
                cache=budget.InstallTemplates(self.root/f'count-{i}',diag)
                cache._decisions.observe('capture',self.gh,True,'key','ok',(),'done','empty')
                c=diag['templateDecisions'];self.assertEqual(len(c['rows']),1)
                self.assertIsNone(c['rows'][0]['installCount'])
                self.assertTrue(c['truncated']);self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))
                self.assertIs(type(c['counterOverflow']),bool)
                self.assertEqual(c['counterOverflow'],i in (2,3))
                if isinstance(diag,Unreadable):diag.blocked=False
                diag['directGhCommands']={'skillInstall':7}
                cache._decisions.observe('capture',self.gh,True,'key','ok',(),'done','empty')
                c=diag['templateDecisions'];self.assertEqual(len(c['rows']),2)
                self.assertEqual(c['rows'][1]['installCount'],7)
                self.assertIs(type(c['rows'][1]['installCount']),int)
                self.assertEqual(c['observedDecisions'],2)
                self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))
                wrapper={'commandDiagnostics':{'templateDecisions':c}}
                self.assertLessEqual(len(json.dumps(wrapper,separators=(',',':')).encode()),32768)
                self.assertLessEqual(len(json.dumps(wrapper,indent=2).encode()),32768)
                self.assertNotIn('SECRET',json.dumps(c));self.assertNotIn(str(self.root),json.dumps(c))
        # Missing counters also cannot change an actual admitted fake-file copy.
        self.gh.parent.mkdir();self.gh.write_bytes(b'fake gh fixture')
        # Reproduce Darwin's log-home shape even on Linux; it is not a template.
        (self.home/'Library/Caches/SkillView/logs').mkdir(parents=True,exist_ok=True)
        refused_diag={};refused=budget.InstallTemplates(self.root/'log-home-refusal',refused_diag)
        self.assertFalse(refused.capture(self.gh,True,'skills/gh-skill',self.home,self.root,self.root,{}))
        self.assertEqual([row['reason'] for row in refused_diag['templateDecisions']['rows']],['ly'])
        template_home=self.root/'template-home';template_home.mkdir()
        TemplateTests.install(self,template_home)
        diag={};cache=budget.InstallTemplates(self.root/'real-copy-null-count',diag)
        self.assertTrue(cache.capture(self.gh,True,'skills/gh-skill',template_home,self.root,self.root,{}))
        target=self.root/'copy-target';target.mkdir()
        self.assertIsNotNone(cache.reuse(self.gh,True,'skills/gh-skill',target))
        self.assertEqual(len(cache.used),1)
        self.assertEqual(budget._home_manifest(target),budget._home_manifest(template_home))
        self.assertEqual(len(diag['templateDecisions']['rows']),2)
        for row in diag['templateDecisions']['rows']:self.assertIsNone(row['installCount'])

    def test_nullable_tool_key_capacity_rows_and_known_followups_remain_bounded(self):
        for i,(tools_full,keys_full) in enumerate(((True,False),(False,True),(True,True))):
            with self.subTest(tools=tools_full,keys=keys_full):
                diag={'directGhCommands':{'skillInstall':4}};cache=budget.InstallTemplates(self.root/f'ids-{i}',diag);d=cache._decisions
                if tools_full:d._tools={f'tool-{j}':j+1 for j in range(128)}
                if keys_full:d._keys={f'key-{j}':j+1 for j in range(128)}
                d.observe('capture',self.gh,True,'new-key','ok',(),'done','empty')
                c=diag['templateDecisions'];self.assertEqual(len(c['rows']),1)
                if tools_full:self.assertIsNone(c['rows'][0]['toolOrdinal'])
                else:self.assertEqual(c['rows'][0]['toolOrdinal'],1)
                if keys_full:self.assertIsNone(c['rows'][0]['keyOrdinal'])
                else:self.assertEqual(c['rows'][0]['keyOrdinal'],1)
                self.assertTrue(c['truncated']);self.assertEqual(c['totals'],dict(accepted=None,refused=None,error=None))
                d.observe('capture','tool-0' if tools_full else self.gh,True,'key-0' if keys_full else 'new-key','ok',(),'done','empty')
                c=diag['templateDecisions'];self.assertEqual(len(c['rows']),2)
                self.assertEqual((c['rows'][1]['toolOrdinal'],c['rows'][1]['keyOrdinal']),(1,1))
                self.assertEqual(c['observedDecisions'],2);self.assertLessEqual(len(d._tools),128);self.assertLessEqual(len(d._keys),128)
                self.assertLessEqual(len(json.dumps({'commandDiagnostics':{'templateDecisions':c}},indent=2).encode()),32768)

    def test_real_row_and_byte_bounds_still_close_storage_after_restoring_limits(self):
        diag={'directGhCommands':{'skillInstall':0}};cache=budget.InstallTemplates(self.root/'row-close',diag);d=cache._decisions
        with patch.object(budget,'MAX_TEMPLATE_DECISIONS',1):
            for _ in range(2):d.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        self.assertEqual(len(diag['templateDecisions']['rows']),1)
        d.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        self.assertEqual(len(diag['templateDecisions']['rows']),1);self.assertEqual(diag['templateDecisions']['observedDecisions'],3)
        diag2={'directGhCommands':{'skillInstall':0}};cache2=budget.InstallTemplates(self.root/'byte-close',diag2);d=cache2._decisions
        d.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        bound=len(json.dumps({'commandDiagnostics':{'templateDecisions':diag2['templateDecisions']}},indent=2).encode())-1
        with patch.object(budget,'MAX_TEMPLATE_DIAGNOSTIC_BYTES',bound):d.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        stored=len(diag2['templateDecisions']['rows']);self.assertLess(stored,2)
        d.observe('capture',self.gh,True,'key','ok',(),'done','empty')
        self.assertEqual(len(diag2['templateDecisions']['rows']),stored)
        self.assertEqual(diag2['templateDecisions']['observedDecisions'],3)
        self.assertEqual(diag2['templateDecisions']['totals'],dict(accepted=None,refused=None,error=None))


if __name__ == '__main__':
    unittest.main()
