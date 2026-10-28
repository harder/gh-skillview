"""Run bounded registry restore proofs with full Linux metadata authority.

Only binaries, proof tools and a private result directory are mounted. With
--gh-config-dir, gh receives its normal existing delegated configuration; the
harness never reads/copies credentials. CI passes its already supplied token
through Docker's environment-name forwarding, without extracting its value.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--gh', action='append', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--gh-config-dir', type=Path)
    parser.add_argument('--dotnet-root', type=Path)
    parser.add_argument('--old-app', type=Path)
    parser.add_argument('--proof-host', type=Path)
    parser.add_argument('--retirement', action='store_true', help='Run the dedicated F1/H2 interoperability proof instead')
    parser.add_argument('--image', default='mcr.microsoft.com/dotnet/sdk:10.0.113')
    args = parser.parse_args()
    app = args.app.resolve()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    tools = Path(__file__).resolve().parent
    container = 'skillview-registry-restore-' + uuid.uuid4().hex[:12]
    command = ['docker', 'run', '--name', container, '--privileged', '--network=host',
               '--mount', f'type=bind,src={app.parent},dst=/proof-app,readonly',
               '--mount', f'type=bind,src={tools},dst=/proof-tools,readonly',
               '--mount', f'type=bind,src={output},dst=/proof-output',
               '--env', 'GH_TOKEN', '--env', 'GITHUB_TOKEN']
    proof = ['python3', '/proof-tools/prove-registry-recorded-restore.py', '--app', '/proof-app/' + app.name,
             '--output', '/proof-output/result.json']
    if args.retirement:
        if not args.proof_host or len(args.gh) != 1 or args.old_app:
            parser.error('--retirement requires one --gh and --proof-host, without --old-app')
        proof = ['python3', '/proof-tools/prove-registry-recorded-retirement.py', '--app', '/proof-app/' + app.name,
                 '--output', '/proof-output/result.json']
    for i, item in enumerate(args.gh):
        gh = item.resolve()
        command += ['--mount', f'type=bind,src={gh.parent},dst=/proof-gh-{i},readonly']
        proof += ['--gh', f'/proof-gh-{i}/{gh.name}']
    for key, item in [('old-app', args.old_app), ('proof-host', args.proof_host)]:
        if item:
            item = item.resolve()
            command += ['--mount', f'type=bind,src={item.parent},dst=/proof-{key},readonly']
            proof += ['--proof' if args.retirement and key == 'proof-host' else '--' + key, f'/proof-{key}/{item.name}']
    if args.gh_config_dir:
        command += ['--mount', f'type=bind,src={args.gh_config_dir.resolve()},dst=/proof-gh-config',
                    '--env', 'GH_CONFIG_DIR=/proof-gh-config']
    if args.dotnet_root:
        command += ['--mount', f'type=bind,src={args.dotnet_root.resolve()},dst=/proof-dotnet,readonly',
                    '--env', 'DOTNET_ROOT=/proof-dotnet']
    # Keep private originals inside the container until copied to the bounded
    # output at exit. Do not put fixtures on a bind mount (NO_XDEV authority).
    import shlex
    script = ('set -eu; apt-get update -qq > /proof-output/container-tools.log 2>&1; '
              'apt-get install -y -qq python3 git libunwind8 >> /proof-output/container-tools.log 2>&1; '
              'mkdir -m 700 /proof-private; export TMPDIR=/proof-private; set +e; '
              + shlex.join(proof) + '; result=$?; '
              'mkdir -p /proof-output/private-originals; '
              'tar -C /proof-private --exclude="*/frozen-host" --exclude="*/frozen-app" --exclude="*/frozen-proof" -cf - . | tar -xf - -C /proof-output/private-originals; '
              f'chown -R {os.getuid()}:{os.getgid()} /proof-output; exit "$result"')
    command += [args.image, 'bash', '-c', script]
    (output / 'invocation.json').write_text(json.dumps(command, indent=2))
    with (output / 'stdout.log').open('wb') as stdout, (output / 'stderr.log').open('wb') as stderr:
        result = subprocess.run(command, stdout=stdout, stderr=stderr, timeout=1800)
    (output / 'exit.json').write_text(json.dumps(dict(exit=result.returncode)))
    if result.returncode: raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
