"""Owned byte-only/fake PTY and receipt tests. Never launches SkillView or real gh."""
import contextlib
import copy
import errno
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import measure_inventory as measure


class ImmediateThread:
    def __init__(self, target, **unused):
        self.target = target
        self.ident = None
        self.joined = None
    def start(self):
        self.ident = 1
        self.target()
    def join(self, timeout):
        self.joined = timeout
    def is_alive(self):
        return False


class SilentThread(ImmediateThread):
    def start(self):
        self.ident = 1


class FailingStartThread(ImmediateThread):
    def start(self):
        raise RuntimeError("private worker start text")


class UnsettledThread(ImmediateThread):
    def is_alive(self):
        return True


def owner_ok():
    return dict(pid=123, declaredPgid=123, declaredSid=123, quitRequestedNs=10,
                exitObservedNs=20, actualExit=0, naturalExitObserved=True,
                terminalEof=True, rawBytes=42, waiterJoined=True, fallbackEntered=False,
                cleanupReturned=True, status='settled', firstFailure='none', errorType=None)


def pty_ok(local=False):
    result = dict(status='observed', cleanExit=True, owner=owner_ok(), inputsUnchanged=True,
                  shellSamples=1, firstShellPaintMs=1.0, inputResponseUpperBoundMs=2.0)
    if local:
        for key in ('navigation', 'filtering'):
            result[key] = dict(measure.summary(list(range(100, 130))), status='observed')
        result['localActivity'] = dict(capturesBefore=1, capturesAfter=1,
                                       loggedChildrenBefore=54, loggedChildrenAfter=54)
    else:
        result['tabTransitions'] = dict(measure.summary(list(range(100, 130))), status='measured',
                                       capturesBefore=1, capturesAfter=1)
    return result


class CloseTests(unittest.TestCase):
    def fake(self, root, code=0):
        terminal = measure.Terminal.__new__(measure.Terminal)
        terminal.output = root / 'pty'
        terminal.master = 71
        terminal.screen = SimpleNamespace(text='frozen endpoint cells', feed=Mock(side_effect=AssertionError('must not decode shutdown')))
        terminal.raw = io.BytesIO()
        terminal.bytes_read = 7
        terminal.process = SimpleNamespace(pid=123, returncode=None)
        def wait():
            terminal.process.returncode = code
            return code
        terminal.process.wait = Mock(side_effect=wait)
        terminal.send = Mock()
        return terminal

    def close(self, terminal, reads, thread=ImmediateThread, clock=None, fallback=None):
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(measure.threading, 'Thread', thread))
            select = stack.enter_context(patch.object(measure.select, 'select', return_value=([71], [], [])))
            read = stack.enter_context(patch.object(measure.os, 'read', side_effect=reads))
            close = stack.enter_context(patch.object(measure.os, 'close'))
            stop = stack.enter_context(patch.object(measure, 'stop_tree', side_effect=fallback))
            if clock is not None:
                stack.enter_context(patch.object(measure.time, 'monotonic', side_effect=clock))
            receipt = terminal.close()
        return receipt, read, close, stop

    def test_zero_exit_requires_raw_eof_and_keeps_measured_cells_unchanged(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d))
            receipt, read, close, stop = self.close(terminal, [b'\x1b[<uSHUTDOWN', b''])
            self.assertEqual('settled', receipt['status'])
            self.assertEqual(0, receipt['actualExit'])
            self.assertTrue(receipt['naturalExitObserved'])
            self.assertTrue(receipt['terminalEof'])
            self.assertTrue(receipt['waiterJoined'])
            self.assertEqual(7 + len(b'\x1b[<uSHUTDOWN'), receipt['rawBytes'])
            terminal.process.wait.assert_called_once_with()
            terminal.send.assert_called_once_with(b'\x11')
            terminal.screen.feed.assert_not_called()
            close.assert_called_once_with(71)
            stop.assert_not_called()
            self.assertEqual('frozen endpoint cells', terminal.output.with_suffix('.screen.txt').read_text())
            self.assertEqual('unknown', json.loads(terminal.output.with_suffix('.owner-start.json').read_text())['status'])
            self.assertEqual(receipt, json.loads(terminal.output.with_suffix('.owner.json').read_text()))
            self.assertEqual(receipt, terminal.close())  # No second signal or wait.

    def test_linux_eio_is_terminal_eof_not_a_read_failure(self):
        with tempfile.TemporaryDirectory() as d:
            receipt, _, _, stop = self.close(self.fake(Path(d)), [OSError(errno.EIO, 'secret-path')])
            self.assertEqual('settled', receipt['status'])
            self.assertEqual('none', receipt['firstFailure'])
            stop.assert_not_called()

    def test_nonzero_exit_is_failed_and_fallback_cannot_promote_it(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d), code=23)
            receipt, _, _, stop = self.close(terminal, [b''])
            self.assertEqual(23, receipt['actualExit'])
            self.assertTrue(receipt['naturalExitObserved'])
            self.assertEqual('failed', receipt['status'])
            self.assertTrue(receipt['fallbackEntered'])
            self.assertIsNone(terminal.exit_code)
            stop.assert_called_once_with(terminal.process, terminal.raw)

    def test_raw_cap_uses_already_measured_bytes_and_retains_first_failure(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d));terminal.bytes_read = measure._PTY_TRANSCRIPT_LIMIT
            receipt, _, _, stop = self.close(terminal, [b'X'], fallback=OSError('private-secret'))
            self.assertEqual('output-cap', receipt['firstFailure'])
            self.assertIsNone(receipt['errorType'])
            self.assertEqual(measure._PTY_TRANSCRIPT_LIMIT + 1, receipt['rawBytes'])
            self.assertNotEqual('settled', receipt['status'])
            self.assertFalse(receipt['cleanupReturned'])
            stop.assert_called_once()

    def test_read_and_quit_errors_are_fixed_and_cannot_be_masked_by_fallback(self):
        for quit_error in (False, True):
            with self.subTest(quit_error=quit_error), tempfile.TemporaryDirectory() as d:
                terminal = self.fake(Path(d))
                if quit_error:terminal.send.side_effect = OSError('credential-and-path')
                receipt, _, _, stop = self.close(terminal, [OSError(errno.EBADF, 'credential-and-path')],
                                               fallback=RuntimeError('second-secret'))
                self.assertEqual('quit-write-error' if quit_error else 'read-error', receipt['firstFailure'])
                self.assertEqual('OSError', receipt['errorType'])
                self.assertNotIn('credential', json.dumps(receipt))
                self.assertNotIn('second-secret', json.dumps(receipt))
                self.assertNotEqual('settled', receipt['status'])
                stop.assert_called_once()

    def test_one_shared_five_second_deadline_does_not_restart_at_eof(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d))
            # 4.9 seconds used before reading the last bytes; only .1 remains.
            receipt, _, _, stop = self.close(terminal, [b'pending'], clock=[0, 4.9, 5.01])
            self.assertEqual(5, measure._QUIT_SECONDS)
            self.assertEqual('quit-deadline', receipt['firstFailure'])
            self.assertFalse(receipt['terminalEof'])
            self.assertNotEqual('settled', receipt['status'])
            stop.assert_called_once()

    def test_eof_without_observed_exit_times_out_and_zero_fallback_is_not_natural(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d))
            def fallback(process, unused):process.returncode = 0
            with patch.object(measure.threading.Event, 'wait', return_value=False):
                receipt, _, _, stop = self.close(terminal, [b''], thread=SilentThread, clock=[0, 0, 0], fallback=fallback)
            self.assertEqual('quit-deadline', receipt['firstFailure'])
            self.assertEqual(0, receipt['actualExit'])
            self.assertFalse(receipt['naturalExitObserved'])
            self.assertTrue(receipt['fallbackEntered'])
            self.assertNotEqual('settled', receipt['status'])
            stop.assert_called_once()

    def test_wait_error_and_unjoined_worker_are_unknown_without_secret_type_names(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d));terminal.process.wait.side_effect = RuntimeError('hidden wait path')
            receipt, _, _, _ = self.close(terminal, [b''])
            self.assertEqual('wait-error', receipt['firstFailure'])
            self.assertEqual('RuntimeError', receipt['errorType'])
            self.assertEqual('unknown', receipt['status'])
        with tempfile.TemporaryDirectory() as d:
            receipt, _, _, _ = self.close(self.fake(Path(d)), [b''], thread=UnsettledThread)
            self.assertFalse(receipt['waiterJoined'])
            self.assertEqual('waiter-unsettled', receipt['firstFailure'])
            self.assertEqual('unknown', receipt['status'])

    def test_worker_start_failure_is_fixed_and_never_sends_quit_to_unobserved_owner(self):
        with tempfile.TemporaryDirectory() as d:
            terminal = self.fake(Path(d))
            receipt, _, _, stop = self.close(terminal, [], thread=FailingStartThread)
            self.assertEqual('wait-error', receipt['firstFailure'])
            self.assertEqual('RuntimeError', receipt['errorType'])
            self.assertEqual('unknown', receipt['status'])
            terminal.send.assert_not_called()
            stop.assert_called_once()

    def test_receipt_write_failure_never_becomes_clean_success(self):
        for fail_first in (False, True):
            with self.subTest(fail_first=fail_first), tempfile.TemporaryDirectory() as d:
                terminal = self.fake(Path(d))
                original = measure.write_owner
                calls = []
                def write(path, receipt):
                    calls.append(path)
                    if len(calls) == (1 if fail_first else 2):
                        raise OSError('private receipt path')
                    original(path, receipt)
                with patch.object(measure, 'write_owner', side_effect=write):
                    receipt, _, _, _ = self.close(terminal, [b''])
                self.assertEqual('cleanup-error', receipt['firstFailure'])
                self.assertEqual('OSError', receipt['errorType'])
                self.assertNotEqual('settled', receipt['status'])
                self.assertNotIn('private receipt path', json.dumps(receipt))

    def test_exception_labels_use_only_fixed_builtin_names(self):
        class PrivateSecretException(Exception):pass
        self.assertEqual('PermissionError', measure.error_type(PermissionError('secret')))
        self.assertEqual('other', measure.error_type(PrivateSecretException('secret')))

    @unittest.skipUnless(os.name == 'posix', 'Owned byte-only PTY')
    def test_real_backpressured_byte_child_drains_before_natural_exit_without_decoding(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            script = ('import os,tty; tty.setraw(0); os.write(1,b"READY"); '
                      'key=os.read(0,1); assert key==b"\\x11"; '
                      'payload=b"\\x1b[<u"+b"x"*524288; '
                      'p=0\nwhile p<len(payload): p+=os.write(1,payload[p:])\n')
            terminal = measure.Terminal([sys.executable, '-I', '-B', '-c', script], root,
                                        {'PATH': '/usr/bin:/bin', 'HOME': str(root)}, root / 'bytes')
            terminal.wait(lambda text: 'READY' in text, timeout=5)
            before = terminal.screen.text
            with patch.object(measure, 'stop_tree', side_effect=AssertionError('fallback must not be needed')) as stop:
                receipt = terminal.close()
            self.assertEqual('settled', receipt['status'])
            self.assertEqual(0, receipt['actualExit'])
            self.assertEqual(before, terminal.screen.text)
            self.assertGreaterEqual(receipt['rawBytes'], 524288)
            self.assertIn(b'\x1b[<u', (root / 'bytes.ansi').read_bytes())
            stop.assert_not_called()


class QualificationTests(unittest.TestCase):
    def test_late_but_complete_metrics_pass_without_changing_latency_targets(self):
        for local in (False, True):
            self.assertTrue(measure.pty_qualified(pty_ok(local), local))

    def test_each_missing_or_false_owner_witness_refuses(self):
        for key in owner_ok():
            with self.subTest(missing=key):
                r=pty_ok();del r['owner'][key]
                self.assertFalse(measure.pty_qualified(r, False))
        for key, value in [('actualExit', False), ('actualExit', 7), ('naturalExitObserved', False),
                           ('terminalEof', False), ('waiterJoined', False), ('fallbackEntered', True),
                           ('cleanupReturned', False), ('rawBytes', True), ('pid', 0),
                           ('declaredPgid', 999), ('exitObservedNs', 1), ('firstFailure', 'read-error')]:
            with self.subTest(key=key, value=value):
                r=pty_ok();r['owner'][key]=value
                self.assertFalse(measure.pty_qualified(r, False))

    def test_partial_bad_counts_changed_cache_or_unknown_inputs_never_qualify(self):
        for local in (False, True):
            for metric in (['navigation', 'filtering'] if local else ['tabTransitions']):
                for field, value in [('samples', 29), ('samples', True), ('milliseconds', [1]*29),
                                     ('milliseconds', [True]*30), ('milliseconds', [float('nan')]*30),
                                     ('p95Ms', 0), ('status', 'unmeasured')]:
                    r=pty_ok(local);r[metric][field]=value
                    self.assertFalse(measure.pty_qualified(r, local))
            r=pty_ok(local);r['localActivity' if local else 'tabTransitions']['capturesAfter']=2
            self.assertFalse(measure.pty_qualified(r, local))
            r=pty_ok(local);r['inputsUnchanged']=None
            self.assertFalse(measure.pty_qualified(r, local))

    def test_missing_or_nonfinite_shell_observations_are_not_a_complete_baseline(self):
        for key in ('shellSamples', 'firstShellPaintMs', 'inputResponseUpperBoundMs'):
            result = pty_ok();del result[key]
            self.assertFalse(measure.pty_qualified(result, False))
        for key, value in [('shellSamples', True), ('firstShellPaintMs', float('nan')),
                           ('inputResponseUpperBoundMs', -1), ('inputResponseUpperBoundMs', 0)]:
            result = pty_ok();result[key] = value
            self.assertFalse(measure.pty_qualified(result, False))

    def test_primary_observation_failure_survives_owner_failure_without_raw_text(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);terminal=Mock();terminal.wait.side_effect=TimeoutError('private-token-path')
            owner=owner_ok();owner.update(status='failed', firstFailure='cleanup-error')
            terminal.close.return_value=owner
            with patch.object(measure,'Terminal',return_value=terminal),patch.object(measure,'log_text',return_value=''):
                result=measure.pty_measure(Path('/fake/product'),root,dict(copies=200,cwd='project',scanRoots=[]),{},root/'out')
            self.assertEqual('PTY observation did not complete',result['reason'])
            self.assertEqual('TimeoutError',result['measurementErrorType'])
            self.assertEqual('cleanup-error',result['owner']['firstFailure'])
            self.assertNotIn('private-token-path',json.dumps(result))
            self.assertFalse(result['cleanExit'])
            terminal.close.assert_called_once_with(graceful=False)


    def test_primary_failure_survives_unavailable_final_log_receipt(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d);terminal = Mock();terminal.wait.side_effect = TimeoutError('secret predicate')
            terminal.close.return_value = owner_ok()
            with patch.object(measure, 'Terminal', return_value=terminal), \
                    patch.object(measure, 'log_text', side_effect=PermissionError('secret log path')):
                result = measure.pty_measure(Path('/fake/product'), root, dict(copies=200, cwd='project', scanRoots=[]), {}, root/'out')
            self.assertEqual('PTY observation did not complete', result['reason'])
            self.assertEqual('TimeoutError', result['measurementErrorType'])
            self.assertEqual('PermissionError', result['logErrorType'])
            self.assertNotIn('secret', json.dumps(result))


class MainTests(unittest.TestCase):
    def scenario(self, d, *, fail=None, mutate=False, cli_only=False, all_mode=False, after_error=False):
        root=Path(d);repo=root/'repo';repo.mkdir();(repo/'tools').mkdir()
        for name in ['inventory_fixture.py','test_once.py']:(repo/'tools'/name).write_text('fixture source')
        (repo/'global.json').write_text(json.dumps(dict(sdk=dict(version='10.0.403'))))
        binary=root/'binary';binary.mkdir();app=binary/'not-executed';gh=binary/'not-executed-gh';app.write_bytes(b'fake-app');gh.write_bytes(b'fake-gh')
        generated=[];snapshots=[];seen=[];cli=[]
        def generate(path,kind,copies,roots):
            generated.append((path.name,kind,copies,roots));(path/'project').mkdir();m=dict(copies=copies,cwd='project',scanRoots=[],scenarios={});measure.write_json(path/'fixture.json',m);return m,{}
        def snapshot(path,manifest,identities=False):
            snapshots.append((path.name,identities))
            if after_error and len([x for x in snapshots if x[1]])==2:raise PermissionError('private evidence path')
            return [dict(path='authored',identity=2 if mutate and len([x for x in snapshots if x[1]])==2 else 1)]
        def fake_pty(unused_app,path,manifest,env,output,local=False):
            self.assertTrue(output.with_name(output.name+'-inputs-before.json').exists())
            seen.append(path.name);r=pty_ok(local)
            if fail is not None:r=fail(r)
            return r
        def fake_cli(unused_app,path,manifest,env,output,timeout):
            cli.append(path.name);count=manifest['copies'];return dict(status='passed',inventory=dict(rows=count,partial=False)),dict(skills=[None]*count,providers=[dict(succeeded=True)])
        def check(argv,**unused):
            if argv[:3]==['git','rev-parse','HEAD']:return 'a'*40+'\n'
            if argv[:3]==['git','status','--porcelain']:return ''
            return 'gh version fixture\n'
        args=['--app',str(app),'--gh',str(gh),'--expect-head','a'*40,'--output',str(root/'results')]
        args+=['--cli-only'] if cli_only else [] if all_mode else ['--pty-only']
        with contextlib.ExitStack() as stack:
            for name,value in [('ROOT',repo),('generate',generate),('snapshot',snapshot),('pty_measure',fake_pty),('run_cli',fake_cli),('verify_story',lambda *a: {})]:stack.enter_context(patch.object(measure,name,value))
            stack.enter_context(patch.object(measure,'offline_gh',return_value=gh))
            stack.enter_context(patch.object(measure.subprocess,'check_output',side_effect=check))
            stack.enter_context(patch.object(measure.subprocess,'Popen',side_effect=AssertionError('no child may launch')))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            code=measure.main(args)
        return code,json.loads((root/'results/report.json').read_text()),generated,seen,cli

    def test_pty_only_uses_existing_two_scenarios_and_zero_cli_product_calls(self):
        with tempfile.TemporaryDirectory() as d:
            code,r,generated,seen,cli=self.scenario(d)
            self.assertEqual(0,code);self.assertEqual('completed',r['status']);self.assertEqual([],cli)
            self.assertEqual([('typical-pty','performance',200,50),('large-pty','performance',1000,50)],generated)
            self.assertEqual(['typical-pty','large-pty'],seen)
            self.assertEqual('pty-only',r['parameters']['mode'])
            for name in ['typical','large']:
                p=r['measurements'][name]['pty'];self.assertTrue(p['inputsUnchanged'])
                before=Path(p['inputIdentitiesBefore']['path']);after=Path(p['inputIdentitiesAfter']['path'])
                self.assertEqual(before.read_bytes(),after.read_bytes())
                self.assertEqual(measure.digest(before),p['inputIdentitiesBefore']['sha256'])
                self.assertEqual(measure.digest(after),p['inputIdentitiesAfter']['sha256'])

    def test_each_owner_metric_or_cache_failure_stops_before_large_workload(self):
        def broken_owner(r):r['owner']['status']='unknown';r['owner']['waiterJoined']=False;return r
        def broken_metric(r):r['tabTransitions']['milliseconds'].pop();return r
        def broken_cache(r):r['tabTransitions']['capturesAfter']=2;return r
        for fault in [broken_owner,broken_metric,broken_cache]:
            with self.subTest(fault=fault.__name__),tempfile.TemporaryDirectory() as d:
                code,r,generated,seen,cli=self.scenario(d,fail=fault)
                self.assertEqual(1,code);self.assertEqual('incomplete',r['status'])
                self.assertEqual(['typical-pty'],seen);self.assertEqual(1,len(generated));self.assertEqual([],cli)
                self.assertNotIn('large',r['measurements'])
                if fault is broken_owner:self.assertIsNone(r['measurements']['typical']['pty']['inputsUnchanged'])

    def test_changed_inputs_stop_before_next_host(self):
        with tempfile.TemporaryDirectory() as d:
            code,r,generated,seen,cli=self.scenario(d,mutate=True)
            self.assertEqual(1,code);self.assertFalse(r['measurements']['typical']['pty']['inputsUnchanged'])
            self.assertEqual(['typical-pty'],seen);self.assertEqual(1,len(generated))

    def test_primary_measurement_failure_survives_later_identity_evidence_failure(self):
        def primary(result):
            result.update(status='unmeasured', reason='PTY observation did not complete', measurementErrorType='TimeoutError')
            return result
        with tempfile.TemporaryDirectory() as d:
            code, report, generated, seen, cli = self.scenario(d, fail=primary, after_error=True)
            self.assertEqual(1, code)
            result = report['measurements']['typical']['pty']
            self.assertEqual('PTY observation did not complete', result['reason'])
            self.assertEqual('TimeoutError', result['measurementErrorType'])
            self.assertEqual('PermissionError', result['identityErrorType'])
            self.assertIsNone(result['inputsUnchanged'])
            self.assertNotIn('private evidence path', json.dumps(report))
            self.assertEqual(['typical-pty'], seen)

    def test_cli_only_keeps_original_seven_launch_schedule_and_no_pty(self):
        with tempfile.TemporaryDirectory() as d:
            code,r,generated,seen,cli=self.scenario(d,cli_only=True)
            self.assertEqual(0,code);self.assertEqual([],seen)
            self.assertEqual(['messy']+['typical']*5+['large'],cli)
            self.assertTrue(all(r['measurements'][name]['pty']['status']=='not-run' for name in ['typical','large']))

    def test_default_cohort_failure_keeps_cli_observations_and_never_starts_large(self):
        def failure(result):result['owner']['fallbackEntered']=True;return result
        with tempfile.TemporaryDirectory() as d:
            code, report, generated, seen, cli = self.scenario(d, fail=failure, all_mode=True)
            self.assertEqual(1, code)
            self.assertEqual('incomplete', report['status'])
            self.assertEqual(['messy']+['typical']*5, cli)
            self.assertEqual(['typical-pty'], seen)
            self.assertNotIn('large', report['measurements'])
            self.assertTrue(report['measurements']['typical']['inputsUnchanged'])
            self.assertTrue(report['measurements']['typical']['pty']['inputsUnchanged'])

    def test_cli_only_and_pty_only_are_mutually_exclusive_before_setup(self):
        with patch.object(measure,'make_output',side_effect=AssertionError('must not set up')),contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as e:measure.main(['--app','unused','--expect-head','a'*40,'--cli-only','--pty-only'])
            self.assertEqual(2,e.exception.code)


if __name__=='__main__':unittest.main()
