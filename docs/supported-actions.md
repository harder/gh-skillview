# Supported actions and their boundaries

This matrix describes the development checkout, which may be ahead of an
installed release. Use that binary's `--help` for its command surface. A visible
button or captured finding is an invitation to review; fresh admission can still
refuse it. All actions retain their own cancellation, identity and context checks.

## Browse, inspect and choose

| Selection / surface | Action | Result and limits |
|---|---|---|
| Discover result | Preview; `i` compact install; `I` advanced install | Preview is read-only. Install reviews source, selected skills, placement, scope/agents or custom path. A separate confirmation starts gh; post-attempt inventory can remain incomplete. |
| Installed source heading | Enter / Left / Right | Expand or collapse observed physical copies. A heading cannot run copy operations. |
| Installed exact visible copy | Enter; F2; Coverage | Inspect metadata, every recorded consumer and technical evidence. Below 110 columns detail replaces the list; Esc returns. Missing, ambiguous or hidden selections disable copy actions. |
| Installed search / selectors | `/` or `f`; Agent, Project/location, Source/owner, Issue | Filter captured observations, preserving all consumers of a matching copy. Unknown issue results and incomplete coverage remain unknown. No scan, hashing or package check runs just to filter. |
| Installed or Issues | `C` Check updates / Cancel check | Explicit catalog-wide upstream observations; no candidate is prepared. Not checked, unknown, stale, pinned and failed are distinct from current. |
| Issues typed family | Enter / Right | Expand only that finding's exact captured members. A family never chooses a survivor or an operation target. |
| Issues expanded exact child | Enter; then Reference / Compare | One Enter opens inline copy comparison at any width. Reference choices stay inside the freshly validated finding membership. Stale or missing members refuse comparison. |
| Installed exact copy → More actions | Compare copies (broad: same name or recorded source; not the finding's family) | Explicit broader read-only comparison. The chosen reference is not a preferred survivor. |
| Issues ordinary eligible exact row, including Updates / package rows | Enter | Below 110 columns first Enter shows details, second Enter opens the supported review. At wider widths Enter opens review directly. No apply follows merely from Enter. |
| Issues eligible exact item / expanded child | `u` Review | Direct exact-item review; on an expanded child, saved update review. It is not an alias for Enter and does not require the narrow details step. |
| Issues unmatched gh registry observation | Enter; F2 | Inspect exact selector, context and nonexclusive-record guidance only. It has no installation subject, guided action, restore or registry-deletion authority. Add known Locations and refresh to improve coverage. |
| Installed exact copy | `K` Package check | Bounded local metadata/reference checks, without launching an AI client. Missing resources offer targeted replacement only when the existing bound comparison establishes that exact restoration. |
| Main views | F5 Locations → Save & refresh | Saved TUI roots/workspaces/profiles with independent CLI precedence per category. CLI subcommands keep their own explicit/default scope. Saving does not authenticate or scan arbitrary projects. |
| Main views | `d` Doctor | Local diagnostics plus on-demand authentication probe. Local inventory browsing does not await remote auth. |

A comparison's completed bounded local capture does not prove complete inventory
or consumer coverage. Raw and normalized differences, native physical independence,
root modes, source and revision evidence remain separate. Unknown origin stays
unknown; equal content establishes neither authenticity nor non-use, redundancy
or safe deletion. Tracked copies cannot use standalone Move aside.

## Preferences and guided review

| Selection / state | Action | Result and limits |
|---|---|---|
| Eligible observed source/revision/method family | `k` Keep both; intentional family: `k` Restore | Private evidence-bound acknowledgement or restored suggestions. Problems, conflicts, coverage and updates remain visible. No payload or registry change. |
| Issues | `K` Choices | Review reset of saved intentional differences; Cancel is default. Separate confirmation preserves a bounded backup and resets only those preferences. |
| Issues with eligible exact updates or non-review-only cleanup | `g` Guided | Freeze up to 128 eligible visible items only with no active sequence, open/working guided item, selection or preference work. Family/registry/coverage rows grant no target. |
| Active sequence | `n` Review next | Freshly resolve one exact item. Source and retention approvals remain separate; the next sibling is not prepared until prior reconciliation and another explicit Next. |
| Active sequence | `s` Skip; `x` Stop | Skip an unstarted item; Stop cancels only the guided request. Outcomes remain visible, including stopped status. These keys are unhandled outside an active sequence. `k` never means Skip. |
| Open or working guided review | `k`, `K`, `c` | Finish or Stop the guided review first. Preference cancellation cannot settle or cancel the guided operation. There is no Apply all. |

## Exact operations, retained originals and retirement

| Exact target / evidence | Review path | Approval and recovery boundary |
|---|---|---|
| Eligible Installed copy / Issues update | `u` → saved Updates; Choose version for a tag | Prepare one immutable candidate. Inspect frozen Diff (`D`) and omissions; source acknowledgement and original retention are separate. Replace reviewed promotes the whole candidate. Preserve installed / Skip do not apply it. |
| Supported pin-only change | Separate unpin review | Explicit pin purpose and approval, not an implicit part of ordinary update. |
| Ownerless local installation with recorded source | More actions → local reimport (`R`) or local source switch (`T`) | Purpose-specific source/installed evidence, every consumer, fresh admission and retained original. No automatic local merge. |
| Damaged exact copy with an attached compatible gh record | More actions → Review registry-recorded replacement | Dedicated purpose, placement uncertainty and surviving-content corroboration. Frozen source review and retention are separate. A registry-only observation cannot use this route. See [recorded restoration](recorded-restoration.md). |
| Exact admitted removable copy / cleanup candidate | Installed `x`; Issues `c` / exact review | Fresh native preflight and confirmation; additional warnings/consumers can require extended review. Review-only, owned, partial or uncertain states remain protected. Deletion has no automatic Undo. |
| Exact untracked standalone Custom copy | More actions → Review standalone Move aside | Fresh Preview fingerprint, standalone declaration and effect approval; same-volume native rename retains original objects. Tracked, managed, pinned, shared, owned and incomplete states refuse. No registry editing or consumer retargeting. |
| Successful saved update result | Undo this update | Fresh inspection plus separate restoration approval; restores retained local edits/extras for every consumer of that physical copy. Changed payload/native/owner evidence can refuse Undo. |
| Quarantine operation | Inspect retained → separate Review Undo; CLI `undo --id` | Fresh exact operation/context/native checks and vacant destination; never overwrites. Quarantine Undo has its own contract, without an `--expect` fingerprint. |
| F4 History saved replacement | Inspect → Abandon unapplied review | Fresh retirement inspection verifies never-moved intact private evidence; separate Cancel-default approval deletes its candidate/review, not a live installation. |
| F4 History applied/restored replacement | Inspect → Discard Undo data | Separate Cancel-default approval permanently removes retained payloads and loses Undo. The live copy must exist; live files, consumers and registries stay unchanged. Active, unknown or interrupted evidence remains protected. |

After an attempted write, refresh inventory before another review. Native outcome,
inventory verification and running-agent reload are separate facts. Preserve
interrupted journals and originals; nothing resumes or evicts them automatically.
F4 lists saved replacements, not quarantine. See [saved updates](saved-updates.md),
[quarantine and undo](quarantine-and-undo.md) and [History](usage.md#retained-operation-history).

## CLI and JSON compatibility

Global discovery flags precede the subcommand. Keep HOME/state/CWD and
workspace/profile/scan-root options with saved IDs and fingerprints. Use
purpose-specific plan/inspect/source-ack/apply/recover/undo commands; generic
mutation wrappers are retired. The bounded legacy `inspect --plan-file` reader
is guidance only, not payload authority or approval.

CLI `queue --json` remains schema 1: TUI grouped families and registry-only rows
are not additional wire subjects. Existing saved schemas, fingerprints and
specialist recovery remain supported. Install post-attempt inventory reports
`added: null` with `addedKnown: false` when additions are unknown, including on a
successful child exit; `observed` is not a list of newly added entries. Check
`addedKnown` before interpreting `added: []`. See [usage](usage.md) for full
commands, migration details, limits and refusal reasons.
