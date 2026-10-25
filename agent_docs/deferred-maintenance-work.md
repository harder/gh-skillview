# Project scope and deferred maintenance work

SkillView helps users find skills, understand physical installations and affected
agents, diagnose installation/version/duplicate issues, and apply understandable
repairs. Keep everyday CLI/TUI workflows practical and terminal UX clear.

The former all-21 completion goal is retired. Dated plans, acceptance counts,
handoff instructions and private branch contents do not authorize implementation.
Preserve useful completed features and their safety/recovery contracts. Finish a
bounded change when its user benefit and verification are clear and proportionate.

## Existing product workflows

Search/preview/install, inventory, Doctor, Changes, cleanup, comparison and
one-time Git-tag selection use the existing shared workflow coordinators.
Saved updates retain source review, explicit retention, local-change review,
inspection, recovery and undo. Basic pinning, standalone unpin, local reimport
and local source switching remain supported within their documented bounds.
Inventory retains physical locations, ownership and conditional reload guidance;
enabled and active-session state remain unknown. Package checks remain explicit
opt-in workflows, not automatic installation requirements.

## Deferred expansion

| Candidate | Boundary |
| --- | --- |
| Persistent selected-revision approvals and approved restore | PR #73 is retired without merging. Keep the existing one-time tag flow; broader policy needs a concrete user need and recovery model. |
| Exposure detach and automatic consolidation | Compare and target exact installations today. New topology mutations need explicit survivor/consumer review and native recovery proof. |
| Machine profiles and first-install profile management | Observed profile paths are inventory context, not permission to build profile lifecycle management. |
| Additional native-owner adapters or remote-source adoption | Retain existing owner diagnostics and safe refusals until one concrete repair justifies a supported transition. |
| Claude local plugin disable | Historical fixtures did not establish a complete native disable proof. Reconsider only with current source/layout evidence and an actual user need. |
| General transaction, migration or lint framework expansion | Reuse current recovery and package-check foundations. Broaden only for a bounded user-facing requirement. |

Retired local work was archived and verified before obsolete checkouts and
branches were removed. The canonical PR #73 branch is preserved in that private
archive; it is not a live implementation queue. Historical locators and the
original scope inventory remain in [Git history](https://github.com/harder/gh-skillview/blob/9825cede66f34348b2325cfe0179e17e6c85b99c/agent_docs/deferred-maintenance-work.md).

## Keeping scope current

Record the user benefit, approved boundary and verification when reconsidering
a deferred candidate. Keep implementation invariants in the focused contracts
linked from [AGENTS.md](../AGENTS.md), and user workflows in [docs/usage.md](../docs/usage.md).
No retired acceptance ledger is a current release gate.
