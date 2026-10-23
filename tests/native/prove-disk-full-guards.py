"""Read-only proof-host admission refusal; never asks the host to fill anything."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--proof-host', required=True, type=Path)
parser.add_argument('--fixture-parent', required=True, type=Path,
                    help='Ordinary filesystem or deliberately oversized tmpfs whose admission must be refused.')
parser.add_argument('--reason', required=True, help='Required refusal text, e.g. bounded tmpfs or Linux.')
args = parser.parse_args()
host = args.proof_host.resolve()
with tempfile.TemporaryDirectory(prefix='skillview-local-merge-proof-', dir=args.fixture_parent.resolve()) as temporary:
    root = Path(temporary).resolve()
    (root / 'home').mkdir()
    (root / 'state').mkdir()
    (root / 'tmp').mkdir()
    marker = root / 'disposable-proof-marker'
    marker.write_bytes(b'SkillView disposable process proof\n')
    before = sorted((str(path.relative_to(root)), path.stat().st_mode,
                     path.read_bytes() if path.is_file() else None) for path in root.rglob('*'))
    env = os.environ.copy()
    env.update(HOME=str(root / 'home'), USERPROFILE=str(root / 'home'), SKILLVIEW_STATE_HOME=str(root / 'state'),
               TMPDIR=str(root / 'tmp'), DOTNET_EnableDiagnostics='0')
    process = subprocess.run([str(host), '--check-disk-full-root', str(root)], cwd=root, env=env,
                             capture_output=True, text=True, timeout=30)
    assert process.returncode == 2 and args.reason in process.stderr, (process.returncode, process.stdout, process.stderr)
    after = sorted((str(path.relative_to(root)), path.stat().st_mode,
                    path.read_bytes() if path.is_file() else None) for path in root.rglob('*'))
    assert before == after, 'Read-only admission changed fixture data'
    assert not (root / 'disk-full-proof-filler').exists()
    print(process.stderr.strip())
    print('Read-only refusal verified; no filler created.')
