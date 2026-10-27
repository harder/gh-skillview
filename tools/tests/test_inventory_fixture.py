"""Fixture evidence, isolation and observer regressions; no live gh or AI clients."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import inventory_fixture as fixture
import measure_inventory as measure


class InventoryFixtureTests(unittest.TestCase):
    def test_story_is_reproducible_and_physical_relationships_are_real(self):
        manifests = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                story, _ = fixture.generate(root)
                try:
                    self.assertEqual(12, len(story['scenarios']))
                    shared = story['scenarios']['shared-payload']
                    if shared['status'] == 'created':
                        paths = [root / p for p in shared['paths']]
                        self.assertTrue(all(os.path.samefile(paths[0], p) for p in paths[1:]))
                        self.assertFalse((root / 'home/.agents/skills/broken-link').exists())
                        self.assertTrue((root / 'home/.agents/skills/broken-link').is_symlink())
                    one, two = [root / p for p in story['scenarios']['independent-duplicates']['paths']]
                    self.assertFalse(os.path.samefile(one, two))
                    self.assertEqual((one / 'SKILL.md').read_bytes(), (two / 'SKILL.md').read_bytes())
                    one, two = [root / p for p in story['scenarios']['project-override']['paths']]
                    self.assertNotEqual((one / 'SKILL.md').read_bytes(), (two / 'SKILL.md').read_bytes())
                    lock = json.loads((root / 'home/.agents/.skill-lock.json').read_text())['skills']
                    self.assertEqual('v1.2.3', lock['lock-only-pin']['pinnedRef'])
                    self.assertNotIn('pinned', (root / 'home/.agents/skills/lock-only-pin/SKILL.md').read_text())
                    self.assertFalse((root / 'home/.agents/skills/absent-owner').exists())
                    self.assertFalse((root / 'home/.agents/skills/missing-resource/references/missing.md').exists())
                    self.assertEqual([], list((root / 'home/.agents/skills/empty-leftover').iterdir()))
                    denied = story['scenarios']['inaccessible-location']
                    if denied['status'] == 'created':
                        with self.assertRaises(PermissionError):
                            list((root / denied['paths'][0]).iterdir())
                    else:
                        self.assertIn('no denied coverage claimed', denied['reason'])
                    manifests.append(fixture.snapshot(root, story))
                finally:
                    (root / 'inaccessible').chmod(0o700)
                    (root / 'home/.codex/skills/.system/managed').chmod(0o700)
        self.assertEqual(manifests[0], manifests[1])

    def test_unsupported_links_are_labelled_without_substituting_copies(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(Path, 'symlink_to', side_effect=OSError('links forbidden')):
                story, _ = fixture.generate(root)
            try:
                for name in ('shared-payload', 'broken-link'):
                    self.assertEqual('unsupported', story['scenarios'][name]['status'])
                self.assertFalse((root / 'home/.agents/skills/shared').exists())
            finally:
                (root / 'inaccessible').chmod(0o700)
                (root / 'home/.codex/skills/.system/managed').chmod(0o700)

    def test_generation_refuses_existing_inputs_and_preserves_sentinel(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sentinel = root / 'keep'
            sentinel.write_text('existing')
            with self.assertRaises(ValueError):
                fixture.generate(root)
            self.assertEqual('existing', sentinel.read_text())
            self.assertEqual([sentinel], list(root.iterdir()))

    def test_performance_has_exact_counts_and_unique_rows(self):
        for copies in (200, 1000):
            with self.subTest(copies=copies), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                story, _ = fixture.generate(root, 'performance', copies, 50)
                self.assertEqual(50, len(story['scanRoots']))
                files = list((root / 'roots').rglob('SKILL.md'))
                self.assertEqual(copies, len(files))
                self.assertEqual(copies, len({p.parent.name for p in files}))
                self.assertEqual(100, len(fixture.flags(root, story)))

    def test_allowlisted_environment_never_inherits_credentials_or_client_homes(self):
        original = dict(os.environ)
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {
                'GH_TOKEN': 'secret', 'AWS_SECRET_ACCESS_KEY': 'secret', 'LD_PRELOAD': 'foreign',
                'CLAUDE_CODE_PLUGIN_DIRS': 'foreign', 'GIT_DIR': 'foreign', 'SSH_AUTH_SOCK': 'foreign'}):
            root = Path(temporary)
            env = fixture.fixture_environment(root)
            for key in ('GH_TOKEN', 'AWS_SECRET_ACCESS_KEY', 'LD_PRELOAD', 'CLAUDE_CODE_PLUGIN_DIRS', 'GIT_DIR', 'SSH_AUTH_SOCK'):
                self.assertNotIn(key, env)
            for key in ('HOME', 'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'SKILLVIEW_STATE_HOME', 'TMPDIR'):
                self.assertTrue(Path(env[key]).is_relative_to(root))
        self.assertEqual(original, dict(os.environ))

    @unittest.skipUnless(os.name == 'posix', 'POSIX executable adapter')
    def test_offline_adapter_refuses_remote_mutation_and_auth_without_launching_gh(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            real = root / 'must-not-execute'
            real.write_text('#!/bin/sh\nexit 88\n')
            real.chmod(0o700)
            adapter = measure.offline_gh(root, real)
            for argv in (['api', 'user'], ['skill', 'search', 'anything'], ['skill', 'install'], ['auth', 'token']):
                p = subprocess.run([str(adapter), *argv], capture_output=True, timeout=5)
                self.assertEqual(97, p.returncode)
            self.assertEqual(1, subprocess.run([str(adapter), 'auth', 'status'], capture_output=True, timeout=5).returncode)
            self.assertEqual(88, subprocess.run([str(adapter), '--version'], capture_output=True, timeout=5).returncode)

    def test_vt_observer_handles_split_sequences_redraws_and_does_not_reuse_erased_text(self):
        screen = measure.Screen(4, 30)
        screen.feed(b'\x1b[2Jold ready')
        screen.feed(b'\x1b[')
        screen.feed(b'1;1Hnew\x1b[K')
        self.assertNotIn('ready', screen.text)
        self.assertIn('new', screen.text)
        self.assertEqual(b'\x1b[1;4R', screen.feed(b'\x1b[6n'))
        with self.assertRaises(ValueError):
            screen.feed(b'\x1b[4P')

    def test_phase_and_child_evidence_counts_only_instrumented_events(self):
        evidence = measure.log_evidence('[inventory.timing] local discovery and default catalog: 3ms (total 3ms)\n'
                                        '[subprocess] exec: gh skill list --json\n')
        self.assertEqual(1, evidence['inventoryCaptures'])
        self.assertEqual(['gh skill list --json'], evidence['loggedChildren'])
        self.assertEqual(29, measure.summary(list(range(1, 31)))['p95Ms'])

    def test_navigation_observer_rejects_a_shared_title_with_stale_copy_or_group_detail(self):
        root = Path('/private-fixture')
        def frame(lines):
            return '\n'.join([' ' * 160] * 5 + [' ' * 83 + line for line in lines] + [' ' * 160] * 35)
        group = frame(['item-0001', 'Source: Unknown source [root-001]', 'Select an exact copy; source groups cannot run copy actions.'])
        self.assertTrue(measure.selected_detail_matches(group, root, 1, False))
        self.assertFalse(measure.selected_detail_matches(group, root, 1, True))
        copy = frame(['item-0001', '1 physical copy', 'Location: /private-fixture/roots/root-000/item-0000'])
        self.assertFalse(measure.selected_detail_matches(copy, root, 1, True))
        copy = frame(['item-0001', '1 physical copy', 'Location: /private-fixture/roots/root-001/item-0001'])
        self.assertTrue(measure.selected_detail_matches(copy, root, 1, True))
        self.assertFalse(measure.selected_detail_matches(copy, root, 1, False))
        wrapped = frame(['item-0001', '1 physical copy', 'Location: /private-fixture/roots/█│', 'root-001/item-0001 █│'])
        self.assertTrue(measure.selected_detail_matches(wrapped, root, 1, True))

    def test_process_timing_observes_child_exit_before_delayed_parent_notification(self):
        # Model a late parent/GUI wake-up, independently of the child lifetime.
        # A parent wait(timeout) stopwatch would incorrectly include this delay.
        class DelayedEvent:
            def __init__(self):
                self.event = threading.Event()
            def set(self):
                self.event.set()
            def wait(self, timeout):
                done = self.event.wait(timeout)
                if done:
                    time.sleep(.15)
                return done

        original_wait = subprocess.Popen.wait
        def reject_live_timed_poll(process, timeout=None):
            if process.returncode is None:
                self.assertIsNone(timeout, 'A live timed Popen.wait would quantize the endpoint')
            return original_wait(process, timeout=timeout)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / 'child-exiting-ns'
            script = ('import time; from pathlib import Path; Path(' + repr(str(marker))
                      + ').write_text(str(time.perf_counter_ns()))')
            with patch.object(measure, 'threading', SimpleNamespace(Event=DelayedEvent, Thread=threading.Thread)), \
                    patch.object(subprocess.Popen, 'wait', reject_live_timed_poll):
                step = measure.timed_process([sys.executable, '-c', script], cwd=root, env=os.environ.copy(),
                                             stdout=root / 'stdout', stderr=root / 'stderr', timeout=10)
            self.assertEqual('passed', step['status'])
            self.assertTrue(step['naturalExitObserved'])
            self.assertLessEqual(int(marker.read_text()), step['exitObservedNs'])
            self.assertGreater(step['exitObservedNs'], step['startedNs'])
            self.assertLess(step['exitObservedNs'], step['parentResumedNs'] - 100_000_000)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux direct host timeout teardown')
    def test_direct_process_timeout_kills_descendants_without_reporting_forced_exit_as_wall_time(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sentinel = root / 'late'
            child = 'import time; from pathlib import Path; time.sleep(2); Path(' + repr(str(sentinel)) + ').touch()'
            script = ('import subprocess,sys,time; subprocess.Popen([sys.executable,"-c",' + repr(child)
                      + ']); print("ready",flush=True); time.sleep(60)')
            step = measure.timed_process([sys.executable, '-c', script], cwd=root, env=os.environ.copy(),
                                         stdout=root / 'stdout', stderr=root / 'stderr', timeout=.3)
            self.assertEqual('timeout', step['status'])
            self.assertFalse(step['naturalExitObserved'])
            self.assertNotIn('processWallMs', step)
            time.sleep(2.1)
            self.assertFalse(sentinel.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux nested PTY teardown')
    def test_runner_cooperative_timeout_closes_a_nested_pty_session(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sentinel = root / 'late'
            child = 'import time; from pathlib import Path; time.sleep(2); Path(' + repr(str(sentinel)) + ').touch(); time.sleep(60)'
            script = ('import sys,signal; from pathlib import Path; '
                      'sys.path.insert(0,' + repr(str(Path(measure.__file__).parent)) + '); '
                      'from measure_inventory import Terminal\n'
                      'def stop(signum, frame): raise KeyboardInterrupt\n'
                      'signal.signal(signal.SIGTERM,stop)\n'
                      't=Terminal([sys.executable,"-c",' + repr(child) + '],Path(' + repr(str(root)) + '),__import__("os").environ.copy(),Path(' + repr(str(root / 'nested')) + '))\n'
                      'try: t.wait(lambda s: False,timeout=60)\n'
                      'finally: t.close(graceful=False)\n')
            step = measure.execute([sys.executable, '-c', script], cwd=root, env=os.environ.copy(),
                                   log=root / 'driver.log', timeout=.4, cooperative_stop=True)
            self.assertEqual('timeout', step['status'])
            self.assertTrue((root / 'nested.screen.txt').exists())
            time.sleep(2.1)
            self.assertFalse(sentinel.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux owned PTY teardown')
    def test_pty_timeout_kills_owned_descendants_and_keeps_transcript(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sentinel = root / 'late'
            child = 'import time; from pathlib import Path; time.sleep(2); Path(' + repr(str(sentinel)) + ').touch()'
            script = ('import subprocess,sys,time; subprocess.Popen([sys.executable,"-c",' + repr(child)
                      + ']); print("fixture alive",flush=True); time.sleep(60)')
            terminal = measure.Terminal([sys.executable, '-c', script], root, os.environ.copy(), root / 'pty')
            try:
                with self.assertRaises(TimeoutError):
                    terminal.wait(lambda s: 'not here' in s, timeout=.3)
            finally:
                terminal.close(graceful=False)
            self.assertIsNotNone(terminal.process.poll())
            time.sleep(2.1)
            self.assertFalse(sentinel.exists())
            self.assertIn(b'fixture alive', (root / 'pty.ansi').read_bytes())


if __name__ == '__main__':
    unittest.main()
