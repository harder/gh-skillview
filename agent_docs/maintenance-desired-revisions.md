# Desired revision policy and saved admission

Desired intent lives in the bounded private sibling `desired-state` store. It is
separate from SKILL.md, cleanup suppression, observed ownership and active-session
state. `policy-inspect` is offline and read-only. `policy-set` / `policy-clear`
require one exact physical installation, the separately supplied inspection
fingerprint and `--yes`. Clearing intent never removes a real pin.

Only positive native missing-child evidence authorizes an absent/default policy.
The thin platform presence helpers reuse existing held-relative opens and full
native identity/generation comparisons; access errors, malformed records, linked
state roots and stale source/owner bindings refuse. Existing incomplete private
directories are unknown. Do not turn `Directory.Exists == false` into default
allow. Configured path canonicalization is an address hint, never absence proof.
Held policy sessions reopen the original configured address and compare its full
native directory identity/generation with the held store, bracketing the check.
They repeat that binding check before live payload/owner transitions and adapter
launch; an ancestor alias cannot substitute another policy store at those checks.

The append-only record limit reserves a later clear for every active installation.
Latest records close earlier records by installation ID or physical address. A
clear carries history and a new fingerprint; it cannot revive an earlier saved
default approval. Inventory reads one frozen bounded record set per capture.

Lock order is desired-policy writer, replacement-root writer, operation writer,
gh registry lease, then native payload authority. Preparation writes a retained
`desired-policy.json` receipt bound to the immutable plan fingerprint. First apply
and never-started forward recovery reacquire policy before replacement writers,
recheck the receipt and retain the policy writer through execution. Read-only
preflight opens release their writers before policy acquisition. A missing receipt
is compatible only with a historical ready plan that still has no policy/history.
Original schema 1–5 plan JSON and hash domains remain unchanged. Merged plans use
the already validated source-plan fingerprint in the inherited receipt.

Recovery classification uses native topology as well as journal steps: an exact
retained original can prove a legacy transition started before its first durable
journal entry. Started recovery, owner repair and undo use the frozen native and
owner evidence even when later policy is unavailable. They cannot turn an unknown
or foreign topology into recovery authority.

Routine checks and updates refuse explicit pins, forks, approved immutable intent
and native-manager intent; `--force` supplies no policy override. Explicit selected
unpin remains the schema-4 native transaction. A fork can enter explicit same-source
local reimport review. The installer retains its original destination pin and owner
observations across discovery and holds the policy writer through its final child.
Any admitted root containing a recorded policy or actual pin refuses real install
before source discovery, including namespace/direct-path/@, URL and upstream
selectors. A verified selector-to-native placement contract is not available in
this base; sibling installs can therefore be conservatively refused. The final
root policy check repeats under the writer. Missing install roots are
omitted from pin scanning only after the native presence helper proves absence.
Validate destination rows before filtering actual pins: malformed pin metadata
can produce diagnostics with `Pinned=false`, which never proves an absent pin.
Real update admission also rejects captured invalid/conflicted/incomplete-owner
subjects and fresh tracking diagnostics even when repository/ref/tree/pin values
still match. Read-only list observations and adapter dry-run stay independent.
The legacy mutable update adapter also requires one exact captured selector and
directory and holds its original application-context policy writer through the
child; incidental child HOME isolation cannot select a different policy store.

The first policy boundary does not implement immutable approval capture, selected
revision/pin preparation, set/profile export or reprovision. `approved-immutable`
cannot be published from a mode string alone. Chosen gh branch intent has no
verified execution contract and refuses rather than inventing update flags. The
original M17 acceptance remains open until captured-byte and topology replay proofs
are implemented and run. Adjacent source-review and authoring APIs are not present
in the actual PR69 base and require final-base integration.

Focused evidence includes native unknown/absence/access tests, stale source and
policy-history refusals, an actual writer-contention checkpoint during apply,
preserved native undo after policy corruption, and offline versioned CLI JSON for
same-name/different-origin exact selection. Test fixtures use private HOME, state,
CWD and Git redirection and never invoke AI clients or payload scripts.
