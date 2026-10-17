# Maintenance queue (first M20 slice)

`MaintenanceQueueBuilder` projects reconciled inventory and the existing cleanup
classifier into read-only issue + physical-installation rows. Use the frozen
`OperationSubject`, installation address ID and affected paths throughout. The
queue never authorizes mutation or establishes current object identity. Empty
directory issues retain the classifier's captured subject. Unknown update and
active-session state must remain explicit.

Changes routes the selected cleanup issue through a fresh classification and
filters by issue, exact path and installation ID. Updates drill-in resolves the
selected ID + path against fresh inventory, clears all/batch choices, and shows
only that installation. Preserve that restriction on refresh. Missing/ambiguous
selection yields no target; never fall back to a name, sibling or row index.
The existing dry-run, saved-plan review, apply and reconciliation contracts remain
responsible for checks, mutation authority and verification.

The presentation retains lightweight rows and renders only the selected detail.
It caps visible rows at 8,192, selected detail at 32 Ki characters, and shared
coverage detail at 8 Ki characters. Omission counts are explicit; exact fields
are omitted whole rather than shortened into routing selectors. Full schema JSON
remains available through a fresh queue capture. Projection and field rendering
check cancellation while enumerating. Coverage text is retained once per view.

Selected cleanup hand-offs use the Changes activation/request lease, not only
the app lifetime. Leaving or refreshing Changes cancels pending selection work;
repeated Enter supersedes a pending capture. The queued callback rechecks the
request before admission. Once dispatch starts, ownership remains exclusive
until the awaited modal/UI completion returns, so repeated activation cannot
admit another modal. Create the request inside admitted owned work to avoid
leaking a lease when shutdown rejects task admission.

`queue [--install-id ID] [--allow-hidden-dirs] [--json]` refreshes local/catalog
inventory without update checks, skill evaluation, saved-plan preparation or
agent startup. JSON schema 1 retains full frozen subjects, exact affected paths,
coverage diagnostics and explicit policy/status distinctions. Exit codes are
0 for captured observations, 1 for partial inventory, 2 for invalid arguments,
and 20 for a complete capture with no matching observations. Entries are
observations rather than confirmed updates. Re-run `queue` or use Changes `r`
to refresh disk inventory; checking availability is a separate explicit action.

This does not complete M20. Deferred clauses include a complete unified
inspect/check/plan/apply/repair/undo CLI plan-file contract, integrated selected
row comparisons, retained availability results in the queue, and provider/version
verified session reload guidance. Existing exact-path compare and saved-plan CLI
commands remain available. No provider session or loaded-version fact is inferred
from disk inventory, owner manifests, or a read-only client list handler.
