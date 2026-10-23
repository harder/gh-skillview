"""Linux native capability proof, using a private gh inventory protocol fixture.

This is a filesystem/ABI regression, not a real gh contract test. A seccomp
wrapper denies openat2 before each fresh native SkillView process starts.
"""
import argparse
import errno
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve(strict=True)
compiler = shutil.which('cc') or shutil.which('clang')
assert compiler, 'A C compiler is required for the Linux seccomp regression'
results = []

with tempfile.TemporaryDirectory(prefix='skillview-no-openat2-') as temporary:
    root = Path(temporary)
    wrapper = root / 'deny-openat2'
    subprocess.run([compiler, '-Wall', '-Wextra', '-Werror', '-o', str(wrapper),
                    str(Path(__file__).with_name('deny-openat2.c'))], check=True, timeout=60)
    home = root / 'home'
    skills = home / '.agents' / 'skills'
    for name in ('owned', 'unowned'):
        payload = skills / name
        payload.mkdir(parents=True)
        (payload / 'SKILL.md').write_text(f'---\nname: {name}\ndescription: Fixture\n---\nInstructions\n')
        (payload / 'keep.txt').write_text('keep')
    lock = skills.parent / '.skill-lock.json'
    lock.write_text(json.dumps({'version': 3, 'skills': {'owned': {
        'source': 'fixture/repository', 'sourceType': 'github',
        'skillPath': 'skills/owned/SKILL.md'
    }}}))
    builtin = home / '.codex/skills/.system/builtin'
    builtin.mkdir(parents=True)
    builtin.joinpath('SKILL.md').write_text('---\nname: builtin\ndescription: Built-in fixture\n---\nBody\n')
    gh = root / 'gh'
    gh.write_text('''#!/bin/sh
case "$*" in
  --version) printf 'gh version 2.97.0 (fixture)\n' ;;
  'skill --help') printf 'Skill inventory fixture\n' ;;
  'skill list'*) printf '[]\n' ;;
  *) exit 1 ;;
esac
''')
    gh.chmod(0o700)
    env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith('GIT_')}
    env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
               GH_CONFIG_DIR=str(root / 'gh-config'), GH_TELEMETRY='false',
               XDG_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
               XDG_CACHE_HOME=str(root / 'cache'), XDG_DATA_HOME=str(root / 'data'),
               CLAUDE_CONFIG_DIR=str(root / 'claude'), PI_CODING_AGENT_DIR=str(root / 'pi'))
    before = {str(path): path.read_bytes() for path in root.rglob('*') if path.is_file()}
    for denial in (errno.EPERM, errno.ENOSYS):
        def run(command):
            return subprocess.run([str(wrapper), str(denial), str(app), '--scan-root', str(skills)] + command,
                                  env=env, cwd=root, capture_output=True, text=True, timeout=120)

        inventory_process = run(['list', '--json'])
        assert inventory_process.returncode == 0, (inventory_process.stdout, inventory_process.stderr)
        inventory = json.loads(inventory_process.stdout)
        assert {row['name'] for row in inventory['skills']} == {'owned', 'unowned', 'builtin'}, inventory
        assert all(row['validity'] == 'Valid' for row in inventory['skills']), inventory
        assert any(owner['source'] == 'fixture/repository' for owner in inventory['ownerRecords']), inventory
        observed_builtin = next(row for row in inventory['skills'] if row['name'] == 'builtin')
        assert any(owner['provider'] == 'codex-builtin' and owner['readOnly'] for owner in observed_builtin['ownerRecords']), observed_builtin
        removal = run(['remove', 'unowned', '--yes', '--json'])
        assert removal.returncode != 0, removal.stdout
        assert 'openat2' in removal.stdout + removal.stderr, (removal.stdout, removal.stderr)
        assert all(Path(path).read_bytes() == content for path, content in before.items()), 'Fixture changed after refused removal'
        results.append({'deniedErrno': denial, 'validMetadataObserved': True,
                        'ownerLockObserved': True, 'managedBuiltinObservedReadOnly': True,
                        'removalCapabilityRefusedWithoutMutation': True})
print(json.dumps(results, indent=2))
