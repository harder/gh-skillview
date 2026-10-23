# Force install and existing local changes

`install --force` does not approve replacing an existing installation. SkillView
requires every catalog-admitted destination root to be positively missing or an
ordinary empty native directory. It checks before any repository discovery and
again after the existing final ownership checks immediately before launching gh.
No new source listing, manager or skill execution is used to infer vacancy.
After admission the effective child argv omits `--force`: a directory appearing
after the final observation receives only ordinary fresh-install semantics.
`BuildArgs` still encodes the original request; CLI JSON `force` reports that
requested intent, while `commandLine` reports effective argv for a launched child.
A pre-dispatch refusal retains the blocked requested argv and its explicit error.

An occupied root refuses with its exact path. This includes an unrelated skill,
an empty namespace child, hidden entries, final links, files and uncertain native
coverage. This conservative refusal can prevent a genuinely new selector beside
unrelated entries: current namespace/direct-path/@/all-repository selectors do
not independently prove gh's native materialization names. SkillView does not
guess a basename or conflate those entries with the requested installation.

Inspect the exact installed physical subject and source. Supported saved update,
recorded restore, local reimport or local source-switch workflows review local
changes and retain originals under their own source/owner/pin contracts. Unknown
provenance does not establish a base, source or available repair. No force
refusal creates a plan, copies payloads, modifies tracking/owners or changes
exclusions. Non-force install/list behavior and source-selector argv encoding
remain unchanged.

Vacancy uses the existing NativePlacementLease positive missing-suffix contract
and a bounded held NativeExposureDirectory namespace for existing ordinary roots.
Original lexical placement and full captured root identity/generation are checked
around the observation. An unavailable native backend refuses force rather than
using Directory.Exists=false as absence. Final native observation precedes ordinary gh install
launch; this is not a cross-process atomic transaction against another writer
creating content after that check. Destructive force authority is never delegated
from vacancy alone. No global manager lock/CAS is claimed.

Focused checks: ForceInstallDispositionTests, GhInstallOwnershipTests,
GhSkillInstallServiceTests and CliDispatcherInstallTests. The new fixtures use
private HOME/state/CWD and child environment, synthetic owner records only where
explicitly stated, and local inert shell child markers. They prove service/CLI
admission and byte preservation, not real gh/provider behavior. POSIX child
markers and unsupported native positives carry explicit platform qualifications.
These tests do not establish additional native-manager lifecycle support.
