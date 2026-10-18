# Selected immutable revision preparation

The selected-revision workflow is separate from routine latest updates.
Its schema 7/purpose `selected-revision` reserves schema 6 for the separately
reviewed source-switch workflow. `Selection` is omitted entirely from schemas
1–5 and historical hashes. Public CLI/UI consumers and durable approval captures
are described in [the existing-target guide](maintenance-approved-existing-target.md).
Selected preparation alone is not complete desired-state or reprovision support.

`SelectedRevisionRequest` identifies a tag or a full commit, plus explicit pin
intent. Resolve only that kind, retain the immutable commit/subtree tree, and
check tag movement again before freezing. Never guess a commit's symbolic ref
or fall back to latest/default/branch. A saved apply/recovery never resolves the
tag again. The provider executable can disappear after successful capture.

`GhPinnedRevisionStager` uses the existing process runner, native archives and
verified binary witness helpers. Its private child HOME/state/CWD/Git/plugin
context retains captured auth/proxy resolution without persisting credentials
or mutating the parent environment. Trusted flags precede `--`; a pin value is
one `--pin=<ref>` argument. Both raw immutable source and actual gh output are
retained. Recompute Git blob/tree identities from source bytes and captured Git
modes, including on Windows where native Unix modes are absent. Actual gh
tracking is observed separately; do not manufacture `refs/heads/<commit>`.

Capture the full runner environment and absolute CWD once at preparation entry.
API resolution, raw source, actual provider output, baseline and owner/policy
work derive from that same runner. Frozen runners do not inherit absent keys
when launching children. Private Git overrides use the operating system's key
comparison, so mixed-case Windows names cannot replace the intended private
values. Persist only the bounded context digest; credentials remain in memory.
First selected apply must still match that preparation context.

Actual provider output must preserve every authored SKILL field and body, as
well as its captured mode. Validate original YAML before removing recognized
installer metadata, then use exact authored bytes or the existing conservative
same-source presentation comparison. Changed authored values, ambiguous scalar
coercion and invalid tracking headers refuse. Repeat this check from retained
archives when reopening a saved selected plan.

Provider capture is observation, not publication authority. Unknown native
metadata remains unknown in the archive. Private materialization, saved-plan
completion and every live candidate verification require the existing complete
metadata authority. Incomplete/unknown previews retain the sole bounded working
reservation and refuse a retry; successful completed evidence is copied through
held native archives into the immutable operation before owned preview cleanup.

The desired-policy writer precedes preview/replacement/operation authorities;
registry leases do not span provider calls. Borrow the same policy writer into
preparation rather than reacquiring it. Exact-pin policy admits only the matching
explicitly pinned selection. Other protected intents refuse. Selection cannot
implicitly clear an installed pin; standalone unpin remains its own operation.
An unpinned selected result removes only the verified installer pin member from
the private provider output. It does not edit the installed original in place.

Shared owners use the existing extension-preserving editor and the same held
DATA FILE object. Read-only owner preflight binds the actual installed tree
without a fabricated resolved commit; final preparation binds the actual frozen
selected tree and reviewed pin. The new purpose requires
`selected-revision-retain-original`; ordinary update commands cannot apply it.
Native originals, journal/recovery/undo and policy receipts retain their earlier
authority. Selected plans do not reuse ordinary content-only no-op checks because
their supported metadata may differ even when bytes match.
Schema7 was introduced with policy receipts, so a missing receipt refuses first
apply and first recovery even when current desired policy is absent. Historical
receipt-less legacy read/recovery contracts remain unchanged; already-started
native recovery and undo retain their existing journal authority.

Injected protocol fixtures exercise exact requests/argv, actual output capture,
malformed output and moved tags, before-child refusal and charged failures.
They are not real-gh runtime proof. Positive metadata-preserving apply/undo
fixtures explicitly skip after verifying unchanged originals when the existing
Linux trusted-namespace witness refuses; run those positives on an admitted
privileged Linux/macOS/Windows host before claiming native completion. Never
weaken the metadata witness to make an ordinary host pass. Source-review
acknowledgement remains an independent final actual-base integration gate.
