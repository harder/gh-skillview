# Bounded Unix placement capture mitigation

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
The [intermediate PR #83 run](https://github.com/harder/gh-skillview/actions/runs/39655757995/job/119415499404)
also failed `CanceledCaptureRetainsExactDiagnosticAndLeavesBothPayloadsUnchanged`:
fresh placement revalidation threw the same IOException before the expected
cancellation diagnostic. These three examples were checked against actual logs;
they establish recurrence, not the cause or its frequency across all runs.

Unix placement walks from the filesystem root, including macOS `/var` alias
expansion. Every parent is bracketed by full native identity/generation checks.
An unrelated sibling creation can change a directory's ctime while it is a held
parent or the named child of the preceding component. The old implementation
immediately refused either observation. Parallel fixtures
share system-temp ancestors. The old logs did not record the changed component
or concurrent actor, so they do not prove which process changed which ancestor.
Deterministic native tests reproduce those ctime-only refusal mechanisms; they
do not establish which actor/window was responsible for the CI failures.

Windows already permits four complete initial-capture attempts for this condition.
Unix now uses the same bounded approach for ctime-only directory changes. A
private generation-change exception unwinds
every held descriptor and recursive alias lease before restarting the original
lexical address with fresh component/link budgets. Device, inode and type must
match before a directory generation difference can restart capture. Both the
opened child and the named entry are checked first, so moving an opened directory
and replacing its name still refuses permanently. Named-child identity or alias
mismatch,
unsupported types, access errors and mount/link bounds still refuse immediately.
Cancellation is checked before admission, before parent checks and before retry.
Completed leases retain their existing fresh-address/anchor comparison: no
observation from an earlier attempt or saved approval is refreshed into acceptance.
Persistent churn still refuses after four attempts.

Twelve new Unix cases cover existing/missing children after one parent change,
bounded continuous churn, cancellation, child/alias replacement without retry and
recursive alias-parent restart. Both opened-child and named-entry ctime windows
have successful-restart and bounded-exhaustion cases. Existing placement and local
source tests retain
their assertions. Independent static review found no concrete safety/correctness
issue; native macOS execution is still required for platform evidence.

## Validation

Initial local validation at `c3bb0e3`: locked restore and the Release test build
with .NET SDK 10.0.402 succeeded with zero warnings or errors; scoped format
verification and `git diff --check` passed. `NativePlacementTests` passed 17
with four Windows-only cases skipped on Linux; `LocalSourceSwitchTests` passed
17, `LocalReimportAdmissionTests` passed 15, and
`LocalReimportCandidateValidationTests` passed 2. Total: 51 passed, 4 skipped,
0 failed. This does not substitute for the pending native CI matrix.

After review identified the second, named-child ctime window, five additional
Unix cases were added. Both child checks now distinguish identity from generation;
tests also require component/field diagnostics and refusal of a replacement in
the earlier opened-child window. Revised local validation passed 56 tests with
four Windows-only skips (22 placement, 17 source-switch, 15 reimport admission,
2 reimport candidate tests). The Release build had zero warnings/errors; scoped
format and diff checks passed. Independent static re-review found no concrete
safety/correctness issue. New-head macOS/native verification is still required;
neither deterministic tests nor an isolated green CI run prove that the original
CI actor has been identified or that persistent churn cannot exhaust the bound.
