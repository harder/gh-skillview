#!/bin/sh
# Inside an isolated SDK container with fresh private tmpfs mounts. No mounting
# or build occurs here; admission remains enforced independently by the host.
set -eu
test "$#" -eq 4 || { echo 'usage: prove-local-merge-disk-full.sh app proof-host gh output-directory' >&2; exit 2; }
test "$(uname -s)" = Linux || { echo 'Linux container required; no fill requested.' >&2; exit 2; }
proof_app=$1
proof_host=$2
proof_gh=$3
proof_output=$4
proof_scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export DOTNET_EnableDiagnostics=0
mkdir -p "$proof_output"
python3 "$proof_scripts/prove-disk-full-guards.py" --proof-host "$proof_host" \
  --fixture-parent /tmp --reason 'bounded tmpfs' > "$proof_output/guard-ordinary.log" 2>&1
python3 "$proof_scripts/prove-disk-full-guards.py" --proof-host "$proof_host" \
  --fixture-parent /proof-too-large --reason 'bounded tmpfs' > "$proof_output/guard-oversized.log" 2>&1
python3 "$proof_scripts/prove-disk-full-guards.py" --proof-host "$proof_host" \
  --fixture-parent /dev/shm --reason 'dedicated /proof-fixtures' > "$proof_output/guard-shared-small.log" 2>&1
for proof_boundary in retain-moved install-moved displace-moved restore-moved; do
  printf 'stage=real-enospc-%s\n' "$proof_boundary"
  TMPDIR=/proof-fixtures python3 "$proof_scripts/prove-local-merge-update.py" \
    --app "$proof_app" --gh "$proof_gh" --resolved --proof-host "$proof_host" \
    --disk-full-boundary "$proof_boundary" --output "$proof_output/disk-full-$proof_boundary.json" \
    > "$proof_output/disk-full-$proof_boundary.log" 2>&1
done
