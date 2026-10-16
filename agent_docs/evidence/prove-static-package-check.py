"""Exercise explicit read-only package checks through one built product host.

All inputs are disposable local files. Child HTTP(S) proxies point to a reserved
non-listening socket, GitHub credentials are removed, and a referenced script
must never execute.
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


def manifest(root):
    observed = []
    for path in [root, *sorted(root.rglob('*'))]:
        info = path.lstat()
        assert not stat.S_ISLNK(info.st_mode), 'Proof inputs must be regular local fixtures'
        value = path.read_bytes() if path.is_file() else b''
        observed.append((str(path.relative_to(root)), info.st_dev, info.st_ino,
                         stat.S_IMODE(info.st_mode), hashlib.sha256(value).hexdigest()))
    return observed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    app = args.app.resolve(strict=True)
    assert app.is_file()
    rows = []
    with tempfile.TemporaryDirectory(prefix='skillview-package-check-proof-') as temporary:
        root = Path(temporary).resolve()
        home, skills = root / 'home', root / 'skills'
        home.mkdir()
        skills.mkdir()
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GIT_', 'SKILLVIEW_', 'GH_', 'CLAUDE_CODE_PLUGIN_'))
               and key not in ('HOME', 'USERPROFILE', 'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'PI_CODING_AGENT_DIR',
                               'GITHUB_TOKEN', 'GITHUB_AUTH_TOKEN', 'GITHUB_ENTERPRISE_TOKEN')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'),
                   SKILLVIEW_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_STATE_HOME=str(root / 'xdg-state'), XDG_DATA_HOME=str(root / 'data'),
                   XDG_CACHE_HOME=str(root / 'cache'), APPDATA=str(root / 'appdata'),
                   LOCALAPPDATA=str(root / 'localappdata'), CODEX_HOME=str(home / '.codex'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi'),
                   GIT_CONFIG_COUNT='0', GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1')
        # Keep the reserved socket open and non-listening throughout every child.
        with socket.socket() as blocked:
            blocked.bind(('127.0.0.1', 0))
            proxy = 'http://127.0.0.1:' + str(blocked.getsockname()[1])
            for key in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
                env[key] = proxy
            env['NO_PROXY'] = env['no_proxy'] = ''
            for scenario in ('intact', 'missing-reference', 'case-mismatch', 'invalid-metadata'):
                payload = skills / scenario
                (payload / 'references').mkdir(parents=True)
                (payload / 'scripts').mkdir()
                marker = root / (scenario + '-executed')
                (payload / 'scripts' / 'check.py').write_text(
                    'from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("executed")\n', encoding='utf-8')
                (payload / 'references' / 'guide.md').write_text('Local resource.\n', encoding='utf-8')
                target = 'references/missing.md' if scenario == 'missing-reference' else (
                    'references/GUIDE.md' if scenario == 'case-mismatch' else 'references/guide.md')
                header = '---\nname: ' + scenario + '\ndescription: Disposable package check\n---\n'
                if scenario == 'invalid-metadata':
                    header = '---\nname: ' + scenario + '\n---\n'
                (payload / 'SKILL.md').write_text(header + '\n[Guide](' + target + ')\n\n`scripts/check.py`\n', encoding='utf-8')
                before = manifest(payload)
                process = subprocess.run([str(app), '--scan-root', str(skills), 'check', '--path', str(payload), '--json'],
                                         cwd=root, env=env, capture_output=True, text=True, timeout=120)
                report = json.loads(process.stdout)
                assert report['schemaVersion'] == 1 and report['execution'] == 'none', report
                assert report['agentCompatibility'] == report['sourceReview'] == 'not-assessed', report
                assert report['coverage']['payload'] == 'complete', report
                assert report['payloadFingerprint'] and report['installationId'], report
                if scenario == 'intact':
                    assert process.returncode == 0 and not report['hasErrors'], (process.returncode, report, process.stderr)
                    assert report['formatStatus'] == 'valid', report
                    assert report['referenceStatus'] == 'none-observed', report
                else:
                    assert process.returncode == 1 and report['hasErrors'], (process.returncode, report, process.stderr)
                    if scenario == 'invalid-metadata':
                        assert report['formatStatus'] == 'invalid', report
                        assert any(finding['code'] == 'metadata-required-description' for finding in report['findings']), report
                        assert report['referenceStatus'] == 'none-observed', report
                    else:
                        assert report['formatStatus'] == 'valid', report
                        expected_code = 'reference-missing' if scenario == 'missing-reference' else 'reference-case-mismatch'
                        assert any(finding.get('target') == target and finding['code'] == expected_code
                                   for finding in report['findings']), report
                        assert report['referenceStatus'] == 'findings', report
                assert manifest(payload) == before, 'Check changed payload bytes, native objects or modes'
                assert not any(root.glob('*-executed')), 'Check executed a referenced script'
                rows.append(dict(scenario=scenario, exitCode=process.returncode, formatStatus=report['formatStatus'],
                                 referenceStatus=report['referenceStatus'], sourceUnchanged=True,
                                 referencedScriptNotExecuted=True, payloadFingerprint=report['payloadFingerprint']))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(schemaVersion=1, appSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                                          httpProxiesBlocked=True, githubCredentialsRemoved=True, rows=rows), indent=2) + '\n', encoding='utf-8')
    print(args.output)


if __name__ == '__main__':
    main()
