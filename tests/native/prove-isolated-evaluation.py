"""Exercise the real offline Docker protocol, not model or skill quality.

The caller must explicitly pre-pull and supply a local digest/ID. This fixture
never builds/pulls an image or runs a skill script, model, or external API.
It records a transparent Docker CLI wrapper as part of the trusted test runtime.
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


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def snapshot(root):
    result = []
    for path in [root, *sorted(root.rglob('*'))]:
        value = path.lstat()
        assert stat.S_ISREG(value.st_mode) or stat.S_ISDIR(value.st_mode), path
        result.append([str(path.relative_to(root)), value.st_dev, value.st_ino,
                       value.st_ctime_ns, value.st_mode,
                       sha(path) if stat.S_ISREG(value.st_mode) else None])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--docker', required=True, type=Path)
    parser.add_argument('--endpoint', default='unix:///var/run/docker.sock')
    parser.add_argument('--image', required=True)
    parser.add_argument('--image-id', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    assert os.name == 'posix' and Path('/proc/sys/kernel/ostype').read_text().strip() == 'Linux'
    app, docker = args.app.resolve(strict=True), args.docker.resolve(strict=True)
    assert '@sha256:' in args.image and args.image_id.startswith('sha256:')
    assert args.endpoint.startswith('unix:///')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {'schemaVersion': 1, 'status': 'failed',
              'scope': 'Actual Docker protocol/configuration and cleanup fixture; no model, skill-script execution, quality verdict, or escape proof.',
              'app': {'path': str(app), 'sha256': sha(app)},
              'docker': {'path': str(docker), 'sha256': sha(docker)},
              'image': args.image, 'imageId': args.image_id, 'cases': []}
    try:
        with tempfile.TemporaryDirectory(prefix='skillview-eval-proof-', dir='/var/tmp') as temporary:
            root = Path(temporary)
            home, payload, calls = root / 'home', root / 'payload', output / 'docker-calls'
            for path in [home, payload, calls, root / 'tmp']:
                path.mkdir()
            (home / '.claude').mkdir()
            (home / '.claude' / 'settings.json').write_text('{"fixtureSecret":"host-only-canary"}')
            (home / '.codex').mkdir()
            (home / '.codex' / 'config.toml').write_text('fixture_secret = "host-only-canary"\n')
            (payload / 'SKILL.md').write_text('---\nname: protocol-fixture\ndescription: Disposable offline protocol fixture\n---\nRead references/facts.txt. Never execute scripts/poison.sh.\n')
            (payload / 'references').mkdir()
            (payload / 'references' / 'facts.txt').write_text('alpha\nbeta\ngamma\n')
            (payload / 'scripts').mkdir()
            (payload / 'scripts' / 'poison.sh').write_text('#!/bin/sh\necho POISON_EXECUTED\ntouch /work/poison-executed\nexit 97\n')
            (payload / 'scripts' / 'poison.sh').chmod(0o755)
            before, home_before = snapshot(payload), snapshot(home)
            wrapper = root / 'docker-recorder'
            # Only records calls made by the product; argv/environment are passed
            # unchanged to the real Docker CLI. Inspect output is also retained.
            wrapper.write_text('''#!/usr/bin/python3
import json, os, pathlib, subprocess, sys, time
record = pathlib.Path(CALLS) / (str(time.time_ns()) + '.json')
event = {'argv': sys.argv[1:], 'environment': dict(os.environ), 'cwd': os.getcwd()}
record.write_text(json.dumps(event, indent=2) + '\\n')
if 'inspect' in sys.argv[1:]:
    result = subprocess.run([DOCKER, *sys.argv[1:]], capture_output=True, timeout=20)
    event.update(exitCode=result.returncode, stdout=result.stdout.decode(), stderr=result.stderr.decode())
    record.write_text(json.dumps(event, indent=2) + '\\n')
    sys.stdout.buffer.write(result.stdout); sys.stderr.buffer.write(result.stderr)
    sys.exit(result.returncode)
os.execve(DOCKER, [DOCKER, *sys.argv[1:]], dict(os.environ))
'''.replace('CALLS', repr(str(calls))).replace('DOCKER', repr(str(docker))))
            wrapper.chmod(0o755)
            report['dockerRecorderSha256'] = sha(wrapper)
            env = {'PATH': os.environ.get('PATH', os.defpath), 'LANG': 'C.UTF-8',
                   'HOME': str(home), 'USERPROFILE': str(home),
                   'GH_TOKEN': 'disposable-invalid-token', 'GH_CONFIG_DIR': str(root / 'gh'),
                   'GIT_CONFIG_COUNT': '0', 'TMPDIR': str(root / 'tmp'),
                   'SKILLVIEW_STATE_HOME': str(root / 'state'),
                   'CLAUDE_CONFIG_DIR': str(home / '.claude'), 'CODEX_HOME': str(home / '.codex'),
                   'SKILLVIEW_EVAL_HOST_CANARY': 'must-not-enter-runtime'}
            for name in ['CONFIG', 'STATE', 'DATA', 'CACHE']:
                env['XDG_' + name + '_HOME'] = str(root / ('xdg-' + name.lower()))
            gh = shutil.which('gh')
            if gh:
                env.update(GH_EXTENSION='1', GH_PATH=str(Path(gh).resolve()))
            config_path = root / 'evaluation.json'
            fixture = '''set -eu
test "$0" = /input/request.json
test "$(id -u)" = 65534
test "$HOME" = /work
test "$(pwd)" = /work
test -z "${SKILLVIEW_EVAL_HOST_CANARY+x}"
test -z "${GH_TOKEN+x}"
test -z "${GITHUB_TOKEN+x}"
test ! -e /var/run/docker.sock
test ! -e /work/.claude/settings.json
test ! -e /work/.codex/config.toml
grep -q 'Read references/facts.txt' /input/skill/SKILL.md
grep -q 'count-lines' "$0"
grep -q 'first-fact' "$0"
test "$(wc -l < /input/skill/references/facts.txt | tr -d ' ')" = 3
test "$(head -n 1 /input/skill/references/facts.txt)" = alpha
if touch /input/forbidden 2>/dev/null; then exit 81; fi
if touch /forbidden 2>/dev/null; then exit 82; fi
touch /work/writable /tmp/writable
test ! -e /work/poison-executed
printf 'PROTOCOL_FIXTURE_OK\\ncount-lines=3\\nfirst-fact=alpha\\n'
'''

            def product(label, command, expected_zero):
                result = subprocess.run([str(app), '--scan-root', str(payload), *command, '--json'],
                                        cwd=root, env=env, capture_output=True, text=True, timeout=90)
                (output / (label + '.stdout.json')).write_text(result.stdout)
                (output / (label + '.stderr.txt')).write_text(result.stderr)
                document = json.loads(result.stdout)
                report['cases'].append({'case': label, 'argv': command, 'exitCode': result.returncode,
                                        'state': document.get('state')})
                assert (result.returncode == 0) == expected_zero, (label, result.returncode, document, result.stderr)
                assert snapshot(payload) == before and snapshot(home) == home_before, label
                return document

            for scenario, suffix, timeout in [('success', '', 30), ('failure', 'exit 23\n', 30),
                                               ('deadline', 'sleep 60\n', 15)]:
                config = {'schemaVersion': 1, 'dockerExecutable': str(wrapper), 'dockerEndpoint': args.endpoint,
                          'image': args.image, 'imageId': args.image_id, 'entrypoint': '/bin/sh',
                          # Evaluation argv deliberately forbids control characters.
                          # Every fixture command/if statement occupies one complete
                          # line; joining preserves shell syntax without broadening
                          # the product parser's accepted input.
                          'arguments': ['-c', '; '.join((fixture + suffix).splitlines())],
                          'evaluator': 'BusyBox protocol fixture; no model or quality assessment',
                          'context': 'Disposable deterministic protocol/configuration/cleanup assertions only.',
                          'prompts': [{'id': 'count-lines', 'text': 'Count facts.txt lines.'},
                                      {'id': 'first-fact', 'text': 'Read the first fact.'}],
                          'timeoutSeconds': timeout, 'memoryMiB': 64}
                config_path.write_text(json.dumps(config))
                count = len(list(calls.iterdir()))
                review = product(scenario + '-review', ['evaluate-review', '--path', str(payload), '--config', str(config_path)], True)
                assert review['state'] == 'review-only' and not review['executionAttempted'], review
                assert len(list(calls.iterdir())) == count, 'Review launched Docker'
                common = ['evaluate-run', '--path', str(payload), '--config', str(config_path)]
                if scenario == 'success':
                    product('missing-consent', [*common, '--review', review['reviewFingerprint']], False)
                    product('wrong-fingerprint', [*common, '--review', '0' * 64, '--consent-run'], False)
                    assert len(list(calls.iterdir())) == count, 'Refused approval launched Docker'
                result = product(scenario + '-run', [*common, '--review', review['reviewFingerprint'], '--consent-run'], scenario == 'success')
                expected = {'success': 'completed', 'failure': 'failed', 'deadline': 'canceled'}[scenario]
                assert result['state'] == expected and result['executionAttempted'], result
                assert not result['cleanupError'], result
                assert 'PROTOCOL_FIXTURE_OK\ncount-lines=3\nfirst-fact=alpha\n' in result['stdout'], result
                assert 'POISON_EXECUTED' not in result['stdout'] + result['stderr'], result
                assert result['observedRuntime']['imageId'] == args.image_id, result
                assert not result['stdoutTruncated'] and not result['stderrTruncated'], result
                if scenario != 'deadline':
                    assert result['exitCode'] == (0 if scenario == 'success' else 23), result
                observed = [json.loads(path.read_text()) for path in sorted(calls.iterdir())][count:]
                for event in observed:
                    child = event['environment']
                    assert 'GH_TOKEN' not in child and 'SKILLVIEW_EVAL_HOST_CANARY' not in child, event
                    assert child['HOME'] == event['cwd'] and child['HOME'] != str(home), event
                    assert child['DOCKER_CONFIG'] == str(Path(event['cwd']) / 'docker'), event
                    assert not Path(event['cwd']).exists(), 'Private runtime inputs remain'
                container = result['observedRuntime']['containerId']
                assert container and len(container) == 64, result
                # Independent read-only daemon observation after product cleanup.
                remaining = subprocess.run([str(docker), '--host', args.endpoint, 'container', 'ls', '--all',
                                            '--no-trunc', '--filter', 'id=' + container, '--format', '{{.ID}}'],
                                           env=env, cwd=root, capture_output=True, text=True, timeout=20)
                (output / (scenario + '-remaining-containers.txt')).write_text(remaining.stdout + remaining.stderr)
                assert remaining.returncode == 0 and not remaining.stdout.strip(), remaining
            report.update(status='passed', payloadUnchanged=True, privateHomeUnchanged=True,
                          rawSourceSnapshot=before, modelExecuted=False, skillScriptsExecuted=False)
    finally:
        (output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'status': report['status'], 'cases': len(report['cases']), 'output': str(output)}))


if __name__ == '__main__':
    main()
