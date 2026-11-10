"""Early infrastructure check and bounded, non-authoritative CI diagnostics."""
import argparse
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys


IMAGE = 'mcr.microsoft.com/dotnet/sdk:10.0.403'
LABELS = ('registry_tool', 'container_prerequisite') + tuple(
    f'{prefix}_{i:02}' for prefix, count in [('api_primary', 18), ('native_check', 12)]
    for i in range(1, count + 1))


def validate_scheduling(text):
    native = text.split('  aot-proof:\n', 1)[1].split('  aot-smoke:\n', 1)[0]
    policy = ('    concurrency:\n      group: skillview-native-proof-api\n'
              '      queue: max\n      cancel-in-progress: false\n')
    if (native.count(policy) != 1 or len(re.findall(r'^\s*queue:', text, re.M)) != 1
            or native.count('      max-parallel: 1\n') != 1):
        raise ValueError('native proof queue policy changed')


def read_record(path, limit):
    named = path.lstat()
    if not stat.S_ISREG(named.st_mode):
        raise ValueError('invalid diagnostic file')
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
    with os.fdopen(fd, 'rb') as stream:
        before = os.fstat(stream.fileno())
        if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size > limit
                or (named.st_dev, named.st_ino) != (before.st_dev, before.st_ino)):
            raise ValueError('invalid diagnostic file')
        data = stream.read(limit + 1)
        after = os.fstat(stream.fileno())
        if len(data) > limit or (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (
                after.st_size, after.st_mtime_ns, after.st_ctime_ns):
            raise ValueError('unstable diagnostic file')
        return data


def publish(root, name, value):
    root.mkdir(exist_ok=True)
    if not stat.S_ISDIR(root.lstat().st_mode):
        raise ValueError('unsafe diagnostic directory')
    data = (json.dumps(value, separators=(',', ':')) + '\n').encode()
    if len(data) > 16384:
        raise ValueError('diagnostic budget exceeded')
    fd = os.open(root / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)


def classify_http(data):
    """Classify retained response evidence; never grant source/proof authority."""
    text = data.decode('utf-8', errors='replace').replace('\r\n', '\n')
    header, separator, body = text.partition('\n\n')
    status = re.match(r'^HTTP/(?:1\.[01]|2) ([0-9]{3})(?:\s|$)', header)
    if not separator or status is None:
        return 'incomplete-or-invalid-http-evidence'
    code = int(status[1])
    try:
        parsed = json.loads(body)
    except (ValueError, TypeError):
        parsed = None
    message = parsed.get('message', '') if isinstance(parsed, dict) else ''
    if code in (403, 429) and isinstance(message, str) and 'rate limit exceeded' in message.lower():
        return 'explicit-provider-rate-limit'
    if code == 403:
        return 'provider-http-403-cause-unknown'
    if code >= 500:
        return 'provider-http-5xx'
    if code != 200:
        return 'provider-http-non-200'
    if not isinstance(parsed, dict) or not parsed:
        return 'incomplete-or-invalid-http-evidence'
    return 'http-200-json-observed'


def summary(root, steps):
    failures = []
    for label in LABELS:
        value = steps.get(label, {})
        if not isinstance(value, dict) or value.get('outcome') not in ('failure', 'cancelled'):
            continue
        phase = 'infrastructure-prerequisite' if label in LABELS[:2] else (
            'provider-access' if label == 'api_primary_01' else 'native-proof-step')
        failures.append(dict(step=label, outcome=value['outcome'], phase=phase, cause='unknown'))
    observed = dict(schema=1, failures=failures, proofSuccessClaim=False,
                    scope='Diagnostic observations only; original step outcomes and aggregate gates remain authoritative',
                    repositoryResponse='unavailable', exhaustedBudgetObserved=False, latestBudget=None)
    latest_time = -1
    try:
        observed['repositoryResponse'] = classify_http(read_record(root / 'gh-api-access.txt', 32768))
    except (OSError, ValueError):
        pass
    for stage in ('job-start',) + tuple(f'{side}-{i:02}' for i in range(1, 19) for side in ('before', 'after')):
        try:
            record = json.loads(read_record(root / 'api-budget' / f'api-budget-{stage}.json', 768))
            core = record.get('core', {})
            fields = ('limit', 'used', 'remaining', 'reset')
            if (record.get('result') == 'success' and isinstance(core, dict)
                    and all(type(core.get(key)) is int and 0 <= core[key] <= 2**63 - 1 for key in fields)):
                if core['remaining'] == 0:
                    observed['exhaustedBudgetObserved'] = True
                timestamp = record.get('utcNs')
                if type(timestamp) is int and 0 <= timestamp <= 2**63 - 1 and timestamp > latest_time:
                    latest_time = timestamp
                    observed['latestBudget'] = {key: core[key] for key in fields}
        except (OSError, ValueError, TypeError, AttributeError):
            pass
    for failure in failures:
        if failure['phase'] == 'provider-access':
            failure['cause'] = observed['repositoryResponse']
    return observed


def preflight(root):
    # Docker retains responsibility for registry access. No token inspection,
    # quota query, retry, alternate image or proof-success fallback is admitted.
    try:
        result = subprocess.run(['docker', 'pull', '--quiet', IMAGE],
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, timeout=240)
        code = result.returncode
        cause = 'none' if code == 0 else 'docker-pull-failed-cause-unknown'
    except subprocess.TimeoutExpired:
        code, cause = 124, 'docker-pull-timeout'
    except OSError:
        code, cause = 127, 'docker-unavailable'
    try:
        publish(root, 'native-prerequisite.json', dict(schema=1, phase='infrastructure-prerequisite',
                image=IMAGE, actualExit=code, cause=cause, proofSuccessClaim=False))
    except (OSError, ValueError):
        print('{"diagnosticError":"prerequisite-record-unavailable"}', file=sys.stderr)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('preflight', 'summary'))
    action = parser.parse_args().action
    root = Path('dist')
    if action == 'preflight':
        return preflight(root)
    raw = os.environ.get('NATIVE_STEPS_JSON', '{}')
    if len(raw.encode()) > 65536:
        raise ValueError('step metadata budget exceeded')
    steps = json.loads(raw)
    if not isinstance(steps, dict):
        raise ValueError('invalid step metadata')
    publish(root, 'native-ci-summary.json', summary(root, steps))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
