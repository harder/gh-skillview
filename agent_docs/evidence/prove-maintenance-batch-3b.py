"""Native discovery proof: local-only gh installs and scans under a private home."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', required=True, action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
results = []

for gh in args.gh:
    with tempfile.TemporaryDirectory(prefix='skillview-discovery-proof-') as temporary:
        root = Path(temporary)
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'),
                   XDG_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_CACHE_HOME=str(root / 'cache'), XDG_DATA_HOME=str(root / 'data'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_EXTENSION='1', GH_PATH=str(gh), GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')

        def skill(directory, name):
            directory.mkdir(parents=True, exist_ok=True)
            directory.joinpath('SKILL.md').write_text(f'---\nname: {name}\ndescription: Fixture\n---\nInstructions\n')

        a, b, c = (root / name for name in ('a', 'b', 'c'))
        for workspace in (a, b, c):
            workspace.mkdir()
            subprocess.run(['git', 'init', '--quiet', str(workspace)], env=env, cwd=root, check=True)
        current = a / 'packages' / 'app'
        current.mkdir(parents=True)
        skill(a / '.opencode' / 'skills' / 'a-base', 'a-base')
        skill(a / 'packages' / '.claude' / 'skills' / 'a-intermediate', 'a-intermediate')
        skill(c / '.github' / 'skills' / 'workspace-extra', 'workspace-extra')
        custom = root / 'custom'
        skill(custom / 'author' / 'namespace-demo', 'namespace-demo')
        profile = root / 'profile'
        skill(profile / 'profile-demo', 'profile-demo')
        source = root / 'source'
        skill(source / 'b-native', 'b-native')
        env.update(GIT_DIR=str(b / '.git'), GIT_WORK_TREE=str(b))
        installed = subprocess.run([str(gh), 'skill', 'install', '--agent', 'claude-code', '--scope', 'project',
                                    '--from-local', '--', str(source), 'b-native'],
                                   env=env, cwd=current, capture_output=True, text=True, timeout=60)
        assert installed.returncode == 0, installed.stderr
        assert b.joinpath('.claude/skills/b-native/SKILL.md').is_file()
        global_flags = ['--scan-root', str(custom), '--workspace', str(c), '--profile', f'claude:work={profile}']
        if os.name != 'nt':
            alias = root / 'alias'
            alias.symlink_to(custom, target_is_directory=True)
            global_flags += ['--scan-root', str(alias)]

        def run(command):
            process = subprocess.run([str(args.app)] + global_flags + command, env=env, cwd=current,
                                     capture_output=True, text=True, timeout=120)
            assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        inventory = run(['list', '--json'])
        assert not inventory['partial'], inventory
        names = [row['name'] for row in inventory['skills']]
        assert set(names) == {'a-base', 'a-intermediate', 'workspace-extra', 'b-native', 'namespace-demo', 'profile-demo'}, names
        namespace = next(row for row in inventory['skills'] if row['name'] == 'namespace-demo')
        assert namespace['validity'] == 'Valid' and names.count('namespace-demo') == 1
        profile_row = next(row for row in inventory['skills'] if row['name'] == 'profile-demo')
        assert any(agent['profile'] == 'work' for agent in profile_row['agents'])
        assert all(agent['enabled'] is None and agent['activeSessionState'] == 'unknown' for agent in profile_row['agents'])
        assert any(location['state'] == 'container' for location in inventory['discoveryLocations'])
        assert all(Path(row['resolvedPath']).resolve().is_relative_to(root.resolve()) for row in inventory['skills'])
        broken = custom / 'broken-exposure'
        broken.symlink_to(root / 'missing-target', target_is_directory=True)
        broken_inventory = run(['list', '--json'])
        broken_row = next(row for row in broken_inventory['skills'] if row['name'] == 'broken-exposure')
        assert broken_row['validity'] == 'BrokenSymlink', broken_row
        assert not broken_inventory['partial'], broken_inventory
        repaired = run(['cleanup', '--candidates', 'BrokenSymlink', '--apply', '--yes', '--json'])
        assert any(row['kind'] == 'BrokenSymlink' for row in repaired['entries']), repaired
        assert len(repaired['applied']) == 1 and repaired['applied'][0]['succeeded'], repaired
        assert not broken.is_symlink(), repaired
        deep = custom.joinpath(*(['namespace'] * 10), 'unobserved')
        skill(deep, 'unobserved')
        partial = run(['list', '--json'])
        assert partial['partial'] and any(location['state'] == 'budget-exceeded' for location in partial['discoveryLocations'])
        cleanup = run(['cleanup', '--json'])
        assert cleanup['partial'] and cleanup['inaccessiblePaths'], cleanup
        assert all(candidate['kind'] not in ('Untracked', 'SourceOrphaned', 'Duplicate') for candidate in cleanup['entries']), cleanup
        results.append(dict(gh=subprocess.check_output([str(gh), '--version'], text=True, env=env, cwd=current).splitlines()[0],
                            nativeHost=str(args.app), redirectedLocalInstallObserved=True,
                            monorepoAndLegacyWorkspaceRootsObserved=True, namedProfileContextPreserved=True,
                            namespaceContainersSeparated=True, physicalAliasesMerged=True,
                            danglingExposureIdentityPinnedCleanupVerified=True,
                            depthBudgetReportsPartialCoverage=True, incompleteCoverageCannotRecommendOrphanCleanup=True))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
