# CI scope and native proof inputs

`ci.yml` skips managed/native verification only after the scope job successfully
classifies every changed path as user documentation, screenshots/animations or a
known static-site file. The allowlist lives in
`.github/scripts/ci-change-scope.js`; all `agent_docs/`, workflow, dependency,
source, test and unknown paths require full verification. Exact base/head commits
and NUL-delimited raw Git records preserve unusual filenames, both rename paths
and file modes. Missing commits, failed/bounded/malformed captures and empty
diffs require full verification. Workflow lint, its helper tests and site checks
retain their existing coverage.

Scope classification uses only the event checkout and its two-level history;
it never fetches event-supplied revisions separately. A PR merge checkout includes
both comparison parents. Pushes with older unavailable base commits simply run
full verification.

Each RID's `aot-build` job publishes the current standalone app and extension
sequentially, builds the complete maintenance proof host, and prepares both
checksum-verified gh versions. Only frozen historical producer binaries and
their source/recipe receipts enter the reusable cache. Cache identity includes
RID, exact source commits, SDK, lockfiles, publish recipe, helper hash and runner
image. Every cache hit rechecks tracked-clean source, receipt identity and binary
hash, then executes the producer's version command. Corrupt entries fail; they
cannot authorize using stale producers. Current binaries, proof results and
mutable fixtures are never cached.

The build transfers inputs to three fresh proof jobs per RID: `owners`,
`sources`, and `inspection-unpin`. The tar manifest binds the current commit,
RID, exact member set and hashes; extraction rejects links and unsafe paths and
preserves Unix executable modes. The full managed host output travels with its
runtime/dependency files. Historical source checkouts are recreated and checked
before their verified binaries are staged; Git worktree metadata is never
transferred. Preserve every original proof invocation, host pairing, version,
kill boundary and platform condition when moving steps between shards.

The proof matrix keeps `max-parallel: 4`. Evidence is retained per shard and
combined under the original per-RID artifact names. A completion receipt is
written/uploaded only after a whole shard succeeds. The existing per-RID AOT
check names now aggregate successful native builds, successful proof jobs and
all three exact completion receipts. Missing, skipped, failed or canceled proof
work cannot pass that gate. Receipts require the same run, commit, RID and shard;
an earlier successful attempt within that run is accepted for rerun-failed-jobs,
while future/invalid attempts are refused. Current job results remain required
even when earlier receipts exist.

Focused offline checks:

```sh
node --test .github/scripts/ci-change-scope.test.js
python3 -m unittest discover -s .github/scripts -p test_native_proof_inputs.py
```
