# Bounded static package checks (partial M19)

`check --path PATH` and `check --install-id ID` select exactly one local inventory
installation. Paths may identify the physical payload or a captured agent exposure.
The CLI requests an offline inventory with no gh host. The Installed-tab `K`
action uses the workflow coordinator to recapture the selected installation with
the same workspace/profile roots, then opens an owned asynchronous read-only
dialog. Filter typing retains printable `K`.

JSON schema 1 separates `formatStatus`, `referenceStatus`, and payload/reference
`coverage`; typed findings retain source paths, line numbers, targets and repair
guidance. `referenceScope` names the limited Markdown/code-path grammar. Complete
payload coverage with no errors in those requested checks exits 0; findings or
unknown/partial coverage exit 1. Invalid options exit 2, unavailable environment
errors exit 10, and a selector that does not match exactly one installation exits
20. Human output is terminal-sanitized; JSON escapes the original evidence.

No skill scripts, agent clients, remote fetches, or automated fixes are part of
this workflow. It does not establish authenticity, agent compatibility, behavioral
safety or source review. Those report dimensions remain `not-assessed`; execution
is `none`. M19 and the original 21-item acceptance audit remain incomplete. This
feature is a partial implementation, with original acceptance still 9/21.

The dialog uses `ModalOperationTracker` ownership through queued completion.
Closing cancels and drains its worker before disposing controls; parent lifetime
cancellation suppresses a queued successful result. All UI mutations are dispatched
through the application while the dialog is active.
