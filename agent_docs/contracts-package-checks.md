# Package checks

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.

- Static agent compatibility uses only captured root metadata and package-specific
  source/version citations in `PackageCompatibilityChecker`. Gemini 0.46.0 header
  rejection is a limited rule; passing it never proves whole-agent compatibility.
  Do not read agent configuration or simulate permissions, trust or invocation.

- TUI K / Check package is explicit and checks exactly the physical copy in
  Installed's displayed snapshot. Keep only the last successful native-bound
  report in app memory. Bind to that snapshot reference, catalog generation,
  captured child context and the final PayloadSnapshot root observation; unknown
  identity or metadata-only fallback stays diagnostic-only with Refresh guidance.
  Display-cache reuse age is not fact validity while S remains displayed: time
  alone does not block K. Results belong only to that displayed inventory until
  reload/replacement. Installed/Issues activation uses GetAsync's existing
  one-minute reuse window; an aged S is replaced with S2 on tab load, invalidating
  even a just-completed check on S. Explain this lifetime in product copy. Do not
  broaden reuse or rebind facts to S2 without fresh evidence. Publication checks owned modal
  completion, close/cancellation/request epoch, app lifetime and Installed's same
  snapshot. Do not recapture inventory, probe identity again or check on rendering.
- Only ordinal category `reference`, code `reference-missing`, document `SKILL.md`
  is a package Problem. Other findings and review-state observations are Reviews;
  declaration Info/unknown meanings and incomplete/omitted coverage stay separate.
  New, Check again and retained dialogs start at the summary top after text/layout
  publication. Reuse this classification directly over reports, including unbound
  diagnostics; do not construct a bound fact for presentation. Keep the full
  bounded evidence and display-limit notices below the summary.
  Issues adds at most one UI-only Problem row for the exact checked copy; Enter
  opens the retained report with no repair action. Metadata never adds a duplicate
  Malformed Problem. Keep queue evidence/schema and check schema unchanged.
- Compose package evidence separately from InstalledIssueEvidence. Package
  Problems/Reviews contribute Has captured findings; any such finding or partial/
  omitted package evidence excludes No local findings observed for that copy.
  A clean package report cannot establish queue absence. Other copies remain
  package not checked for this inventory capture and keep their own captured-scan
  filter semantics.
- Installed More actions may offer one resource replacement review only for one
  distinct ordinal raw `references/<safe ASCII segments>` root Problem in its
  current bound fact. Duplicate targets deduplicate; zero/multiple targets do
  not offer this action. Complete payload, raw fingerprint, valid captured root
  metadata, zero omitted findings, unpinned tracked source and complete inventory
  are required. Global reference/declaration incompleteness remains visible and
  does not disqualify this selected path. Facts never grant mutation authority.
- Freeze the exact copy/source/context/native intent before navigation. One
  owned Updates activation may bind it once to its own schema-2 saved ID,
  fingerprint and revision choice. Fresh existing inventory/native admission,
  Installed raw fingerprint equality and the full comparison's exact absent
  Installed/regular Candidate row are required. A candidate without that resource
  remains an ordinary saved review with Skip. No intent/approval is persisted or
  reconstructed from stored plans. Other preparation/copy/seed, Skip, cancellation
  and deactivation discard intent. Both independent existing approvals remain.
- Publish successful native apply/reconciliation and RememberUndo first. Then
  schedule package verification as a separate application-tracked follow-up,
  independent of the review-operation token; Undo stays available while the
  result says “Verifying package…”. It checks the applied OriginalPath off thread
  with the checker's finally verified capture. Complete raw equality to
  Candidate, regular target presence and cleared selected finding permit only
  “This resource was restored; other checks remain incomplete.” Keep residual
  Reviews and coverage. Failure/timeout/mismatch says “Replacement installed;
  package verification incomplete.” Checker failure never changes native results
  or Undo eligibility. The separate transient note binds applied ID/fingerprint,
  attaches after RememberUndo, and clears at ClearUndo/RememberUndo, entering Undo,
  new operations, Skip, cancel and deactivation. A verification epoch and owned
  cancellation reject late/queued notes even after Undo inspection is canceled
  for the same plan. During pending verification, deactivation closes approvals
  but preserves the already published idle native result/Undo until the existing
  next-load boundary. Other cancellation behavior stays unchanged. Old root
  identity is not required after native replacement. Saved schema/hash/parser/native/gh policy is unchanged.
