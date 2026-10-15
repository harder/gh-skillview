# Identity and operation subjects (M11, in progress)

Original acceptance (verbatim): "Represent a shared user install, a pinned project copy,
a locally edited fork and a plugin-bundled copy of the same display name without
losing information. Every update/delete operation resolves to physical IDs and
enumerates affected exposures." Address selectors coexist with independent native
object observations and fresh execution validation.
Do not bundle native manager lifecycle commands (M09/M16), loading selection
rules (M12), migration (M13/M14) or desired state (M17).

1. Distributor identity alongside upstream source identity.
   SkillPackage keeps observed provider, source locator, contextual registry,
   installation/package boundary, selector and plugin/version. Introduce an
   explicit identity projection based on those observed facts; unknown upstream
   remains unknown. Package version is revision evidence, not display-name identity.
   Preserve provider/context distinctions and multiple owner claims; no arbitrary
   first-owner collapse. Separate logical distributor identity from a contextual
   delivery instance: v1/v2 share the intended package identity, but retain distinct
   version/location/registry/object evidence. An observed marketplace-directory
   label or manifest name is not a globally verified origin; label its evidence.
   Reuse existing guarded group resolution; change grouping only for a concrete
   collision reproduced by acceptance fixtures.

2. Preserve InstallationId as an address identifier.
   Do not redefine it: saved schema1/2 plans and selectors rely on normalized-path
   IDs. Expose its address role and optional observed native object/generation
   evidence with capture status and timestamp. Capture off the UI thread through
   existing verified native read helpers; unavailable means unknown, not failure
   or permission to mutate. Observation never becomes execution authority. Native
   mutation still freshly validates its separate full identity contract.
   Routine inventory must not hash every payload. Explicit inspection/comparison
   can populate the known payload fingerprint. Resolved commit stays nullable
   until source resolution actually observes it; do not infer a commit from a
   tree hash or authored version.

3. Shared structured operation-subject/impact projection.
   Include address ID/path, source/revision evidence, distributor identities,
   available native observation/execution evidence with role, and immutable
   memberships preserving agent/profile/scope/context/discovery path/version.
   Payload removal/replacement enumerates all known consumers. Link-only removal
   identifies every context/profile sharing that canonical link entry, explicitly
   retaining the payload and other links. Include capture/generation and affected-
   exposure coverage completeness; all known is not all possible. Preserve the
   pre-attempt subject through refusal/failure/cancellation, even after deletion
   makes inspection impossible. Update outcomes can retain legacy string exposures
   while adding structured data; remove/cleanup/retained operations get the same
   projection. CLI and TUI use the shared projection without UI-thread I/O.
   Align remove with existing update --path/--install-id selectors so same-agent
   copies can be selected exactly; reject incompatible/unknown flags rather than
   silently accepting them. Normalize agent aliases through AgentIdentity.

4. Historical serialization compatibility is mandatory.
   Prefer standalone CLI/UI projection records. Avoid changing saved source/owner/
   subject serialization with computed getters.
   JsonIgnore derived properties on persisted records; explicitly write additive
   CLI projections. Any new persisted optional field needs omission when null and
   proof that old fingerprints remain exact. Existing schema1/2 saved plans must
   inspect/recover/undo; use an actual prior native binary fixture as in PR58/59.

Focused acceptance:
- Four physical copies of one display name: shared loose user, pinned project,
  edited local fork, managed plugin. Sources/revisions/owners/exposures stay distinct.
- Same display/plugin names from different distributors retain separate identities;
  aliases retain one object observation with multiple contexts. Versions share
  the intended package identity while keeping distinct delivery/object evidence.
- Same address with a recreated native directory keeps address ID but changes
  object evidence; stale execution is refused by existing native validation.
- Update preview/result enumerates every shared context; link-only removal scope
  differs from payload removal, includes every profile/context of the one link
  entry, and excludes untouched links. Refusal, later failure and cancellation keep the
  original structured subject. Managed copy remains read-only.
- Old-plan serialization hashes and actual old-native persisted restart proof.

Review this design against current source before implementation. Make the batch
small enough to close M11's original representation/impact criterion; do not add
manager command mutation or effective-loading inference.


## Implementation and review checkpoint

The working implementation adds frozen `OperationSubject`/`OperationImpact`
projections, read-only native identity/generation capture, separate distributor
and delivery identities, exact CLI remove selectors, and structured affected
contexts in update/remove/cleanup and retained-operation output. Saved subject,
owner and journal serialization stays unchanged; transient managed-region and
result projections are explicitly ignored.

Two independent source reviews found and verified fixes for lost source conflicts,
collapsed managed contexts, cancellation and unexpected-failure subject loss,
plain-text output omissions, cleanup batch review/result omissions, live
quarantine source loss, retention-location ambiguity, and ambiguous newline
encoding of delivery roots. Independent re-review is clear. The earlier full
suite was intentionally stopped after review changes were required; its partial
log is not a passing verification result.

Final full tests, both AOT hosts, minimum/current gh disposable proofs, actual old
native persisted-plan restart, all platform gates and merge remain required.
This checkpoint does not close original M11 acceptance or increase the completed
finding count. M01/M02/M04/M05/M06/M07/M10 remain the seven completed findings.


Final-source preparation: build passes with zero warnings/errors; maintenance
385/385 and cleanup 18/18 tests pass. Inventory's earlier 344-case checkpoint
is retained as intermediate evidence; the full final-source suite is next.
The native proof now checks ambiguity diagnostics plus unchanged object/byte/mode
fixtures, and independently reads native IDs and generation values. Native AOT,
old-host restart and remote checks remain pending.

PR #61 Copilot follow-up: logical distributor digests now retain normalized
recorded source and URL evidence alongside the plugin label; a shared label
cannot combine different repositories in package removal. Frozen subjects carry
output-only materialization (`directory`, `broken-link`, or historical `unknown`),
so unlinking a broken entry cannot claim its deleted address is a retained payload.
Quarantine previews identify the original observed retention location. Windows
inventory identity reads omit DELETE access; destructive capture still requires
it. The new repository and CLI regressions failed before these fixes. A disposable
Windows ACL regression denies target DELETE and parent DELETE_CHILD while proving
read-only identity/generation capture leaves permissions and bytes intact; its
platform execution remains a CI gate. Native identity proofs now include actual
quarantine preview and broken-link cleanup output. Original M11 remains open until
final-source checks, native proofs, review and merge complete.
