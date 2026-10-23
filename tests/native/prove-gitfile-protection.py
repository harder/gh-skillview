"""Prove repository/worktree removal refusals only inside disposable roots."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
results = []

for gh in args.gh or [None]:
    with tempfile.TemporaryDirectory(prefix='skillview-gitfile-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        templates = root / 'templates'
        templates.mkdir()
        env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'state'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_SYSTEM=os.devnull,
                   GIT_TEMPLATE_DIR=str(templates), GIT_NO_OPTIONAL_LOCKS='1',
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        if gh:
            env.update(GH_EXTENSION='1', GH_PATH=str(gh.resolve()))
        else:
            env.pop('GH_EXTENSION', None)
            env.pop('GH_PATH', None)

        def git(*command):
            process = subprocess.run(['git', '-c', 'user.name=SkillView Fixture', '-c', 'user.email=fixture@localhost',
                                      '-c', 'commit.gpgsign=false', '-c', 'core.autocrlf=false',
                                      '-c', 'core.hooksPath=' + str(templates), *map(str, command)],
                                     env=env, cwd=root, capture_output=True, text=True, timeout=60)
            assert process.returncode == 0, (process.stdout, process.stderr)
            return process.stdout

        repo = root / 'repository'
        git('init', '--quiet', repo)
        repo.joinpath('SKILL.md').write_text('---\nname: worktree-skill\ndescription: Fixture\n---\nBody\n')
        repo.joinpath('resource.txt').write_text('tracked resource')
        git('-C', repo, 'add', 'SKILL.md', 'resource.txt')
        git('-C', repo, 'commit', '--quiet', '-m', 'Fixture')
        skills = root / 'skills'
        skills.mkdir()
        worktree = skills / 'linked-worktree'
        git('-C', repo, 'worktree', 'add', '--quiet', '--detach', worktree, 'HEAD')
        assert worktree.is_relative_to(root) and worktree.joinpath('.git').is_file()

        def digest(path):
            return hashlib.sha256(path.read_bytes()).hexdigest()

        before = {path.name: digest(path) for path in worktree.iterdir() if path.is_file()}

        def refused(name):
            process = subprocess.run([str(app), '--scan-root', str(skills), 'remove', name, '--yes', '--json'],
                                     env=env, cwd=root, capture_output=True, text=True, timeout=120)
            assert process.returncode == 1, (process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            assert not result['allowed'] and not result['succeeded'], result
            assert any(row['kind'] == 'ContainsGitDirectory' for row in result['errors']), result
            assert result['filesDeleted'] == 0 and result['directoriesDeleted'] == 0, result
            assert result['runtimeErrors'] == [], result

        refused('worktree-skill')
        assert {path.name: digest(path) for path in worktree.iterdir() if path.is_file()} == before
        assert git('-C', worktree, 'rev-parse', '--is-inside-work-tree').strip() == 'true'
        assert git('-C', worktree, 'status', '--porcelain') == ''
        broken = skills / 'broken-git-link'
        broken.mkdir()
        broken.joinpath('SKILL.md').write_text('---\nname: broken-git-skill\ndescription: Fixture\n---\nBody\n')
        broken.joinpath('.git').symlink_to(root / 'missing-metadata')
        broken_before = digest(broken.joinpath('SKILL.md'))
        refused('broken-git-skill')
        assert digest(broken.joinpath('SKILL.md')) == broken_before
        assert broken.joinpath('.git').is_symlink() and not root.joinpath('missing-metadata').exists()
        results.append(dict(nativeHost=str(app), nativeSha256=digest(app), gh=str(gh) if gh else 'runner default',
                            actualLinkedWorktreeRefused=True, trackedPayloadAndGitfileUnchanged=True,
                            worktreeStillUsableAndClean=True, brokenGitLinkRefusedWithoutFollowing=True,
                            zeroDeletionAndNoDuplicatedRuntimeErrors=True))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
