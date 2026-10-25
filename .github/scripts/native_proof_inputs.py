"""Fixed CI native inputs: verify frozen producers and transfer this run's tools.

Only producer binaries and their source/recipe receipts are cached. Proof results,
current binaries, Git worktree metadata and mutable fixtures never enter that cache.
"""
import argparse
import ast
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile


ROOT = Path(__file__).resolve().parents[2]
SHARDS = ('owners', 'sources', 'inspection-unpin')
RIDS = ('linux-x64', 'osx-arm64', 'win-x64', 'win-arm64')
PUBLISH = ('-c', 'Release', '-p:PublishAot=true', '-p:StripSymbols=true',
           '-p:RestoreLockedMode=true', '-warnaserror:IL2026',
           '-warnaserror:IL3050', '-warnaserror:IL3053')
FAMILIES = (
    ('legacy', 'prove-old-native-plans.py', 'BASELINE', 'skillview-unpin-baseline', 'SKILLVIEW_LEGACY_CHECKOUT'),
    ('reviewed-merge', 'historical_merge_proof.py', 'BASELINE', 'skillview-reviewed-merge-baseline', 'SKILLVIEW_REVIEWED_MERGE_CHECKOUT'),
    ('unpin', 'prove-old-native-unpin.py', 'BASELINE', 'skillview-local-reimport-baseline', 'SKILLVIEW_UNPIN_CHECKOUT'),
    ('reimport', 'prove-old-native-reimport.py', 'BASELINE', 'skillview-source-switch-baseline', 'SKILLVIEW_REIMPORT_CHECKOUT'),
    ('source-switch', 'prove-old-native-reimport.py', 'SOURCE_SWITCH_BASELINE',
     'skillview-pre-review-source-switch-baseline', 'SKILLVIEW_SOURCE_SWITCH_CHECKOUT'),
)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def git(*arguments, cwd=None):
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
    env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0', GIT_OPTIONAL_LOCKS='0')
    return subprocess.check_output(['git', '--no-optional-locks', '-c', 'core.fsmonitor=false',
                                    *map(str, arguments)], cwd=ROOT if cwd is None else cwd,
                                   env=env, text=True, timeout=120).strip()


def specs():
    result = []
    for family, script, variable, directory, environment in FAMILIES:
        tree = ast.parse((ROOT / 'tests/native' / script).read_text(encoding='utf-8'))
        values = [ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
                  and any(isinstance(target, ast.Name) and target.id == variable for target in node.targets)]
        if len(values) != 1 or not isinstance(values[0], str) or len(values[0]) != 40 or any(
                character not in '0123456789abcdef' for character in values[0]):
            raise ValueError('Frozen native producer must have one exact source commit: ' + family)
        result.append(dict(family=family, sourceCommit=values[0], directory=directory, environment=environment))
    return result


def checkout(spec):
    path = Path(os.environ['RUNNER_TEMP']) / spec['directory']
    if not path.exists():
        git('fetch', '--no-tags', '--depth=1', 'origin', spec['sourceCommit'])
        git('-c', 'core.autocrlf=false', '-c', 'core.eol=lf',
            'worktree', 'add', '--detach', path, spec['sourceCommit'])
    if git('rev-parse', 'HEAD', cwd=path) != spec['sourceCommit'] or git(
            'status', '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=all', cwd=path):
        raise ValueError('Frozen native source must be exact and tracked-clean: ' + spec['family'])
    return path


def export(file, name, value):
    with Path(os.environ[file]).open('a', encoding='utf-8', newline='\n') as stream:
        stream.write(name + '=' + str(value) + '\n')


def plan(rid):
    sdk = subprocess.check_output(['dotnet', '--version'], cwd=ROOT, text=True, timeout=30).strip()
    if sdk != json.loads((ROOT / 'global.json').read_text())['sdk']['version']:
        raise ValueError('Native producers require the exact selected SDK')
    inputs = dict(rid=rid, sdk=sdk, imageOS=os.environ.get('ImageOS', ''),
                  imageVersion=os.environ.get('ImageVersion', ''), recipe=list(PUBLISH),
                  helperSha256=digest(Path(__file__)), dependencies={})
    sources = specs()
    # Include the actual frozen globals/locks, as well as their source commits.
    for name, directory in [('current', ROOT), *[(s['sourceCommit'], checkout(s)) for s in sources]]:
        files = [directory / 'global.json', *sorted(directory.rglob('packages.lock.json'))]
        inputs['dependencies'][name] = {str(path.relative_to(directory)): digest(path) for path in files}
    identity = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
    value = dict(cacheKey='native-producers-v1-' + rid + '-' + identity, inputs=inputs, sources=sources)
    write_json(ROOT / '.ci-native/baseline-plan.json', value)
    export('GITHUB_OUTPUT', 'cache-key', value['cacheKey'])


def verify_producer(directory, spec, cache_key, rid):
    binary = directory / ('skillview.exe' if rid.startswith('win-') else 'skillview')
    receipt = directory / 'source-receipt.json'
    if directory.is_symlink() or binary.is_symlink() or receipt.is_symlink():
        raise ValueError('Frozen producer cache must contain regular files')
    value = json.loads(receipt.read_text(encoding='utf-8'))
    if value.get('sourceCommit') != spec['sourceCommit'] or value.get('trackedClean') is not True or (
            value.get('cacheKey') != cache_key or value.get('rid') != rid or
            value.get('binarySha256') != digest(binary) or value.get('recipe') != list(PUBLISH) or
            value.get('sdk') != json.loads((ROOT / 'global.json').read_text())['sdk']['version']):
        raise ValueError('Frozen producer source, recipe or binary checksum mismatch: ' + spec['family'])
    return binary


def prepare(rid):
    value = json.loads((ROOT / '.ci-native/baseline-plan.json').read_text(encoding='utf-8'))
    if value['inputs']['rid'] != rid or value['sources'] != specs():
        raise ValueError('Frozen producer cache plan changed')
    for spec in value['sources']:
        source = checkout(spec)
        directory = ROOT / '.ci-native/baselines' / spec['family']
        if not directory.exists():
            output = source / 'dist/app'
            subprocess.run(['dotnet', 'publish', str(source / 'src/SkillView.App/SkillView.App.csproj'),
                            *PUBLISH, '-r', rid, '-o', str(output)], cwd=ROOT, check=True)
            checkout(spec)  # Publication must not change tracked source.
            directory.mkdir(parents=True)
            binary = output / ('skillview.exe' if rid.startswith('win-') else 'skillview')
            shutil.copy2(binary, directory / binary.name)
            write_json(directory / 'source-receipt.json', dict(sourceCommit=spec['sourceCommit'],
                trackedClean=True, binarySha256=digest(binary), cacheKey=value['cacheKey'], rid=rid,
                sdk=value['inputs']['sdk'], recipe=list(PUBLISH)))
        # A missing, stale or corrupt cache entry cannot become passing evidence.
        binary = verify_producer(directory, spec, value['cacheKey'], rid)
        subprocess.run([str(binary), '--version'], cwd=ROOT, check=True, timeout=30)


def input_files(rid):
    ext = '.exe' if rid.startswith('win-') else ''
    paths = [ROOT / ('dist/app/skillview' + ext), ROOT / ('dist/ext/gh-skillview' + ext)]
    for directory in ('.ci-native/baselines', '.ci-native/gh-minimum', '.ci-native/gh-current',
                      'tests/SkillView.MaintenanceProof/bin/Release/net10.0'):
        parent = ROOT / directory
        if not parent.is_dir() or parent.is_symlink():
            raise ValueError('Missing regular native tool directory: ' + directory)
        for path in sorted(parent.rglob('*')):
            if path.is_symlink():
                raise ValueError('Native tool input must not be a symlink: ' + str(path))
            if path.is_file():
                paths.append(path)
    if any(not path.is_file() or path.is_symlink() for path in paths):
        raise ValueError('Missing regular native product host')
    return paths


def pack(rid):
    value = json.loads((ROOT / '.ci-native/baseline-plan.json').read_text(encoding='utf-8'))
    paths = input_files(rid)
    manifest = dict(commit=git('rev-parse', 'HEAD'), rid=rid, cacheKey=value['cacheKey'],
                    files={path.relative_to(ROOT).as_posix(): digest(path) for path in paths})
    receipt = ROOT / '.ci-native/native-inputs-manifest.json'
    write_json(receipt, manifest)
    with tarfile.open(ROOT / '.ci-native/native-proof-inputs.tar', 'w') as archive:
        for path in [receipt, *paths]:
            archive.add(path, arcname=path.relative_to(ROOT).as_posix(), recursive=False)


def unpack(rid):
    archive_path = ROOT / '.ci-native/native-proof-inputs.tar'
    with tarfile.open(archive_path, 'r:') as archive:
        members = archive.getmembers()
        names = [member.name for member in members]
        if len(members) > 2048 or len(names) != len(set(names)) or any(
                not member.isfile() or member.size > 256 * 1024 * 1024 or
                not safe_member(member.name) for member in members):
            raise ValueError('Native tool artifact contains an unsupported entry')
        manifest_name = '.ci-native/native-inputs-manifest.json'
        entry = archive.getmember(manifest_name)
        if entry.size > 1024 * 1024 or sum(member.size for member in members) > 1024 * 1024 * 1024:
            raise ValueError('Native tool artifact exceeded its bound')
        manifest = json.load(archive.extractfile(entry))
        if manifest['rid'] != rid or manifest['commit'] != git('rev-parse', 'HEAD') or set(names) != {
                manifest_name, *manifest['files']}:
            raise ValueError('Native tool artifact does not belong to this commit/RID or is incomplete')
        for member in members:
            target = ROOT / member.name
            if any(path.is_symlink() for path in (target, *target.parents)):
                raise ValueError('Native tool artifact destination must not follow links')
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as source, target.open('xb') as destination:
                shutil.copyfileobj(source, destination)
            target.chmod(member.mode & 0o777)
            if member.name != manifest_name and digest(target) != manifest['files'][member.name]:
                raise ValueError('Native tool artifact checksum mismatch: ' + member.name)
    # Bind the cache receipts and official gh checksum receipts again after transfer.
    for spec in specs():
        verify_producer(ROOT / '.ci-native/baselines' / spec['family'], spec, manifest['cacheKey'], rid)
    for version, name in [('2.97.0', 'minimum'), ('2.102.0', 'current')]:
        directory = ROOT / ('.ci-native/gh-' + name)
        receipt = json.loads((directory / 'source.json').read_text(encoding='utf-8'))
        binary = directory / ('gh.exe' if rid.startswith('win-') else 'gh')
        if receipt['version'] != version or receipt['rid'] != rid or receipt['binarySha256'] != digest(binary):
            raise ValueError('Transferred gh binary differs from its official checksum receipt')


def safe_member(name):
    path = PurePosixPath(name)
    return (not path.is_absolute() and '..' not in path.parts and '\\' not in name and ':' not in name
            and str(path) == name and (name.startswith('.ci-native/') or name.startswith('dist/app/')
                or name.startswith('dist/ext/') or name.startswith('tests/SkillView.MaintenanceProof/bin/Release/net10.0/')))


def stage(rid, shard):
    manifest = json.loads((ROOT / '.ci-native/native-inputs-manifest.json').read_text(encoding='utf-8'))
    for spec in specs():
        if shard == 'sources' or spec['family'] == 'reviewed-merge' or (shard == 'inspection-unpin' and spec['family'] == 'legacy'):
            source = checkout(spec)
            binary = verify_producer(ROOT / '.ci-native/baselines' / spec['family'], spec, manifest['cacheKey'], rid)
            destination = source / 'dist/app' / binary.name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(binary, destination)
            if digest(destination) != digest(binary):
                raise ValueError('Frozen producer changed while staging its exact checkout')
            export('GITHUB_ENV', spec['environment'], source)
    # All proof invocations use the same verified recorded-current gh host.
    with Path(os.environ['GITHUB_PATH']).open('a', encoding='utf-8', newline='\n') as stream:
        stream.write(str(ROOT / '.ci-native/gh-current') + '\n')


def completion(rid, shard):
    write_json(ROOT / ('dist/proof-completion/' + rid + '-' + shard + '.json'),
               dict(rid=rid, shard=shard, commit=os.environ['GITHUB_SHA'],
                    run=os.environ['GITHUB_RUN_ID'], attempt=os.environ['GITHUB_RUN_ATTEMPT']))


def verify_completions(rid):
    root = ROOT / 'dist/proof-completion'
    expected = {rid + '-' + shard + '.json' for shard in SHARDS}
    if {path.name for path in root.iterdir()} != expected:
        raise ValueError('Native proof completion set is missing or unexpected')
    for shard in SHARDS:
        value = json.loads((root / (rid + '-' + shard + '.json')).read_text())
        attempt = value.pop('attempt', None)
        if (not isinstance(attempt, str) or not attempt.isascii() or not attempt.isdecimal()
                or not 1 <= int(attempt) <= int(os.environ['GITHUB_RUN_ATTEMPT']) or
                value != dict(rid=rid, shard=shard, commit=os.environ['GITHUB_SHA'], run=os.environ['GITHUB_RUN_ID'])):
            raise ValueError('Native proof completion does not belong to this run/attempt')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('plan', 'prepare', 'pack', 'unpack', 'stage', 'completion', 'verify-completions'))
    parser.add_argument('--rid', required=True, choices=RIDS)
    parser.add_argument('--shard', choices=SHARDS)
    args = parser.parse_args()
    if args.command in ('stage', 'completion'):
        if args.shard is None:
            parser.error('--shard is required')
        globals()[args.command](args.rid, args.shard)
    else:
        globals()[args.command.replace('-', '_')](args.rid)


if __name__ == '__main__':
    main()
