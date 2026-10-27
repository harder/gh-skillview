#!/usr/bin/env python3
"""Deterministic disposable inventory stories; never installs or removes skills."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys

from test_once import isolated_environment, make_output


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')


def fixture_environment(root, gh=None):
    # Reuse acceptance-runner homes, but inherit only runtime/terminal essentials.
    # No credentials, client config, Git redirection, preload, proxy or SSH state.
    allowed = {'PATH', 'SYSTEMROOT', 'WINDIR', 'DOTNET_ROOT', 'DOTNET_ROOT_X64',
               'DOTNET_ROOT_ARM64', 'LANG', 'LC_ALL'}
    env = isolated_environment({k: v for k, v in os.environ.items() if k.upper() in allowed}, root)
    env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull,
               NUGET_PACKAGES=str(root / 'nuget'),
               GIT_TERMINAL_PROMPT='0', GCM_INTERACTIVE='never',
               HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9',
               ALL_PROXY='http://127.0.0.1:9', NO_PROXY='', TERM='xterm-256color',
               COLUMNS='160', LINES='48', SKILLVIEW_LOG='debug')
    if gh:
        env.update(GH_EXTENSION='1', GH_PATH=str(gh))
    return env


def skill(path, name, *, body='Fixture body.\n', metadata=''):
    path.mkdir(parents=True, exist_ok=False)
    (path / 'SKILL.md').write_text(
        f'---\nname: {name}\ndescription: Private deterministic inventory fixture\n{metadata}---\n{body}',
        encoding='utf-8')
    return path


def generate(root, kind='messy', copies=200, roots=50):
    """root must be newly created by make_output; refuse any existing contents."""
    root = Path(root)
    if root.is_symlink() or not root.is_dir() or any(root.iterdir()):
        raise ValueError('Fixture requires an empty newly-created private directory')
    if not 1 <= roots <= 50 or not roots <= copies <= 1000:
        raise ValueError('Require 1..50 roots and roots..1000 copies')
    root.chmod(0o700)
    env = fixture_environment(root)
    cwd = root / 'project'
    cwd.mkdir()
    scenarios = {}
    scan = []
    denied = None

    def record(name, *paths, status='created', reason=None):
        scenarios[name] = dict(paths=[str(p.relative_to(root)) for p in paths], status=status)
        if reason:
            scenarios[name]['reason'] = reason

    if kind == 'messy':
        user = root / 'home/.agents/skills'
        project = cwd / '.agents/skills'
        user.mkdir(parents=True)
        project.mkdir(parents=True)
        shared = skill(root / 'payloads/shared', 'shared')
        scan.append(shared.parent)
        links = [user / 'shared', project / 'shared']
        try:
            for link in links:
                link.symlink_to(os.path.relpath(shared, link.parent), target_is_directory=True)
            broken = user / 'broken-link'
            broken.symlink_to('../../../missing-payload', target_is_directory=True)
            record('shared-payload', shared, *links)
            record('broken-link', broken)
        except OSError as error:
            record('shared-payload', shared, status='unsupported', reason=str(error))
            record('broken-link', status='unsupported', reason=str(error))
        one = skill(user / 'duplicate', 'duplicate')
        two = skill(project / 'duplicate', 'duplicate')
        record('independent-duplicates', one, two)
        one = skill(user / 'override', 'override', body='User defaults.\n')
        two = skill(project / 'override', 'override', body='Intentional project override.\n')
        record('project-override', one, two)
        mismatch = skill(user / 'revision-mismatch', 'revision-mismatch', metadata=(
            'metadata:\n  github-repo: https://github.com/fixture.invalid/story\n'
            '  github-ref: "' + 'a' * 40 + '"\n  github-path: skills/revision-mismatch\n'))
        pin = skill(user / 'lock-only-pin', 'lock-only-pin')
        lock = user.parent / '.skill-lock.json'
        lock.write_text(json.dumps({'version': 3, 'skills': {
            'revision-mismatch': dict(source='fixture.invalid/story', sourceType='github',
                                      ref='b' * 40, skillPath='skills/revision-mismatch/SKILL.md'),
            'lock-only-pin': dict(source='fixture.invalid/story', sourceType='github', pinnedRef='v1.2.3'),
            'absent-owner': dict(source='fixture.invalid/absent', sourceType='github'),
        }}, sort_keys=True) + '\n', encoding='utf-8')
        record('mismatched-revisions', mismatch, lock)
        record('lock-only-pin', pin, lock)
        record('unmatched-owner-record', lock)
        malformed = skill(user / 'malformed', 'malformed')
        (malformed / 'SKILL.md').write_text('---\nname: [not-a-string]\ndescription: []\n---\nBody\n', encoding='utf-8')
        record('malformed-metadata', malformed)
        missing = skill(user / 'missing-resource', 'missing-resource', body='See [instructions](references/missing.md).\n')
        record('missing-resource', missing)
        empty = user / 'empty-leftover'
        empty.mkdir()
        record('empty-leftover', empty)
        managed = skill(root / 'home/.codex/skills/.system/managed', 'managed')
        (managed / 'SKILL.md').chmod(0o444)
        managed.chmod(0o555)
        record('managed-read-only', managed)
        denied = root / 'inaccessible'
        skill(denied / 'unobservable', 'unobservable')
        scan.append(denied)
        denied.chmod(0)
        try:
            list(denied.iterdir())
        except PermissionError:
            record('inaccessible-location', denied)
        else:
            record('inaccessible-location', denied, status='unsupported',
                   reason='This identity can enumerate mode-000 directories (for example root); no denied coverage claimed')
        finally:
            if scenarios.get('inaccessible-location', {}).get('status') != 'created':
                denied.chmod(0o700)
    elif kind == 'performance':
        scan = [root / f'roots/root-{i:03d}' for i in range(roots)]
        for path in scan:
            path.mkdir(parents=True)
        for i in range(copies):
            skill(scan[i % roots] / f'item-{i:04d}', f'item-{i:04d}')
        record('regular-copies', *scan)
    else:
        raise ValueError('Unknown fixture kind')
    manifest = dict(schema=1, kind=kind, copies=copies if kind == 'performance' else None,
                    requestedRoots=roots if kind == 'performance' else None,
                    cwd=str(cwd.relative_to(root)), scanRoots=[str(p.relative_to(root)) for p in scan],
                    scenarios=scenarios, limitations=[
                        'Synthetic authored metadata/locks; no install, update, removal, remote or human acceptance proof.',
                        'A missing Markdown resource is authored evidence; inventory does not validate resource links.'])
    write_json(root / 'fixture.json', manifest)
    return manifest, env


def flags(root, manifest):
    return [value for path in manifest['scanRoots'] for value in ('--scan-root', str(root / path))]


def snapshot(root, manifest, identities=False):
    """Hash only fixture inputs, without following links or entering denied subtrees."""
    excluded = {'fixture.json', 'environment.json', 'manifest.json', 'logs', 'cache', 'state',
                'tmp', 'dotnet-home'}
    denied = manifest['scenarios'].get('inaccessible-location', {})
    skip = set(denied.get('paths', [])) if denied.get('status') == 'created' else set()
    rows = []

    def visit(path):
        rel = path.relative_to(root).as_posix()
        info = path.lstat()
        mode = info.st_mode
        kind = 'link' if stat.S_ISLNK(mode) else 'directory' if stat.S_ISDIR(mode) else 'file'
        content = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        rows.append(dict(path=rel, kind=kind, mode=stat.S_IMODE(mode),
                         sha256=hashlib.sha256(content).hexdigest()))
        if identities:
            rows[-1].update(device=info.st_dev, inode=info.st_ino)
        if kind == 'directory' and rel not in skip:
            for child in sorted(path.iterdir()):
                visit(child)
    for path in sorted(root.iterdir()):
        if path.name not in excluded:
            visit(path)
    return rows


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--kind', choices=['messy', 'performance'], default='messy')
    parser.add_argument('--copies', type=int, default=200)
    parser.add_argument('--roots', type=int, default=50)
    args = parser.parse_args(argv)
    root = make_output(args.output)
    manifest, env = generate(root, args.kind, args.copies, args.roots)
    write_json(root / 'environment.json', env)
    write_json(root / 'manifest.json', snapshot(root, manifest))
    print(root)
    return 0


if __name__ == '__main__':
    sys.exit(main())
