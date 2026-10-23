# Identity and operation subjects

Represent shared user installs, pinned project copies, locally edited forks and
plugin-bundled copies without losing source, owner or consumer information. Every
update/delete resolves to physical IDs and enumerates affected exposures. Address
selectors, native observations and fresh execution validation are separate facts.

1. Distributor identity alongside upstream source identity.
   SkillPackage keeps observed provider, source locator, contextual registry,
   installation/package boundary, selector and plugin/version. Use an
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

Regression coverage:
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


`OperationSubject` and `OperationImpact` are frozen CLI/TUI projections. Keep
identity-only observations separate from mutation authority and preserve the
pre-attempt subject through refusal, cancellation and partial mutation. See
[discovery](contracts-discovery.md), [native filesystem](contracts-native-filesystem.md)
and [saved-operation](contracts-saved-operations.md) contracts.
