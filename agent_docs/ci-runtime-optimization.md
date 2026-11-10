# CI scope and native proof inputs

`ci.yml` skips managed/native verification only after the scope job successfully
classifies every changed path as user/agent documentation, screenshots/animations or a
known static-site file. The allowlist lives in
`.github/scripts/ci-change-scope.js`. Root `AGENTS.md` and regular `.md` files
under `docs/` and `agent_docs/` qualify. Executable modes, links, nested
`AGENTS.md`/`SKILL.md`, hidden paths, proof inputs, workflows, dependencies,
source, tests and unknown paths require full verification. Active native proof
code lives in `tests/native/`; every change there retains full verification. Exact base/head commits
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
RID, exact source commits, each frozen SDK/global/locks, publish recipe, helper hash and runner
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

The proof matrix uses `max-parallel: 1` to pace live API work. Retained CI budget
observations showed concurrent owner/source proofs exhausting the available
installation budget, leaving later jobs with no requests at startup. Serialization
is also enforced across PR and main runs by the fixed `skillview-native-proof-api`
job concurrency group, with `queue: max` and `cancel-in-progress: false`. Pending
proofs queue instead of replacing each other (GitHub bounds this queue at 100).
This does not reserve quota or exclude other workflows/consumers. Preserve reset
evidence and use bounded, diagnosed retries when needed.
The pinned actionlint predates `queue`. Its narrowly anchored CI-file compatibility
exception is paired with offline positive/negative validation of the exact group,
`queue: max`, non-cancellation policy and absence of any extra queue keys. Remove
the exception when the pinned linter understands GitHub's field.
All twelve RID/shard jobs and their proof invocations remain required.
Evidence is retained per shard and
combined under the original per-RID artifact names. A completion receipt is
written/uploaded only after a whole shard succeeds. The existing per-RID AOT
check names now aggregate successful native builds, successful proof jobs and
all three exact completion receipts. Missing, skipped, failed or canceled proof
work cannot pass that gate. Receipts require the same run, commit, RID and shard;
an earlier successful attempt within that run is accepted for rerun-failed-jobs,
while future/invalid attempts are refused. Current job results remain required
even when earlier receipts exist.

Sources jobs prepare the same checksum-verified registry gh version before any
proof, and Linux pulls the exact registry execution image before spending API
requests on fixtures. Prerequisite failures remain failures; there is no alternate
tool/image or success fallback. `.github/scripts/proof_ci.py` retains an always-run,
bounded summary of failed step phases and existing HTTP/budget evidence. Explicit
rate-limit responses, generic HTTP refusals and prerequisite failures stay distinct.
A failed native step with no causal evidence remains unclassified; exhausted budget
alone does not prove why that step failed. This summary cannot create completion
receipts, turn a failure green, or replace the aggregate gate.

Focused offline checks:

```sh
node --test .github/scripts/ci-change-scope.test.js
python3 -m unittest discover -s .github/scripts -p test_native_proof_inputs.py
```


The current native-input helper uses schema 3 (`native-producers-v3`): the current
SDK is selected by the current checkout, while each frozen source must resolve its
own `global.json` from that source CWD. CI installs SDKs 10.0.402 and 10.0.113 beside
current 10.0.403. The pinned setup-dotnet action supports the multiline versions
and `global-json-file` together. Planning and publication both verify actual
versions. Source receipts bind actual frozen SDK, independently re-read source
SDK/global/locks, source commit, recipe, RID, tracked cleanliness and binary hash;
transferred inputs are checked against exact freshly verified frozen checkouts,
not only the artifact's own receipt. Old schema-1/2 receipts are refused.

The key conservatively still includes current SDK/locks as extra invalidators;
this can cause additional historical rebuilds, never reuse of a mismatched binary.
Only binary/source receipts are cached, never proof results. All families, three
shards, both current product hosts, four RIDs and supported historical schemas
remain required. Current/release audit and warning behavior stays strict. On
2026-11-06 the owner explicitly approved the narrow historical-fixture exception
recorded in `.github/scripts/frozen-audit-exception.json`. Only compiler10.0.13,
the exact high-severity GHSA-p3jp-9v83-76w9 URL and the five listed source commits,
SDKs and seven input hashes qualify. Frozen tracked source/global/six locks and the
base publish tuple remain unchanged; the additional generated-props recipe is
explicitly versioned and bound. These old CI-only producers remain vulnerable and
must never be shipped as current/release artifacts.

Before constructing or accepting any cached producer, the helper performs a fresh
UNSUPPRESSED graph-faithful locked App restore in the exact source CWD/SDK. It
requires the complete original-policy Core error/App warning for only the known
compiler13/high/advisory tuple. Different or missing diagnostics, other packages,
URLs/severities, feed failures, NU1004, partial output or any unexpected warning
fail closed. No TreatWarningsAsErrors override, audit-mode/level change, NoWarn or
blanket NU1903 tolerance is used. Fresh results live outside the reusable baseline
cache. Their complete bounded streams and source/SDK/disposition/recipe identities
are transferred for this run and rechecked against independent frozen inputs;
proof success is never cached. An earlier same-run artifact remains identifiable
by its run/commit, not an assertion of a new audit at the consumer.

Only after that admission, one exact-byte/hash-checked regular props file outside
tracked source supplies a single advisory-URL `NuGetAuditSuppress` through
`CustomBeforeMicrosoftCommonProps` to the historical publish. The URL item itself
is advisory-wide: exact source/SDK/lock guards and the default audit are what
restrict its package scope. Recheck props immediately before use. Current/release
source and additional properties/recipes are refused. Any NuGet diagnostic token
in either publish stream still fails, as does a nonzero exit. Compiler/linker
warnings retain the original frozen recipe's policy, including its strict
IL2026/IL3050/IL3053 flags; this exception adds no blanket warning rule. Cache/transfer schema3 binds disposition and props hashes; older,
missing, extra or mixed receipts cannot inherit this exception. All other
advisories still block. Download-only runtime-pack audit coverage is UNKNOWN;
absence of output is not proof of coverage. Revisit this explicit exception when
the family/schema is retired or the advisory ceases to apply; never expand it
automatically.


The always-run native-build-receipts artifact retains the exact named per-family
audit stdout/stderr, publish stdout/stderr, command/exit/audit JSON and STOP JSON
leaves, including hidden paths. This preserves refused/default-audit and failed
publish evidence even when packaging native proof inputs is never reached. No
recursive HOME, configuration, package/cache, bin/obj or private payload path is
included by this receipt selection. Retention, permissions and failure semantics
are unchanged.
