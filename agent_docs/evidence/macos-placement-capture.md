# Bounded Unix placement capture stabilization

Base: PR #83 merge `1017293`. Scope is initial read-only placement capture,
not source replacement, mutation authority or retrying an approved operation.

## Failure and mechanism

On [PR #82's macOS test job](https://github.com/harder/gh-skillview/actions/runs/39639145310/job/119357857734),
`CopiedSourceRelativeTrackingRefusesExplicitlyRatherThanRelaxingSchemaFive`
received the early `Source placement changed during relative observation`
IOException instead of its expected copied-tracking diagnostic. A later PR #83
run failed `NativePlacementTests.AliasedAncestorsResolveAndBrokenOrFinalLinksCannotMasqueradeAsMissing(relative: True)`
because the alias-ancestor capture received the same early refusal instead of
resolving the existing alias and retaining the missing suffix.

Unix placement walks from the filesystem root, including macOS `/var` alias
expansion. Every parent is bracketed by full native identity/generation checks.
An unrelated sibling creation can change that parent's generation before the
capture completes; the old implementation immediately refused. Parallel fixtures
share system-temp ancestors. The old logs did not record the changed component
or concurrent actor, so they do not prove which process changed which ancestor.
Deterministic native tests reproduce that parent-change refusal mechanism.

Windows already permits four complete initial-capture attempts for this condition.
Unix now uses the same bounded approach. A private parent-change exception unwinds
every held descriptor and recursive alias lease before restarting the original
lexical address with fresh component/link budgets. Named-child or alias mismatch,
unsupported types, access errors and mount/link bounds still refuse immediately.
Cancellation is checked before admission, before parent checks and before retry.
Completed leases retain their existing fresh-address/anchor comparison: no
observation from an earlier attempt or saved approval is refreshed into acceptance.
Persistent churn still refuses after four attempts.

Seven new Unix cases cover existing/missing children after one parent change,
bounded continuous churn, cancellation, child/alias replacement without retry and
recursive alias-parent restart. Existing placement and local source tests retain
their assertions. Independent static review found no concrete safety/correctness
issue; native macOS execution is still required for platform evidence.

## Separate native CI quota failure

An earlier main-run Linux owner artifact recorded HTTP 403 with installation
core quota limit/used 5000, remaining 0. This is remote verification failure,
not evidence that native behavior passed or failed. Personal-token quota is a
different authority and cannot establish installation-token availability.
Verification must use the current reviewed head and retain actual job results;
older green jobs do not waive new-head checks.

Local validation on the reviewed fix: locked restore and the Release test build
with .NET SDK 10.0.402 succeeded with zero warnings or errors; scoped format
verification and `git diff --check` passed. `NativePlacementTests` passed 17
with four Windows-only cases skipped on Linux; `LocalSourceSwitchTests` passed
17, `LocalReimportAdmissionTests` passed 15, and
`LocalReimportCandidateValidationTests` passed 2. Total: 51 passed, 4 skipped,
0 failed. This does not substitute for the pending native CI matrix.
