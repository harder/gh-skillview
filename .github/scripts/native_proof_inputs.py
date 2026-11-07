"""Fixed CI native inputs: verify frozen producers and transfer this run's tools.

Only producer binaries and their source/recipe receipts are cached. Proof results,
current binaries, Git worktree metadata and mutable fixtures never enter that cache.
"""
import argparse
import ast
import hashlib
import json
import os
import re
import stat
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile


ROOT = Path(__file__).resolve().parents[2]
SHARDS = ('owners', 'sources', 'inspection-unpin')
RIDS = ('linux-x64', 'osx-arm64', 'win-x64', 'win-arm64')
SCHEMA = 3
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


def selected_sdk(source):
    version = json.loads((source / 'global.json').read_text(encoding='utf-8'))['sdk']['version']
    if not isinstance(version, str) or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
        raise ValueError('Frozen SDK must be an exact stable version')
    return version


def sdk_version(source):
    actual = subprocess.check_output(['dotnet', '--version'], cwd=source, text=True, timeout=30).strip()
    if actual != selected_sdk(source):
        raise ValueError('Native producers require their own exact selected SDK')
    return actual


def source_inputs(source):
    files = [source / 'global.json', *sorted(source.rglob('packages.lock.json'))]
    return dict(sdk=selected_sdk(source), dependencies={path.relative_to(source).as_posix(): digest(path) for path in files})


# The URL-wide NuGet mechanism is admitted only after an unsuppressed exact-source
# audit. This policy is deliberately independent of a cache's self-description.
ADVISORY = 'https://github.com/advisories/GHSA-p3jp-9v83-76w9'
AUDIT_RECIPE = 'historical-audit-exception-v1'
PROPS = ('<Project>\n  <ItemGroup>\n    <NuGetAuditSuppress Include="' + ADVISORY
         + '" />\n  </ItemGroup>\n</Project>\n').encode('ascii')
LOCK_PATHS = ('src/SkillView.App/packages.lock.json', 'src/SkillView.Core/packages.lock.json',
              'src/SkillView.GhExtension/packages.lock.json', 'tests/SkillView.Tests/packages.lock.json',
              'tests/SkillView.IntegrationTests/packages.lock.json', 'tests/SkillView.MaintenanceProof/packages.lock.json')
APP = 'src/SkillView.App/SkillView.App.csproj'
CORE = 'src/SkillView.Core/SkillView.Core.csproj'
AUDIT_LIMIT = 8 * 1024 * 1024
APPROVED_COMMITS = {
    'legacy': ('0dba59be59776e6e654123aa1ce1e5496074763e', '10.0.402'),
    'reviewed-merge': ('27efe94ddbde31ef3c8e6e7dbbee41c00c8b7177', '10.0.113'),
    'unpin': ('c919199a6861b9aaf9301e31c4b2945e098da799', '10.0.402'),
    'reimport': ('4649d75fc3a4f9fdd7c22ca5593e629a1b559298', '10.0.402'),
    'source-switch': ('5822f81077a8675724012670c6acd4c4c6abc0c9', '10.0.402'),
}


def regular_bytes(path, limit):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > limit:
        raise ValueError('Expected a bounded ordinary file')
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    after = path.lstat()
    fields = ('st_dev', 'st_ino', 'st_mode', 'st_nlink', 'st_size', 'st_mtime_ns', 'st_ctime_ns')
    if len(data) != info.st_size or len(data) > limit or any(getattr(info, f) != getattr(after, f) for f in fields):
        raise ValueError('File changed during bounded read')
    return data


def disposition():
    path = Path(__file__).with_name('frozen-audit-exception.json')
    raw = regular_bytes(path, 32768)
    value = json.loads(raw)
    if (set(value) != {'schema', 'scope', 'advisory', 'package', 'version', 'severity',
                      'ownerDecision', 'removalTrigger', 'families'}
            or type(value['schema']) is not int or value['schema'] != 1 or value['scope'] != 'historical-ci-fixture-only/non-release'
            or (value['advisory'], value['package'], value['version'], value['severity']) != (
                ADVISORY, 'Microsoft.DotNet.ILCompiler', '10.0.13', 'high')
            or not all(isinstance(value[k], str) and value[k] for k in ('ownerDecision', 'removalTrigger'))
            or set(value['families']) != set(APPROVED_COMMITS)):
        raise ValueError('Unknown historical audit owner disposition')
    for family, item in value['families'].items():
        if (set(item) != {'sourceCommit', 'sdk', 'dependencies'}
                or (item['sourceCommit'], item['sdk']) != APPROVED_COMMITS[family]
                or set(item['dependencies']) != {'global.json', *LOCK_PATHS}
                or any(not isinstance(v, str) or not re.fullmatch('[0-9a-f]{64}', v)
                       for v in item['dependencies'].values())):
            raise ValueError('Historical exception source scope changed')
    return value, hashlib.sha256(raw).hexdigest()


def exception_binding(spec, identity):
    policy, checksum = disposition()
    item = policy['families'].get(spec['family'])
    if (item is None or item['sourceCommit'] != spec['sourceCommit']
            or identity != {key: item[key] for key in ('sdk', 'dependencies')}):
        raise ValueError('Exception requires the exact approved frozen SDK and seven inputs')
    return dict(recipeVersion=AUDIT_RECIPE, dispositionSha256=checksum,
                propsSha256=hashlib.sha256(PROPS).hexdigest(), scope=policy['scope'])


def guarded_source(spec, source):
    if (source.resolve() == ROOT.resolve() or git('rev-parse', 'HEAD', cwd=source) != spec['sourceCommit']
            or git('status', '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=all', cwd=source)):
        raise ValueError('Audit exception cannot apply to current, changed or foreign source')
    identity = source_inputs(source)
    exception_binding(spec, identity)
    sdk_version(source)
    return identity


def exception_props(spec, source, identity):
    binding = exception_binding(spec, identity)
    parent = Path(os.environ['RUNNER_TEMP']).resolve() / 'skillview-frozen-audit-props'
    if parent.is_symlink():
        raise ValueError('Exception props directory must not be a link')
    parent.mkdir(mode=0o700, exist_ok=True)
    path = parent / (binding['propsSha256'] + '.props')
    if source.resolve() in path.resolve().parents or ROOT.resolve() in path.resolve().parents:
        raise ValueError('Exception props must be outside all tracked source')
    if not path.exists():
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0)
        with os.fdopen(os.open(path, flags, 0o600), 'wb') as stream:
            stream.write(PROPS)
    check_props(path, source)
    return path


def check_props(path, source):
    # MSBuild property separators must never turn a path into extra properties.
    if any(c in str(path) for c in ';,%\r\n') or source.resolve() in path.resolve().parents or ROOT.resolve() in path.resolve().parents:
        raise ValueError('Unsafe exception props path')
    if regular_bytes(path, 4096) != PROPS:
        raise ValueError('Missing, altered or additional audit exception props')


def audit_findings(stdout, stderr, code, source):
    if type(code) is not int or code != 1:
        raise ValueError('Default audit must positively report the exact known failure')
    combined = stdout + stderr
    if (len(combined.encode('utf-8')) > AUDIT_LIMIT or '\ufffd' in combined or '\x00' in combined
            or not combined.endswith('\n') or combined.count('Build FAILED.') != 1
            or not re.search(r'\nTime Elapsed [0-9:.]+\s*$', stdout)
            or not re.search(r'^\s*1 Warning\(s\)\s*$', combined, re.M)
            or not re.search(r'^\s*1 Error\(s\)\s*$', combined, re.M)):
        raise ValueError('Default audit output incomplete or unexpected')
    expected = {str(source / APP): ('warning', ''), str(source / CORE): ('error', 'Warning As Error: ')}
    message = "Package 'Microsoft.DotNet.ILCompiler' 10.0.13 has a known high severity vulnerability, " + ADVISORY
    found = set()
    for line in combined.splitlines():
        if re.fullmatch(r'\s*[0-9]+ (?:Warning|Error)\(s\)\s*', line):
            continue
        if not re.search(r'\b(?:warning|error)\b|\bNU[0-9]{4}\b|An issue was encountered', line, re.I):
            continue
        match = re.fullmatch(r'\s*(?:[0-9]+>)?(.+\.csproj) : (warning|error) NU1903: (.+)', line)
        if not match or match[1] not in expected:
            raise ValueError('Unexpected audit diagnostic')
        level, prefix = expected[match[1]]
        tail = match[3]
        context = ' [' + str(source / APP) + ']'
        if tail.endswith(context):
            tail = tail[:-len(context)]
        if match[2] != level or tail != prefix + message:
            raise ValueError('Additional package, advisory or severity refused')
        found.add((match[1], level))
    if found != {(p, v[0]) for p, v in expected.items()}:
        raise ValueError('Missing Core error or App warning')
    return dict(package='Microsoft.DotNet.ILCompiler', version='10.0.13', advisory=ADVISORY,
                severity='high', projects={'App': 'warning', 'Core': 'error'})


def audit_directory(spec):
    return ROOT / '.ci-native/frozen-audits' / spec['family']


def audit_preflight(spec, source, rid, dotnet='dotnet', config=None):
    identity = guarded_source(spec, source)
    directory = audit_directory(spec)
    # Not inside the reusable baseline cache: a fresh default audit is required
    # before either constructing a producer or accepting cached binary bytes.
    directory.mkdir(parents=True, exist_ok=False)
    argv = [dotnet, 'restore', APP, '--locked-mode', '--verbosity', 'normal']
    if config is not None:
        argv += ['--configfile', str(config)]
    env = dict(os.environ, DOTNET_CLI_UI_LANGUAGE='en-US')
    with (directory / 'stdout').open('xb') as out, (directory / 'stderr').open('xb') as err:
        child = subprocess.Popen(argv, cwd=source, env=env, stdout=out, stderr=err)
        write_json(directory / 'command.json', dict(argv=argv, cwd=str(source), pid=child.pid,
                                                  deadlineSeconds=600))
        try:
            code = child.wait(timeout=600)
        except subprocess.TimeoutExpired:
            write_json(directory / 'STOP.json', dict(exitCode=None, knownPID=child.pid, timeout=True))
            raise
    out = regular_bytes(directory / 'stdout', AUDIT_LIMIT)
    err = regular_bytes(directory / 'stderr', AUDIT_LIMIT)
    write_json(directory / 'exit.json', dict(exitCode=code, stdoutBytes=len(out), stderrBytes=len(err)))
    return finish_audit(spec, source, rid, identity)


def finish_audit(spec, source, rid, identity):
    # Also permits data-only finalization of an already completed owned restore;
    # never reruns a failed/default restore merely to repair receipt parsing.
    directory = audit_directory(spec)
    out = regular_bytes(directory / 'stdout', AUDIT_LIMIT)
    err = regular_bytes(directory / 'stderr', AUDIT_LIMIT)
    exit_record = json.loads(regular_bytes(directory / 'exit.json', 4096))
    if exit_record != dict(exitCode=1, stdoutBytes=len(out), stderrBytes=len(err)) or type(exit_record['exitCode']) is not int:
        raise ValueError('Default restore exit/stream counts differ')
    code = exit_record['exitCode']
    findings = audit_findings(out.decode('utf-8'), err.decode('utf-8'), code, source)
    if guarded_source(spec, source) != identity:
        raise ValueError('Frozen source changed during default audit')
    record = dict(schema=1, sourceCommit=spec['sourceCommit'], sourceInputs=identity,
                  sdkActual=identity['sdk'], exception=exception_binding(spec, identity), rid=rid,
                  currentCommit=git('rev-parse', 'HEAD'), run=os.environ['GITHUB_RUN_ID'], exitCode=code,
                  stdoutSha256=hashlib.sha256(out).hexdigest(), stderrSha256=hashlib.sha256(err).hexdigest(),
                  findings=findings)
    write_json(directory / 'audit.json', record)
    return record


def verify_audit(spec, identity, rid):
    directory = audit_directory(spec)
    record = json.loads(regular_bytes(directory / 'audit.json', 32768))
    # Strict field equality also rejects unrecognized extensions to the exception.
    if type(record.get('schema')) is not int:
        raise ValueError('Audit schema must be an integer')
    source = checkout(spec)
    if source_inputs(source) != identity:
        raise ValueError('Transferred audit has different source inputs')
    out = regular_bytes(directory / 'stdout', AUDIT_LIMIT)
    err = regular_bytes(directory / 'stderr', AUDIT_LIMIT)
    # Diagnostic paths are local to the producer. Retain its exact source CWD in
    # the command receipt; it is a parse context, never a filesystem read target.
    command = json.loads(regular_bytes(directory / 'command.json', 32768))
    if (set(command) != {'argv', 'cwd', 'pid', 'deadlineSeconds'}
            or type(command['pid']) is not int or command['pid'] <= 0 or command['deadlineSeconds'] != 600
            or command['argv'][1:6] != ['restore', APP, '--locked-mode', '--verbosity', 'normal']):
        raise ValueError('Audit command is not the original default restore')
    tail = command['argv'][6:]
    if tail and (len(tail) != 2 or tail[0] != '--configfile'):
        raise ValueError('Audit command contains unauthorized properties')
    parsed_source = Path(command['cwd'])
    findings = audit_findings(out.decode('utf-8'), err.decode('utf-8'), record.get('exitCode'), parsed_source)
    expected = dict(schema=1, sourceCommit=spec['sourceCommit'], sourceInputs=identity,
                    sdkActual=identity['sdk'], exception=exception_binding(spec, identity), rid=rid,
                    currentCommit=git('rev-parse', 'HEAD'), run=os.environ['GITHUB_RUN_ID'], exitCode=1,
                    stdoutSha256=hashlib.sha256(out).hexdigest(), stderrSha256=hashlib.sha256(err).hexdigest(),
                    findings=findings)
    if record != expected:
        raise ValueError('Missing, stale or mixed-source current-run default audit')
    return record


def plan(rid):
    sdk = sdk_version(ROOT)
    inputs = dict(rid=rid, sdk=sdk, imageOS=os.environ.get('ImageOS', ''),
                  imageVersion=os.environ.get('ImageVersion', ''), recipe=list(PUBLISH),
                  helperSha256=digest(Path(__file__)), dependencies={'current': source_inputs(ROOT)['dependencies']},
                  producers={})
    sources = specs()
    # Frozen globals/locks and SDKs remain immutable; current SDK changes do not
    # relabel historical binaries or let the current compiler rewrite old locks.
    for spec in sources:
        source = checkout(spec)
        identity = guarded_source(spec, source)
        inputs['producers'][spec['family']] = identity
        inputs['dependencies'][spec['sourceCommit']] = identity['dependencies']
    inputs['exceptions'] = {s['family']: exception_binding(s, inputs['producers'][s['family']]) for s in sources}
    identity = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
    value = dict(schema=SCHEMA, cacheKey='native-producers-v3-' + rid + '-' + identity, inputs=inputs, sources=sources)
    write_json(ROOT / '.ci-native/baseline-plan.json', value)
    export('GITHUB_OUTPUT', 'cache-key', value['cacheKey'])
    versions = sorted({item['sdk'] for item in inputs['producers'].values()})
    with Path(os.environ['GITHUB_OUTPUT']).open('a', encoding='utf-8', newline='\n') as stream:
        stream.write('frozen-sdks<<FROZEN_SDK_LIST\n' + '\n'.join(versions) + '\nFROZEN_SDK_LIST\n')


def load_plan(rid):
    value = json.loads((ROOT / '.ci-native/baseline-plan.json').read_text(encoding='utf-8'))
    if (value.get('schema') != SCHEMA or value['inputs']['rid'] != rid or value['sources'] != specs()
            or value['inputs']['sdk'] != selected_sdk(ROOT)
            or value['inputs']['dependencies']['current'] != source_inputs(ROOT)['dependencies']
            or set(value['inputs']['producers']) != {spec['family'] for spec in specs()}
            or value['inputs'].get('exceptions') != {s['family']: exception_binding(s, value['inputs']['producers'][s['family']]) for s in specs()}):
        raise ValueError('Frozen producer cache plan changed')
    return value


def verify_producer(directory, spec, cache_key, rid, expected_source):
    binary = directory / ('skillview.exe' if rid.startswith('win-') else 'skillview')
    receipt = directory / 'source-receipt.json'
    if directory.is_symlink() or binary.is_symlink() or receipt.is_symlink():
        raise ValueError('Frozen producer cache must contain regular files')
    value = json.loads(regular_bytes(receipt, 32768))
    verify_audit(spec, expected_source, rid)
    if value.get('schema') != SCHEMA or value.get('sourceCommit') != spec['sourceCommit'] or value.get('trackedClean') is not True or (
            value.get('cacheKey') != cache_key or value.get('rid') != rid or
            value.get('binarySha256') != digest(binary) or value.get('recipe') != list(PUBLISH) or
            value.get('currentSdk') != selected_sdk(ROOT) or value.get('sdk') != expected_source['sdk'] or
            value.get('sourceInputs') != expected_source or
            value.get('exception') != exception_binding(spec, expected_source) or
            set(value) != {'schema', 'sourceCommit', 'trackedClean', 'binarySha256', 'cacheKey', 'rid', 'currentSdk', 'sdk', 'sourceInputs', 'recipe', 'exception'}):
        raise ValueError('Frozen producer source, recipe, SDK or binary checksum mismatch: ' + spec['family'])
    return binary


def prepare(rid):
    value = load_plan(rid)
    sdk_version(ROOT)
    for spec in value['sources']:
        source = checkout(spec)
        identity = source_inputs(source)
        if identity != value['inputs']['producers'][spec['family']]:
            raise ValueError('Frozen source globals, locks or SDK changed after planning')
        sdk = sdk_version(source)
        audit_preflight(spec, source, rid)
        props = exception_props(spec, source, identity)
        directory = ROOT / '.ci-native/baselines' / spec['family']
        if not directory.exists():
            output = source / 'dist/app'
            guarded_source(spec, source)
            check_props(props, source)
            publish_frozen(['dotnet', 'publish', str(source / 'src/SkillView.App/SkillView.App.csproj'),
                            *PUBLISH, '-p:CustomBeforeMicrosoftCommonProps=' + str(props), '-r', rid, '-o', str(output)], source, spec)
            checkout(spec)  # Publication must not change tracked source.
            if source_inputs(source) != identity:
                raise ValueError('Frozen source inputs changed during publication')
            directory.mkdir(parents=True)
            binary = output / ('skillview.exe' if rid.startswith('win-') else 'skillview')
            shutil.copy2(binary, directory / binary.name)
            write_json(directory / 'source-receipt.json', dict(schema=SCHEMA, sourceCommit=spec['sourceCommit'],
                trackedClean=True, binarySha256=digest(binary), cacheKey=value['cacheKey'], rid=rid,
                currentSdk=value['inputs']['sdk'], sdk=sdk, sourceInputs=identity, recipe=list(PUBLISH),
                exception=exception_binding(spec, identity)))
        binary = verify_producer(directory, spec, value['cacheKey'], rid, identity)
        subprocess.run([str(binary), '--version'], cwd=ROOT, check=True, timeout=30)


def publish_frozen(argv, source, spec):
    if source.resolve() == ROOT.resolve():
        raise ValueError('Current/release builds cannot use the historical exception')
    prefix = ['dotnet', 'publish', str(source / APP), *PUBLISH]
    tail = argv[len(prefix):]
    if (argv[:len(prefix)] != prefix or len(tail) != 5
            or not tail[0].startswith('-p:CustomBeforeMicrosoftCommonProps=')
            or tail[1] != '-r' or tail[2] not in RIDS or tail[3:] != ['-o', str(source / 'dist/app')]):
        raise ValueError('Historical publish recipe changed')
    identity = guarded_source(spec, source)
    verify_audit(spec, identity, tail[2])
    check_props(Path(tail[0].split('=', 1)[1]), source)
    directory = audit_directory(spec)
    with (directory / 'publish.stdout').open('xb') as out, (directory / 'publish.stderr').open('xb') as err:
        subprocess.run(argv, cwd=source, stdout=out, stderr=err, check=True)
    for name in ('publish.stdout', 'publish.stderr'):
        text = regular_bytes(directory / name, AUDIT_LIMIT).decode('utf-8')
        # Preserve the frozen compiler/linker warning policy and strict IL
        # flags. This additional gate concerns only NuGet/audit diagnostics.
        if re.search(r'\bNU[0-9]{4}\b', text, re.I):
            raise ValueError('Historical publish emitted an unexpected NuGet diagnostic')


def input_files(rid):
    ext = '.exe' if rid.startswith('win-') else ''
    paths = [ROOT / ('dist/app/skillview' + ext), ROOT / ('dist/ext/gh-skillview' + ext)]
    for directory in ('.ci-native/baselines', '.ci-native/frozen-audits', '.ci-native/gh-minimum', '.ci-native/gh-current',
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
    value = load_plan(rid)
    for spec in value['sources']:
        verify_producer(ROOT / '.ci-native/baselines' / spec['family'], spec, value['cacheKey'], rid, source_inputs(checkout(spec)))
    paths = input_files(rid)
    manifest = dict(schema=SCHEMA, commit=git('rev-parse', 'HEAD'), rid=rid, cacheKey=value['cacheKey'],
                    currentSdk=value['inputs']['sdk'], currentDependencies=value['inputs']['dependencies']['current'],
                    producerInputs=value['inputs']['producers'], exceptions=value['inputs']['exceptions'],
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
        if (manifest.get('schema') != SCHEMA or manifest['rid'] != rid or
                manifest['currentSdk'] != selected_sdk(ROOT) or
                manifest['currentDependencies'] != source_inputs(ROOT)['dependencies'] or
                manifest['commit'] != git('rev-parse', 'HEAD') or set(names) != {
                manifest_name, *manifest['files']}):
            raise ValueError('Native tool artifact does not belong to this commit/RID or is incomplete')
        expected_producers = {spec['family']: source_inputs(checkout(spec)) for spec in specs()}
        if (manifest['producerInputs'] != expected_producers or manifest.get('exceptions') != {s['family']: exception_binding(s, expected_producers[s['family']]) for s in specs()}):
            raise ValueError('Transferred frozen SDK/global/lock identities differ from exact sources')
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
        verify_producer(ROOT / '.ci-native/baselines' / spec['family'], spec, manifest['cacheKey'], rid,
                        manifest['producerInputs'][spec['family']])
    for version, name in [('2.102.0', 'minimum'), ('2.103.3', 'current')]:
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
    if manifest.get('schema') != SCHEMA or manifest['currentSdk'] != selected_sdk(ROOT):
        raise ValueError('Staging requires current schema and SDK identities')
    for spec in specs():
        if manifest.get('exceptions') != {s['family']: exception_binding(s, manifest['producerInputs'][s['family']]) for s in specs()}:
            raise ValueError('Staging requires independently bound owner disposition')
        if shard == 'sources' or spec['family'] == 'reviewed-merge' or (shard == 'inspection-unpin' and spec['family'] == 'legacy'):
            source = checkout(spec)
            if source_inputs(source) != manifest['producerInputs'][spec['family']]:
                raise ValueError('Frozen source identity changed before staging')
            binary = verify_producer(ROOT / '.ci-native/baselines' / spec['family'], spec, manifest['cacheKey'], rid,
                        manifest['producerInputs'][spec['family']])
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
