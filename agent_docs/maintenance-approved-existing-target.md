# Existing-target desired revisions and immutable approvals

This private integration adds public consumers for the reviewed M17 stores and
M19 frozen source review. It is not full M17 acceptance. Portable sets/profiles,
missing targets/parents, new exposure links and new shared-owner entries remain
separate work. Actual merged-base continuity and admitted native proofs remain
required; local metadata-authority skips never count as positive publication.

All actions select one physical `--path` or `--install-id`; global `--scan-root`,
workspace and profile options go before the command. Inventory, configuration,
Git children and native policy checks share one frozen runner context per action.
The gh locator uses the same runner's PATH/extension-host values and resolves
relative PATH components against the captured CWD, without ambient fallback from
an isolated context. No AI client is launched, and running-session state remains unknown.

1. `approval-inspect --path PATH --json` observes actual installed I through held
   native handles. Its payload digest is independent of tracking/version labels.
2. `approval-capture --path PATH --expect-payload DIGEST --yes --json` retains
   actual installed/custom bytes and supported metadata in the independent
   bounded approval store. It does not set desired intent or approve source.
   `--selected-plan-file FILE` instead requires a verified completed selected
   result at this exact installation, never tracking alone or a ready candidate.
3. `approval-inspect --path PATH --id ID --expect APPROVAL_DIGEST --json` verifies
   the exact local archive, source/owner/installation/context and captured bytes.
4. `policy-inspect --path PATH --json` supplies the independent policy fingerprint.
   `policy-set --path PATH --mode approved-immutable --approval-id ID
   --approval-expect APPROVAL_DIGEST --expect POLICY_DIGEST --yes --json` holds
   the verified completed archive to publish desired intent. Capture consent and
   policy consent cannot substitute for each other. Actual pin metadata stays
   independent. Existing policy-set/clear modes remain available.
5. `plan --purpose restore-approved --path PATH --approval-id ID
   --approval-expect APPROVAL_DIGEST --json` stages a real schema8 candidate for
   this existing target. Save its JSON response as a local plan reference.
   Inspect frozen source changes, then `approved-ack-source --id PLAN_ID
   --expect PLAN_DIGEST --review SOURCE_REVIEW_DIGEST --yes`. This declaration
   supplies no authenticity, isolation or behavioral-safety verdict.
6. `apply --plan-file FILE --yes --expect PLAN_DIGEST
   --disposition restore-approved-retain-original --json` uses the same saved
   native lifecycle. `inspect`, `verify`, `repair` and `undo` accept the reference;
   repair/undo require a separately supplied current inspection fingerprint.
   Repair cannot start a never-started ready plan through the generic route.

`plan --purpose selected-revision --path PATH --revision-kind tag|commit
--revision VALUE [--pin] --json` exposes the typed schema7 resolver/stager.
Commit values must be complete 40-character IDs; tag movement during capture
refuses. No remote source is revisited by saved apply. Source acknowledgement is
`selected-ack-source`, with disposition `selected-revision-retain-original`.
Availability/latest updates remain separate from immutable selection.

Installed `A` opens the exact selected row's approval dialog. Preview, private
capture and desired intent have separate controls and consents. Editing the ID,
approval fingerprint or hidden-directory option clears previous review/consents.
After staging, the saved lifecycle dialog requires independent frozen-source
and exact-original retention approval. Every async action stays owned through
queued UI completion; closing waits for cancellation to quiesce. Post-attempt
reconciliation has its own deadline and survives canceled UI delivery. Existing
approvals can be inspected without recapturing, and saved IDs can be reopened
for recovery/undo.

Every schema7/8 producer freezes the real M19 installed/candidate archives and
source report before final hashing and desired-policy receipt publication.
Readers reject an absent source contract for these schemas, regardless of the
store constructor's legacy opt-in flag. Both first Apply and first forward
Recover require the exact source receipt independently of desired-policy and
retention admission. Started recovery keeps copied authority; undo does not
invent a source acknowledgement. The retained header's historical contract
label is only a routing declaration, never a substitute receipt.

Schemas1–6 preserve absent-field/hash compatibility, while every current M19
source-changing product factory retains its existing SourceReview behavior.
Schema4 pin-only operations remain separate. Approved bytes are observed I,
not pristine B/C, publisher provenance or proof that a running agent adopted them.
