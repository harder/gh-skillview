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
        value = os.readlink(path).encode() if stat.S_ISLNK(info.st_mode) else (path.read_bytes() if path.is_file() else b'')
        observed.append((str(path.relative_to(root)), info.st_dev, info.st_ino,
                         info.st_mode, hashlib.sha256(value).hexdigest()))
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
            edge_bodies = {
                'multiline-code': '`\n[Example](missing.md)\n[hidden]: references/missing.md\n`\n\n[hidden]\n',
                'multiline-label': '[Guide\ntext](references/missing.md)\n',
                'html-blank-line': '<script>\n\n[Example](missing.md)\n</script>\n\n[Guide](references/guide.md)\n',
                'list-fence': '- ```md\n  [Example](missing.md)\n  ```\n\n[Guide](references/guide.md)\n',
                'character-references': '[Named](references/a&amp;b.md)\n[Numeric](references/a&#38;b.md)\n[Escaped](references/a\\&amp;b.md)\n',
                'escaped-character-reference': '[Escaped](references/a\\&amp;b.md)\n',
                'link-parent-segment': '`scripts/pointer/../tool.py`\n',
            }
            additional = ('broken-anchor', 'anchor-case', 'dependency-local-missing', 'declared-dependency',
                          'malformed-dependency', 'portability', 'gemini-bom')
            for scenario in ('intact', 'missing-reference', 'case-mismatch', 'invalid-metadata', *edge_bodies, *additional):
                payload = skills / scenario
                (payload / 'references').mkdir(parents=True)
                (payload / 'scripts').mkdir()
                marker = root / (scenario + '-executed')
                (payload / 'scripts' / 'check.py').write_text(
                    'from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("executed")\n', encoding='utf-8')
                (payload / 'references' / 'guide.md').write_text('# Local Resource\n', encoding='utf-8')
                (payload / 'references' / 'a&b.md').write_text('Local resource.\n', encoding='utf-8')
                (payload / 'references' / 'a&amp;b.md').write_text('Local resource.\n', encoding='utf-8')
                outside = None
                if scenario == 'link-parent-segment':
                    outside = root / 'outside'
                    (outside / 'child').mkdir(parents=True)
                    (outside / 'tool.py').write_text('outside must not be inspected or run\n', encoding='utf-8')
                    (payload / 'scripts' / 'tool.py').write_text('inside\n', encoding='utf-8')
                    (payload / 'scripts' / 'pointer').symlink_to(outside / 'child', target_is_directory=True)
                    outside_before = manifest(outside)
                target = 'references/missing.md' if scenario == 'missing-reference' else (
                    'references/GUIDE.md' if scenario == 'case-mismatch' else 'references/guide.md')
                header = '---\nname: ' + scenario + '\ndescription: Disposable package check\n---\n'
                if scenario == 'invalid-metadata':
                    header = '---\nname: ' + scenario + '\n---\n'
                body = edge_bodies.get(scenario, '[Guide](' + target + ')\n')
                if scenario in ('broken-anchor', 'anchor-case'):
                    target = 'references/guide.md#' + ('missing' if scenario == 'broken-anchor' else 'Local-resource')
                    body = '[Guide](' + target + ')\n'
                if scenario in ('dependency-local-missing', 'declared-dependency', 'malformed-dependency'):
                    dependency = {'dependencies': {'local': 'file:vendor/missing'}} if scenario == 'dependency-local-missing' else (
                        {'dependencies': {'reviewed-name': '^2'}} if scenario == 'declared-dependency' else {'dependencies': []})
                    (payload / 'package.json').write_text(json.dumps(dependency), encoding='utf-8')
                if scenario == 'portability':
                    (payload / 'scripts' / 'check.sh').write_bytes(b'#!/bin/bash\r\n# Not executed\r\n')
                if scenario == 'gemini-bom':
                    header = '\ufeff' + header
                (payload / 'SKILL.md').write_text(header + '\n' + body + '\n`scripts/check.py`\n', encoding='utf-8')
                before = manifest(payload)
                command = [str(app), '--scan-root', str(skills), 'check', '--path', str(payload), '--json']
                if scenario == 'gemini-bom':
                    command += ['--agent', 'gemini-cli', '--agent-version', '0.46.0']
                process = subprocess.run(command,
                                         cwd=root, env=env, capture_output=True, text=True, timeout=120)
                report = json.loads(process.stdout)
                assert report['schemaVersion'] == 2 and report['execution'] == 'none', report
                assert report['agentCompatibility'] == report['sourceReview'] == 'not-assessed', report
                dimensions = report['assessments']
                assert dimensions['sourceReviewed']['state'] == 'not-reviewed', report
                assert dimensions['format']['state'] == report['formatStatus'], report
                assert dimensions['format']['fingerprint'] == hashlib.sha256((payload / 'SKILL.md').read_bytes()).hexdigest().upper(), report
                assert dimensions['agentCompatibility']['state'] == ('incompatible' if scenario == 'gemini-bom' else 'unknown'), report
                assert report['rootMetadataSha256'] == dimensions['format']['fingerprint'], report
                assert report['coverage']['payload'] == 'complete', report
                assert report['payloadFingerprint'] and report['installationId'], report
                if scenario in ('intact', 'escaped-character-reference', 'declared-dependency', 'portability'):
                    assert process.returncode == 0 and not report['hasErrors'], (process.returncode, report, process.stderr)
                    assert report['formatStatus'] == 'valid', report
                    assert report['referenceStatus'] == 'none-observed', report
                    assert report['coverage']['references'] == 'limited', report
                    assert dimensions['payloadIntact']['state'] == 'unknown', report
                    assert dimensions['payloadIntact']['observedCondition'] == 'no-supported-reference-defects-observed', report
                    assert any('expected payload baseline' in reason for reason in dimensions['payloadIntact']['reasons']), report
                    if scenario == 'declared-dependency':
                        assert any(item['category'] == 'dependency' and item['value'] == 'reviewed-name@^2'
                                   and item['state'] == 'availability-unknown' and 'selected Node environment' in item['repair']
                                   for item in report['observations']), report
                    if scenario == 'portability':
                        assert any(item['kind'] == 'shebang-crlf' and item['state'] == 'review'
                                   for item in report['observations']), report
                elif scenario in ('gemini-bom', 'malformed-dependency'):
                    assert process.returncode == 1 and not report['hasErrors'], (process.returncode, report, process.stderr)
                    assert dimensions['format']['state'] == 'valid' and dimensions['payloadIntact']['state'] == 'unknown', report
                    assert dimensions['payloadIntact']['observedCondition'] == 'no-supported-reference-defects-observed', report
                    if scenario == 'gemini-bom':
                        assert any(source['revision'] == 'v0.46.0' for source in dimensions['agentCompatibility']['sources']), report
                    else:
                        assert report['declarationCoverage'] == 'partial', report
                        assert any(item['kind'] == 'unsupported-manifest' and item['state'] == 'unknown'
                                   for item in report['observations']), report
                elif scenario in edge_bodies:
                    assert not report['hasErrors'], (process.returncode, report, process.stderr)
                    assert report['formatStatus'] == 'valid', report
                    assert report['coverage']['references'] == 'partial' and report['referenceStatus'] == 'unknown', report
                    assert process.returncode == 1, (process.returncode, report)
                    assert dimensions['payloadIntact']['state'] == 'unknown', report
                    assert not any(finding['code'] in ('reference-missing', 'reference-case-mismatch')
                                   for finding in report['findings']), report
                else:
                    assert process.returncode == 1 and report['hasErrors'], (process.returncode, report, process.stderr)
                    if scenario == 'invalid-metadata':
                        assert report['formatStatus'] == 'invalid', report
                        assert any(finding['code'] == 'metadata-required-description' for finding in report['findings']), report
                        assert report['referenceStatus'] == 'none-observed', report
                    else:
                        assert report['formatStatus'] == 'valid', report
                        expected_code = {'missing-reference': 'reference-missing', 'case-mismatch': 'reference-case-mismatch',
                                         'broken-anchor': 'reference-anchor-missing', 'anchor-case': 'reference-anchor-case-mismatch',
                                         'dependency-local-missing': 'dependency-file-missing'}[scenario]
                        expected_target = 'vendor/missing' if scenario == 'dependency-local-missing' else target
                        assert any(finding.get('target') == expected_target and finding['code'] == expected_code
                                   for finding in report['findings']), report
                        assert report['referenceStatus'] == 'findings', report
                        assert dimensions['payloadIntact']['state'] == 'unknown', report
                        assert dimensions['payloadIntact']['observedCondition'] == 'local-defects-observed', report
                assert manifest(payload) == before, 'Check changed payload bytes, native objects or modes'
                if outside is not None:
                    assert manifest(outside) == outside_before, 'Check changed the external link target'
                assert not any(root.glob('*-executed')), 'Check executed a referenced script'
                rows.append(dict(scenario=scenario, exitCode=process.returncode, formatStatus=report['formatStatus'],
                                 referenceStatus=report['referenceStatus'], sourceUnchanged=True,
                                 referenceCoverage=report['coverage']['references'],
                                 assessmentStates={key: value['state'] for key, value in dimensions.items()},
                                 declarationCoverage=report['declarationCoverage'],
                                 referencedScriptNotExecuted=True, payloadFingerprint=report['payloadFingerprint']))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(schemaVersion=1, packageCheckSchemaVersion=2, appSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                                          httpProxiesBlocked=True, githubCredentialsRemoved=True, rows=rows), indent=2) + '\n', encoding='utf-8')
    print(args.output)


if __name__ == '__main__':
    main()
