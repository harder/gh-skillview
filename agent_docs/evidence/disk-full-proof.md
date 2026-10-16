# Real disk-full proof

This proof fills one exclusively created file on a disposable Linux tmpfs of at
most 128 MiB. It never mounts or resizes a filesystem, never fills an ordinary
filesystem, and never injects a disk-full exception. Both the marked fixture and
its state must already be isolated by the existing proof-host guards. Held-root
`fstatfs` checks and the filesystem-size bound precede creation of the filler.
Unknown architectures, non-Linux hosts, ordinary filesystems and larger tmpfs
mounts are refused before any filler is created.

The canonical fixture must be a direct child of `/proof-fixtures`, a fresh private
tmpfs mount containing only that fixture. The held mount/fixture must share the
same native device, have current-user ownership and mode 0700, and match one
private mountinfo entry with no visible aliases or shared propagation. These
checks run again immediately before filling. A small shared tmpfs such as
`/dev/shm` is refused too. The proof assumes the isolated container's mount
namespace is not concurrently modified by another privileged process.

At one selected merged directory-move checkpoint, native `write` must return
`ENOSPC` and the held tmpfs must report zero available bytes. The callback then
returns normally. Production reconciliation/journal publication must fail with
`needs-recovery` and observed mutation. After the host exits, the parent removes
only the exact device/inode of its own filler. Fresh product CLI processes inspect,
recover and undo the plan, checking original native identity, bytes and modes,
registry bytes, the source baseline, and immutable local-review/resolution archives.
The caller supplies `--resolved` to include the frozen custom-resolution proof.

Scope: this proves capacity exhaustion during completion journaling after each
of the four directory moves. It does not prove quota exhaustion, inode exhaustion,
device I/O failure, loss of power, or shared-owner registry publication under ENOSPC.

Build the proof host and product for Linux first, then run in a disposable SDK
container. The reusable outer wrapper stages owned tools, records their hashes,
and creates the required mounts:

```sh
python3 agent_docs/evidence/run-local-merge-disk-full-docker.py \
  --app /owned/linux/skillview --proof-host-dir /owned/linux/proof-host \
  --gh /owned/linux/gh --output /owned/results/disk-full
```

It passes the existing authentication environment to the child without printing
or saving it. It refuses nonempty result directories and removes its own uniquely
named container on exit. The equivalent manual container setup binds only an
owned artifact directory at `/proof-tools` (read-only)
and an owned result directory at `/proof-results`; pass authentication through
the container environment without printing it. A representative invocation is:

```sh
docker run --rm --cap-add SYS_ADMIN \
  --tmpfs /proof-fixtures:rw,size=64m,mode=700 --tmpfs /proof-too-large:rw,size=256m,mode=700 \
  -e GH_TOKEN -e DOTNET_EnableDiagnostics=0 \
  -v "$owned_tools:/proof-tools:ro" -v "$owned_results:/proof-results" \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -euc '
    sh /proof-tools/prove-local-merge-disk-full.sh /proof-tools/skillview \
      /proof-tools/SkillView.MaintenanceProof /proof-tools/gh /proof-results
  '
```

The SDK image must contain Python, Git, and the selected `gh` host. Full privileged
Linux native-metadata visibility is independently required; a metadata refusal is
not a successful disk-full proof. Keep runtime, authentication and tool caches on
the container filesystem, outside the bounded fixture tmpfs. The read-only guard
script can also run on macOS with `--reason Linux`; it never requests a fill.
The Linux helper first proves ordinary, oversized and shared-small-filesystem
refusal. Child process temporary files stay inside the marked fixture and .NET
diagnostic sockets are disabled so the dedicated mount has no unrelated users.
