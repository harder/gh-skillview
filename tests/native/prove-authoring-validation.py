"""Run explicit publish dry-runs on private no-remote repositories.

Uses invalid disposable credentials and a held non-listening loopback proxy.
This is authored-file/diagnostic evidence, not an OS network or zero-write audit.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import stat
import subprocess
import tempfile


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def manifest(root):
    rows = []
    for path in sorted(root.rglob('*')):
        info = path.lstat()
        assert stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode), path
        if stat.S_ISREG(info.st_mode):
            rows.append([str(path.relative_to(root)), info.st_dev, info.st_ino,
                         info.st_ctime_ns, info.st_mode, sha(path)])
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--gh', required=True, action='append', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    app = args.app.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    evidence = {'schemaVersion': 1, 'status': 'failed',
                'scope': 'Actual gh authoring dry-run diagnostics and observed authored/Git file preservation; no real publish, client, model, or OS network audit.',
                'app': {'path': str(app), 'sha256': sha(app)}, 'results': []}
    try:
        temp_parent = '/var/tmp' if os.name == 'posix' and Path('/var/tmp').is_dir() else None
        with tempfile.TemporaryDirectory(prefix='skillview-authoring-proof-', dir=temp_parent) as temporary, socket.socket() as blocked:
            root = Path(temporary).resolve()
            blocked.bind(('127.0.0.1', 0))  # Held without listen until every child finishes.
            proxy = 'http://127.0.0.1:' + str(blocked.getsockname()[1])
            env = {key: os.environ[key] for key in ('PATH', 'SystemRoot', 'SYSTEMROOT', 'WINDIR', 'PATHEXT') if key in os.environ}
            for leaf in ['home', 'tmp', 'empty-template']:
                (root / leaf).mkdir()
            env.update(HOME=str(root / 'home'), USERPROFILE=str(root / 'home'),
                       GH_TOKEN='disposable-invalid-token', GH_CONFIG_DIR=str(root / 'gh-config'),
                       SKILLVIEW_STATE_HOME=str(root / 'state'), GIT_CONFIG_COUNT='0',
                       GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root / 'empty-git-config'),
                       TMPDIR=str(root / 'tmp'), TMP=str(root / 'tmp'), TEMP=str(root / 'tmp'),
                       CLAUDE_CONFIG_DIR=str(root / 'home' / '.claude'), CODEX_HOME=str(root / 'home' / '.codex'),
                       APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                       GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false')
            (root / 'empty-git-config').write_bytes(b'')
            for name in ['CONFIG', 'STATE', 'DATA', 'CACHE']:
                env['XDG_' + name + '_HOME'] = str(root / ('xdg-' + name.lower()))
            for key in ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy']:
                env[key] = proxy
            env['NO_PROXY'] = env['no_proxy'] = ''
            for index, gh_argument in enumerate(args.gh):
                gh = gh_argument.resolve(strict=True)
                env.update(GH_EXTENSION='1', GH_PATH=str(gh))
                version = subprocess.run([str(gh), '--version'], cwd=root, env=env, capture_output=True, text=True, check=True, timeout=30).stdout.splitlines()[0]
                for scenario in ['valid', 'malformed']:
                    label = str(index) + '-' + scenario
                    repository = root / label
                    payload = repository / 'skills' / 'demo'
                    (payload / 'scripts').mkdir(parents=True)
                    poison = root / (label + '-poison-executed')
                    (payload / 'scripts' / 'poison.py').write_text('from pathlib import Path\nPath(' + repr(str(poison)) + ').write_text("executed")\n', encoding='utf-8')
                    description = 'description: Disposable authoring fixture\n' if scenario == 'valid' else ''
                    (payload / 'SKILL.md').write_text('---\nname: demo\n' + description + '---\nNever execute scripts/poison.py.\n', encoding='utf-8')
                    subprocess.run(['git', 'init', '--quiet', '--template=' + str(root / 'empty-template'), '--', str(repository)],
                                   cwd=root, env=env, capture_output=True, text=True, check=True, timeout=30)
                    remotes = subprocess.run(['git', '-C', str(repository), 'remote', '-v'], cwd=root, env=env,
                                             capture_output=True, text=True, check=True, timeout=30)
                    assert not remotes.stdout.strip(), remotes.stdout
                    before = manifest(repository)
                    command = [str(app), '--scan-root', str(payload.parent), 'authoring-validate',
                               '--repository', str(repository), '--dry-run', '--json']
                    result = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=100)
                    (output / (label + '.stdout.json')).write_text(result.stdout, encoding='utf-8')
                    (output / (label + '.stderr.txt')).write_text(result.stderr, encoding='utf-8')
                    report = json.loads(result.stdout)
                    assert report['execution'] == 'gh-skill-publish-dry-run', report
                    assert report['ghPath'] == str(gh), report
                    assert report['commandExitCode'] == (0 if scenario == 'valid' else 1), report
                    assert report['status'] == ('completed' if scenario == 'valid' else 'failed'), report
                    assert (result.returncode == 0) == (scenario == 'valid'), report
                    assert report['authoredBytes'] == 'unchanged-observed-candidates', report
                    assert report['discoveryCoverage'] == 'bounded-candidates', report
                    assert len(report['candidatePayloads']) == 1, report
                    candidate = report['candidatePayloads'][0]
                    assert candidate['beforeFingerprint'] == candidate['afterFingerprint'], report
                    assert not report['outputTruncated'] and not poison.exists(), report
                    if scenario == 'malformed':
                        assert 'description' in report['stdout'] + report['stderr'], report
                    after = manifest(repository)
                    assert after == before, (label, before, after)
                    evidence['results'].append({'case': label, 'gh': {'path': str(gh), 'sha256': sha(gh), 'version': version},
                                                'argv': command, 'exitCode': result.returncode,
                                                'authoredAndGitFilesUnchanged': True, 'sourceManifest': before})
            evidence['status'] = 'passed'
    finally:
        (output / 'summary.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'status': evidence['status'], 'cases': len(evidence['results']), 'output': str(output)}))


if __name__ == '__main__':
    main()
