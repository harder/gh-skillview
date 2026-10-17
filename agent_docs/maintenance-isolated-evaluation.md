# Explicit offline evaluation boundary

`evaluate-review` performs native read-only payload/config capture only.
`evaluate-run` must validate explicit consent plus the exact review fingerprint
before any runtime subprocess or private staging. Routine inventory, static
checks, policy analysis and saved update workflows must never call this runner.

The schema-1 offline Docker policy binds exact config bytes, completed payload
bytes/modes/native generations and request bytes. Bump the policy version for
isolation/protocol changes. Always append the fixed request pathname after
explicit argv; empty configured arguments must not revive image CMD. Use explicit
absolute Docker executable and local unix socket, cleared child environment and
private Docker config. Never inherit runtime context, credentials or mounts.

Copy bytes only through completed `PayloadSnapshot.CaptureWithEntrySink` evidence
and rehash the held second read. Publish temporary input with native directory
handles, keep writer/ownership leases, and clean through held ownership. This is
not maintenance restore authority and does not restore ACL/xattr/owner metadata.

Docker create/inspect/start and independent finally-removal are separate steps.
PID isolation uses Docker's empty/default PidMode: omit `--pid` and reject any
nonempty inspected PidMode. `private` is valid for IPC/cgroup namespaces but is
not a Docker PID-mode token; keep the primary-contract regression separate from
fake response fixtures. Disable image healthchecks with `--no-healthcheck` and
require inspected `Healthcheck.Test` exactly `["NONE"]`; an image health command
must not introduce execution beyond the reviewed evaluator argv.
A canceled create may have reached the daemon; cleanup is required after admission
regardless of client response. Killing Docker attach is not container termination.
Keep explicit uncertainty if cleanup fails. Verify local image ID/digest/Linux,
no image VOLUME, daemon limits/seccomp, and actual created-container settings
before start. Do not silently pull/build/download or invoke host clients/APIs.

Tests with an injected process boundary prove review/consent, argv/environment,
refusals and cleanup flow. They do not prove container isolation or model quality.
Local development for this change did not authorize actual container/AI execution.
A future real-runtime fixture requires explicit authorization and its own evidence.
See [user contract](../docs/isolated-evaluations.md).
