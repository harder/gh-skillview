"""Best-effort core-budget observations; never a proof gate or request attribution."""
import argparse
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import threading
import time


RIDS = ('win-x64', 'win-arm64', 'osx-arm64', 'linux-x64')
SHARDS = ('owners', 'sources', 'inspection-unpin')
STAGES = ('job-start',) + tuple(f'{side}-{i:02}' for i in range(1, 19) for side in ('before', 'after'))
RESULTS = ('before', 'success', 'failure', 'cancelled', 'skipped')
ERROR_TYPES = ('LaunchError', 'Timeout', 'InvalidResponse', 'CommandError', 'CleanupError')
MAX_RECORD = 768
MAX_RESPONSE = 1024
QUERY_SECONDS = 5
CLEANUP_SECONDS = 1
COMMAND = ('gh', 'api', 'rate_limit', '--jq', '.resources.core | {limit, used, remaining, reset}')


class ObservationError(Exception):
    pass


def query_core():
    # Do not inherit debug output which can contain headers. Proof environments
    # and commands remain untouched; this child is only the diagnostic query.
    env = dict(os.environ)
    env.pop('GH_DEBUG', None)
    try:
        child = subprocess.Popen(COMMAND, env=env, stdin=subprocess.DEVNULL,
                                 stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    except OSError:
        raise ObservationError('LaunchError') from None
    data = []
    errors = []

    def read():
        try:
            data.append(child.stdout.read(MAX_RESPONSE + 1))
        except OSError:
            errors.append(True)

    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    deadline = time.monotonic() + QUERY_SECONDS
    try:
        reader.join(max(0, deadline - time.monotonic()))
        if reader.is_alive():
            raise ObservationError('Timeout')
        if errors or not data or len(data[0]) > MAX_RESPONSE:
            raise ObservationError('InvalidResponse')
        try:
            code = child.wait(timeout=max(0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise ObservationError('Timeout') from None
        if code != 0:
            raise ObservationError('CommandError')
        try:
            value = json.loads(data[0])
            core = {key: value[key] for key in ('limit', 'used', 'remaining', 'reset')}
            if any(type(n) is not int or not 0 <= n <= 2**63 - 1 for n in core.values()):
                raise ValueError()
        except (ValueError, TypeError, KeyError):
            raise ObservationError('InvalidResponse') from None
        return core
    finally:
        # This is our gh query only, never the primary proof or its children.
        if child.poll() is None:
            try:
                child.kill()
            except ProcessLookupError:
                pass
            try:
                child.wait(timeout=CLEANUP_SECONDS)
            except subprocess.TimeoutExpired:
                raise ObservationError('CleanupError') from None
        reader.join(CLEANUP_SECONDS)
        if reader.is_alive():
            raise ObservationError('CleanupError')
        child.stdout.close()


def reserve(stage):
    # Fixed directory/basenames, exclusive regular files, no append or overwrite.
    directory = Path('dist') / 'api-budget'
    for path in (Path('dist'), directory):
        try:
            path.mkdir()
        except FileExistsError:
            pass
        info = path.lstat()
        if not stat.S_ISDIR(info.st_mode):
            raise OSError('unsafe directory')
    total = 0
    count = 0
    with os.scandir(directory) as files:
        for entry in files:
            count += 1
            if count >= len(STAGES):
                raise OSError('record limit')
            info = entry.stat(follow_symlinks=False)
            if (entry.name not in {f'api-budget-{s}.json' for s in STAGES}
                    or not stat.S_ISREG(info.st_mode) or info.st_size > MAX_RECORD):
                raise OSError('unsafe record')
            total += info.st_size
    if total + MAX_RECORD > len(STAGES) * MAX_RECORD:
        raise OSError('byte limit')
    fd = os.open(directory / f'api-budget-{stage}.json',
                 os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    if not stat.S_ISREG(os.fstat(fd).st_mode):
        os.close(fd)
        raise OSError('unsafe file')
    return os.fdopen(fd, 'w', encoding='utf-8')


def observe(stage, rid, shard, proof_result):
    # The CLI constrains these too; validate direct calls before using a path.
    if stage not in STAGES or rid not in RIDS or shard not in SHARDS or proof_result not in RESULTS:
        raise ValueError('invalid fixed label')
    try:
        with reserve(stage) as output:
            record = dict(job='native-proof', rid=rid, shard=shard, stage=stage,
                          utcNs=time.time_ns(), proofResult=proof_result,
                          result='error', errorType=None,
                          core={key: None for key in ('limit', 'used', 'remaining', 'reset')})
            try:
                record['core'] = query_core()
                record['result'] = 'success'
            except ObservationError as error:
                record['errorType'] = str(error) if str(error) in ERROR_TYPES else 'InvalidResponse'
            body = json.dumps(record, separators=(',', ':')) + '\n'
            if len(body.encode()) > MAX_RECORD:
                raise OSError('record limit')
            output.write(body)
    except OSError:
        # Never log paths, environment, response bodies or exception messages.
        print('{"result":"error","errorType":"FileError"}', file=sys.stderr)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True, choices=STAGES)
    parser.add_argument('--rid', required=True, choices=RIDS)
    parser.add_argument('--shard', required=True, choices=SHARDS)
    parser.add_argument('--result', required=True, choices=RESULTS)
    args = parser.parse_args()
    return observe(args.stage, args.rid, args.shard, args.result)


if __name__ == '__main__':
    sys.exit(main())
