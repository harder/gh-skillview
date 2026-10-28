"""Real installed gh registry source selection, raw damage retention and exact Undo.

No token extraction, credential copying, fabricated positive registry or source
execution. Existing gh config resolution may refresh credentials normally.
Fixtures and all initial failures remain at the reported private root.
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

from source_review_proof import acknowledge_frozen_source
from registry_proof_process import RegistryProofArtifacts, run_checkpoint_child, write_proof_marker


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def native(path):
    result = []
    for item in [path, *sorted(path.rglob('*'))]:
        info = item.lstat()
        kind = 'link' if stat.S_ISLNK(info.st_mode) else 'directory' if stat.S_ISDIR(info.st_mode) else 'file'
        result.append(dict(path='/' if item == path else item.relative_to(path).as_posix(), kind=kind,
                           device=info.st_dev, inode=info.st_ino, mode=stat.S_IMODE(info.st_mode),
                           uid=info.st_uid, gid=info.st_gid, mtimeNs=info.st_mtime_ns,
                           value=os.readlink(item) if kind == 'link' else sha(item) if kind == 'file' else None,
                           xattrs={key: os.getxattr(item, key, follow_symlinks=False).hex()
                                   for key in os.listxattr(item, follow_symlinks=False)} if hasattr(os, 'listxattr') else {}))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--gh', action='append', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--expect-metadata-refusal', choices=['CAP_SYS_ADMIN'])
    parser.add_argument('--old-app', type=Path)
    parser.add_argument('--proof-host', type=Path)
    args = parser.parse_args()
    input_app = args.app.resolve()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix='registry-restore-')).resolve()
    root.chmod(0o700)
    frozen = root / 'frozen-host'
    bindings = {p.name: sha(p) for p in input_app.parent.iterdir() if p.is_file()}
    shutil.copytree(input_app.parent, frozen)
    assert bindings == {p.name: sha(p) for p in input_app.parent.iterdir() if p.is_file()}, 'Input host changed while freezing'
    assert bindings == {p.name: sha(p) for p in frozen.iterdir() if p.is_file()}, 'Frozen host differs from input bytes'
    app = frozen / input_app.name
    config = os.environ.get('GH_CONFIG_DIR') or str(Path(os.environ.get('XDG_CONFIG_HOME', str(Path.home() / '.config'))) / 'gh')
    report = dict(proofScriptSha256=sha(Path(__file__)), root=str(root), inputApp=str(input_app), frozenBindings=bindings, app=str(app), appSha256=sha(app), cases=[], status='running')
    # Bind managed hosts to the actual Core and host DLLs, not the apphost alone.
    artifacts = RegistryProofArtifacts(output)
    report['rawArtifacts'] = artifacts.summary
    report['managedBindings'] = {p.name: sha(p) for p in sorted(app.parent.glob('*.dll'))}
    output.write_text(json.dumps(report, indent=2))
    sequence = 0
    try:
        for tool in args.gh:
            gh = tool.resolve()
            for pinned in (True, False):
                for damage in ('corrupt-yaml', 'missing-empty', 'missing-corroborated'):
                    case_root = root / (gh.parent.name + ('-pinned-' if pinned else '-unpinned-') + damage)
                    case_root.mkdir(mode=0o700)
                    home, work = case_root / 'home', case_root / 'work'
                    home.mkdir(); work.mkdir(); (case_root / 'custom').mkdir(); (case_root / 'tmp').mkdir()
                    env = {k: v for k, v in os.environ.items() if not k.startswith(('GIT_', 'CODEX_', 'CLAUDE_', 'PI_', 'XDG_', 'SKILLVIEW_', 'GEMINI_', 'COPILOT_', 'OPENCODE_'))}
                    env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=config,
                               GH_PATH=str(gh), GH_EXTENSION='1', GH_PROMPT_DISABLED='1',
                               GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1',
                               XDG_CONFIG_HOME=str(case_root / 'config'), XDG_STATE_HOME=str(case_root / 'state'),
                               XDG_CACHE_HOME=str(case_root / 'cache'), XDG_DATA_HOME=str(case_root / 'data'),
                               SKILLVIEW_STATE_HOME=str(case_root / 'skillview'),
                               APPDATA=str(case_root / 'appdata'), LOCALAPPDATA=str(case_root / 'localappdata'),
                               CODEX_HOME=str(home / '.codex'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                               CLAUDE_CODE_PLUGIN_CACHE_DIR=str(home / '.claude/plugins'), CLAUDE_CODE_PLUGIN_DIRS='', CLAUDE_CODE_PLUGIN_SEED_DIR='',
                               PI_CODING_AGENT_DIR=str(home / '.pi/agent'), GEMINI_CLI_HOME=str(home),
                               GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0', TMPDIR=str(case_root / 'tmp'))
                    receipt_dir = case_root / 'receipts'; receipt_dir.mkdir()

                    def command(argv, expect=0, timeout=45, process_environment=None, process_cwd=None):
                        nonlocal sequence
                        sequence += 1
                        prefix = receipt_dir / f'{sequence:04d}'
                        try:
                            p = subprocess.run(list(map(str, argv)), env=process_environment or env, cwd=process_cwd or case_root, stdin=subprocess.DEVNULL,
                                               capture_output=True, timeout=timeout)
                            prefix.with_suffix('.stdout').write_bytes(p.stdout)
                            prefix.with_suffix('.stderr').write_bytes(p.stderr)
                            prefix.with_suffix('.json').write_text(json.dumps(dict(argv=list(map(str, argv)), exit=p.returncode), indent=2))
                        except subprocess.TimeoutExpired as ex:
                            prefix.with_suffix('.stdout').write_bytes(ex.stdout or b'')
                            prefix.with_suffix('.stderr').write_bytes(ex.stderr or b'')
                            prefix.with_suffix('.json').write_text(json.dumps(dict(argv=list(map(str, argv)), timeout=timeout), indent=2))
                            raise
                        if expect is not None:
                            assert (p.returncode == 0) == (expect == 0), (argv, p.returncode, p.stdout.decode(errors='replace'), p.stderr.decode(errors='replace'))
                        return p

                    write_proof_marker(case_root)
                    command(['git', '-C', work, 'init', '--quiet'])
                    env.update(GIT_DIR=str(work / '.git'), GIT_WORK_TREE=str(work), GIT_COMMON_DIR=str(work / '.git'))
                    # A genuine source with nonroot resources supplies corroboration.
                    name = 'cli-exercise' if damage == 'missing-corroborated' else 'gh-skill'
                    source = '.github/skills/cli-exercise' if name == 'cli-exercise' else 'skills/gh-skill'
                    install = [gh, 'skill', 'install', '--agent', 'universal', '--scope', 'user', '--allow-hidden-dirs']
                    if pinned: install += ['--pin=v2.103.2']
                    command([*install, '--', 'cli/cli', source])
                    payload = home / '.agents/skills' / name
                    registry = home / '.agents/.skill-lock.json'
                    registry_bytes, registry_native = registry.read_bytes(), native(registry)
                    (case_root / 'registry-original.json').write_bytes(registry_bytes)
                    # Preserve actual installation before creating damage; no cleanup.
                    installed_original = case_root / 'installed-original'
                    payload.rename(installed_original)
                    shutil.copytree(installed_original, payload)
                    file = payload / 'SKILL.md'
                    if damage == 'corrupt-yaml': file.write_bytes(b'---\nname: [unterminated\n---\n\x00raw damaged original\n')
                    else: file.rename(case_root / 'removed-SKILL.md')
                    if damage == 'missing-empty': assert not list(payload.iterdir())
                    if damage == 'missing-corroborated':
                        # The actual source has a genuine root .gitignore. Keep
                        # its exact installed bytes/mode as corroboration; retain
                        # nested source resources outside the damaged subject so
                        # discovery selects one ordinary nonnested copy.
                        removed = case_root / 'removed-resource-directories'; removed.mkdir()
                        for directory in list(payload.iterdir()):
                            if directory.is_dir(): directory.rename(removed / directory.name)
                        assert payload.joinpath('.gitignore').is_file()
                        assert not any(p.is_dir() for p in payload.iterdir())
                    damaged = native(payload)
                    (case_root / 'damaged-native.json').write_text(json.dumps(damaged, indent=2))

                    def run(action, *arguments, ok=True):
                        p = command([app, '--scan-root', case_root / 'custom', action, *arguments, '--allow-hidden-dirs', '--json'], 0 if ok else 1, 180)
                        return json.loads(p.stdout)

                    captured = run('list')
                    selected = [s for s in captured['skills'] if s['resolvedPath'] == str(payload)]
                    assert len(selected) == 1, captured
                    planned = run('registry-restore-plan', '--path', payload, ok=not args.expect_metadata_refusal)
                    case = dict(gh=str(gh), ghSha256=sha(gh), pinned=pinned, damage=damage, root=str(case_root))
                    report['cases'].append(case)
                    if args.expect_metadata_refusal:
                        assert 'CAP_SYS_ADMIN' in json.dumps(planned), planned
                        assert native(payload) == damaged and registry.read_bytes() == registry_bytes
                        case['status'] = 'expected-metadata-capability-refusal'
                        output.write_text(json.dumps(report, indent=2)); continue
                    plan = planned['operation']; case['plan'] = plan
                    assert plan['registryRestore']['observedValidity'] in (1, 2), plan
                    desired = plan['registryRestore']['desiredMetadata']
                    assert desired['githubTreeSha'] == json.loads(registry_bytes)['skills'][name]['skillFolderHash']
                    assert desired['githubRef'] == ('refs/tags/v2.103.2' if pinned else None), desired
                    assert plan.get('revision') is None and plan.get('localImport') is None
                    assert plan['subject']['metadata']['githubRef'] is None
                    assert 'does not record which folder' in plan['message']
                    assert native(payload) == damaged and registry.read_bytes() == registry_bytes
                    # Independent retention cannot bypass source acknowledgement.
                    run('registry-restore-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', plan['planFingerprint'], ok=False)
                    run('restore-inspect', '--id', plan['id'], ok=False)
                    run('registry-restore-ack-source', '--id', plan['id'], '--yes', '--expect', plan['planFingerprint'], '--review', '0' * 64, ok=False)
                    acknowledge_frozen_source(run, plan, 'registry-restore')
                    run('registry-restore-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', '0' * 64, ok=False)
                    if args.old_app:
                        old = command([args.old_app.resolve(), 'restore-inspect', '--id', plan['id'], '--allow-hidden-dirs', '--json'], expect=1, timeout=180)
                        case['oldReaderRefusal'] = json.loads(old.stdout)
                    candidate = native(Path(plan['candidatePath']))
                    if args.proof_host and damage != 'missing-corroborated':
                        boundary = 'retain-moved' if damage == 'corrupt-yaml' else 'install-moved'
                        argv = [str(args.proof_host.resolve()), str(case_root), str(gh), plan['id'], plan['planFingerprint'], 'apply-registry-restore', boundary]
                        case['proofChild'] = {}
                        run_checkpoint_child(argv, env, case_root, case_root / 'kill.stdout', case_root / 'kill.stderr',
                                             boundary, case['proofChild'], artifacts, str(case_root), gh=gh,
                                             ready_timeout=45, failure_message='Proof host did not reach ' + boundary)
                        case['killedAt'] = boundary
                        interrupted = run('registry-restore-inspect', '--id', plan['id'])['operation']
                        applied = run('registry-restore-recover', '--id', plan['id'], '--yes', '--expect', interrupted['reviewFingerprint'])['operation']
                    else:
                        applied = run('registry-restore-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', plan['planFingerprint'])['operation']
                    assert applied['status'] == 'installed', applied
                    assert native(payload) == candidate
                    assert native(Path(applied['retainedPath'])) == damaged
                    assert registry.read_bytes() == registry_bytes and native(registry) == registry_native
                    if not pinned:
                        run('update-plan', '--path', payload, ok=False)
                        run('update-plan', '--path', payload, '--tag', 'v2.103.2', ok=False)
                    current = run('registry-restore-inspect', '--id', plan['id'])['operation']
                    restored = run('registry-restore-undo', '--id', plan['id'], '--yes', '--expect', current['reviewFingerprint'])['operation']
                    assert restored['status'] == 'restored', restored
                    assert native(payload) == damaged and native(Path(restored['displacedPath'])) == candidate
                    assert registry.read_bytes() == registry_bytes and native(registry) == registry_native
                    case['status'] = 'applied-and-exact-native-undo'
                    case['finalDamagedNative'] = native(payload)
                    # Negative authority probes begin only after the complete
                    # positive registry/native Undo assertions above.
                    negative = run('registry-restore-plan', '--path', payload)['operation']
                    owner_before_negative = native(registry)
                    value = json.loads(registry_bytes)
                    value['skills'][name]['skillFolderHash'] = '0' * 40
                    registry.write_text(json.dumps(value))
                    (case_root / 'deliberately-changed-owner.json').write_bytes(registry.read_bytes())
                    changed_owner = run('registry-restore-inspect', '--id', negative['id'], ok=False)
                    registry.write_bytes(registry_bytes)
                    assert registry.read_bytes() == registry_bytes
                    assert registry.stat().st_ino == owner_before_negative[0]['inode']
                    pin_mismatch = None
                    if pinned and name == 'gh-skill':
                        value = json.loads(registry_bytes)
                        # This real earlier cli/cli subtree was independently
                        # verified by the supplied source preflight. Pairing it
                        # with the intact v2.103.2 pin is a deliberate negative
                        # registry corruption, never positive authority.
                        value['skills'][name]['skillFolderHash'] = '344b0386c6247af40b29df273965105047e329fb'
                        registry.write_text(json.dumps(value))
                        pin_mismatch = run('registry-restore-plan', '--path', payload, ok=False)
                        assert 'resolved exact pin' in json.dumps(pin_mismatch), pin_mismatch
                        registry.write_bytes(registry_bytes)
                    source_file = Path(negative['candidatePath']) / 'SKILL.md'
                    (case_root / 'candidate-before-negative-SKILL.md').write_bytes(source_file.read_bytes())
                    source_file.write_bytes(source_file.read_bytes() + b'\nDeliberate native/source drift for refusal proof\n')
                    changed_source_native = run('registry-restore-inspect', '--id', negative['id'], ok=False)
                    assert native(payload) == damaged
                    case['negatives'] = dict(changedOwner=changed_owner, changedSourceNative=changed_source_native,
                                             deliberateOwnerEditAfterPositiveProof=True, pinTreeMismatch=pin_mismatch)
                    if damage == 'missing-corroborated':
                        # Retain the exact successfully undone native original;
                        # use a separate live copy for mismatch admission.
                        raw_undo = case_root / 'exact-undo-original-before-negative'
                        payload.rename(raw_undo); shutil.copytree(raw_undo, payload)
                        with payload.joinpath('.gitignore').open('ab') as stream: stream.write(b'\nDeliberate corroboration mismatch\n')
                        mismatch = run('registry-restore-plan', '--path', payload, ok=False)
                        assert 'corroborat' in json.dumps(mismatch), mismatch
                        case['negatives']['corroborationMismatch'] = mismatch
                        case['negatives']['rawUndoOriginalRetainedAt'] = str(raw_undo)
                    output.write_text(json.dumps(report, indent=2))
            if not args.expect_metadata_refusal:
                # Two genuine custom installations in distinct Git/CWD contexts
                # share one global selector record; neither acquires an attached
                # claim by name. Never fabricate positive owner authority.
                contexts = []
                for suffix in ('a', 'b'):
                    project = case_root / ('independent-context-' + suffix); project.mkdir()
                    custom_env = dict(env, GIT_DIR=str(project / 'repo.git'), GIT_COMMON_DIR=str(project / 'repo.git'), GIT_WORK_TREE=str(project))
                    command(['git', 'init', '--quiet'], process_environment=custom_env, process_cwd=project)
                    custom = project / 'custom-skills'
                    command([gh, 'skill', 'install', '--dir', custom, '--', 'cli/cli', 'skills/gh-skill@v2.103.2'], process_environment=custom_env, process_cwd=project)
                    contexts.append((project, custom_env, custom))
                (contexts[0][2] / 'gh-skill/SKILL.md').write_bytes(b'---\nname: [invalid\n---\nRaw custom damage\n')
                before_customs = [native(item[2] / 'gh-skill') for item in contexts]
                before_registry = registry.read_bytes()
                assert list(json.loads(before_registry)['skills']).count('gh-skill') == 1
                project, custom_env, _ = contexts[0]
                arguments = [app, '--scan-root', contexts[0][2], '--scan-root', contexts[1][2]]
                listed = json.loads(command([*arguments, 'list', '--allow-hidden-dirs', '--json'], process_environment=custom_env, process_cwd=project).stdout)
                for item in contexts:
                    row = [s for s in listed['skills'] if s['resolvedPath'] == str(item[2] / 'gh-skill')]
                    assert len(row) == 1 and row[0]['ownerRecords'] == [], row
                refused = command([*arguments, 'registry-restore-plan', '--path', contexts[0][2] / 'gh-skill', '--allow-hidden-dirs', '--json'], expect=1, process_environment=custom_env, process_cwd=project)
                refusal = json.loads(refused.stdout)
                assert 'already-attached' in json.dumps(refusal), refusal
                assert before_customs == [native(item[2] / 'gh-skill') for item in contexts]
                assert before_registry == registry.read_bytes()
                report['cases'].append(dict(gh=str(gh), pinned=False, damage='two-custom-contexts', status='refused-zero-attached-claims', refusal=refusal))
                output.write_text(json.dumps(report, indent=2))
        report['status'] = 'passed'
    except BaseException as ex:
        report['status'] = 'failed'; report['failure'] = repr(ex)
        raise
    finally:
        output.write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
