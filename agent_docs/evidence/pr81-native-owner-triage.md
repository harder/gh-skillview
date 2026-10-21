# PR #81 Windows x64 owner timeout

The [failed owner job](https://github.com/harder/gh-skillview/actions/runs/39631949475/job/119335200483)
tested PR #81 head `d3199ab1f92cdfc8551bf8842c5a63bda3e4cdc1`, subsequently
merged as `33207ae12dedd2929dca3a610d3fdebe68968311`.

The failure was `subprocess.TimeoutExpired` in `prove_guard()` in
`prove-shared-revision-update.py`: the managed maintenance proof host exceeded
the 20-second admission-guard limit. The actual shared-update process-death
matrix had not started. Earlier update-outcome, recorded-restoration and revision
proof steps passed. The Windows ARM64 owner job passed the corresponding matrix.

The old guard did not log its case (positive control, linked HOME or linked
state), elapsed time or captured child output on timeout. The uploaded x64
artifact contains earlier successful proof results but no shared-owner result or
guard output. This establishes a harness timeout, not its underlying cause;
neither a transient runner delay nor a product deadlock has been proved.
The roughly 22-second step duration does not identify the failed case: earlier
guards can complete in fractions of a second before a later 20-second timeout.

The follow-up gives each guard a 60-second subprocess deadline, including managed
startup and inventory. Case/start/completion timing and bounded captured timeout
output improve recurrence diagnostics. There is no retry, timeout-as-refusal acceptance,
assertion relaxation or product/native-authority change. Portable regression
tests cover preserved results and failing timeout diagnostics, including empty,
text and byte output. CI runs these tests alongside existing proof contracts.

The PR #82 [Windows x64 owner job](https://github.com/harder/gh-skillview/actions/runs/39639145310/job/119363635481)
and [Windows ARM64 owner job](https://github.com/harder/gh-skillview/actions/runs/39639145310/job/119363635385)
at `221e38a` passed. Their guards completed within a few seconds (positive controls
up to 0.4 seconds on x64 and 3.2 seconds on ARM64), establishing non-recurrence only. They do not
show that extending the deadline resolved the original failure or that 60 seconds
would suffice if it recurs. Linux-only helper tests cannot establish Windows
native behavior.

Initial local validation on the proof-only change: 6 shared-revision helper tests, 9 old
native-contract tests, 8 native-proof-input tests and 8 CI change-scope tests
passed (31 total). Python AST parsing passed for both changed Python files, and
`git diff --check` passed. Hosted workflow lint, including actionlint, passed on
`221e38a`.

## Review follow-up

The proof-only host now emits stderr phase markers at entry and before/after
inventory capture. A recurrence can distinguish missing startup output from an
incomplete inventory capture; it still cannot locate a stall within either phase.
These markers do not satisfy the separate `maintenance-proof:` refusal assertion.
Captured diagnostic tails are capped after escaping, including control-heavy
output. CI discovers all current evidence `test_*.py` files, including the
previously omitted archive/checksum preparation tests.

The existing `subprocess.run` cleanup can wait for inherited pipes held by a
descendant on Windows; the subprocess deadline is not a strict bound on that
drain. The observed failure drained promptly. Process-tree termination changes
remain deferred absent a reproduction; this follow-up does not claim to solve
that latent behavior. The two small AST test loaders remain local rather than
adding shared test infrastructure for this patch.

Review follow-up validation: all 18 evidence tests, 8 native-input tests and
8 CI-scope tests passed (34 total). The Release maintenance proof host built
without warnings/errors; scoped format and diff checks passed. All three actual
isolated Linux guard cases passed with that new host: positive-control stderr
included startup and both inventory markers, while linked HOME/state refused
before inventory and left the external sentinel unchanged. This is Linux
admission/diagnostic evidence, not a Windows timeout reproduction or a rerun of
the remote shared-update process-death matrix.
