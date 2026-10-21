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

The follow-up gives each guard a bounded 60 seconds, including managed startup
and inventory. Case/start/completion timing and bounded captured timeout output
make a recurrence diagnosable. There is no retry, timeout-as-refusal acceptance,
assertion relaxation or product/native-authority change. Portable regression
tests cover preserved results and failing timeout diagnostics, including empty,
text and byte output. CI runs these tests alongside existing proof contracts.

Windows execution of this follow-up remains required to establish whether the
longer bound suffices or exposes another failure. Linux-only helper tests cannot
establish Windows native behavior. Private raw triage evidence is under
`/var/tmp/skillview-pr81-native-triage/` on the development host.

Local validation on the proof-only change: 6 shared-revision guard tests, 9 old
native-contract tests, 8 native-proof-input tests and 8 CI change-scope tests
passed (31 total). Python AST parsing passed for both changed Python files, and
`git diff --check` passed. `actionlint` was not installed, so workflow lint was
not run locally.
