# Opt-in offline evaluations

`evaluate-review` freezes a bounded regular-file skill package, your representative
prompts and an explicit offline Docker environment. It does not start Docker or
an AI client. `evaluate-run` starts that environment only with the matching review
fingerprint and `--consent-run`. Inventory, `check`, updates and
source review never start evaluations automatically.

Supply your own already-local Linux image containing a runnable offline evaluator
and, if applicable, its model and dependencies. SkillView does not supply a model,
install dependencies, build/pull images, contact APIs, or infer that an echo/test
fixture evaluates agent quality. Choose prompts and inspect actual outputs against
your own requirements. Successful execution is not a quality score, compatibility
certificate, source-review approval or safety verdict.

## Configuration and review

Create a schema-1 JSON file. All fields below are required; unknown/duplicate
properties are refused. Replace both placeholder hashes with independently
reviewed values from your local image, and choose the explicit executable/socket.
Do not put credentials in prompts, context, image configuration or arguments.

```json
{
  "schemaVersion": 1,
  "dockerExecutable": "/usr/bin/docker",
  "dockerEndpoint": "unix:///var/run/docker.sock",
  "image": "local/offline-evaluator@sha256:0000000000000000000000000000000000000000000000000000000000000000",
  "imageId": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
  "entrypoint": "/opt/evaluator/run",
  "arguments": ["--request"],
  "evaluator": "My reviewed offline evaluator and model version",
  "context": "Describe the task context and assessment expectations.",
  "prompts": [
    {"id": "typical-task", "text": "Your representative task prompt here."},
    {"id": "edge-case", "text": "Your representative edge-case prompt here."}
  ],
  "timeoutSeconds": 60,
  "memoryMiB": 2048
}
```

```sh
skillview evaluate-review --path /absolute/path/to/skill --config evaluation.json --json
# Review the entire output and package, then explicitly authorize that exact run:
skillview evaluate-run --path /absolute/path/to/skill --config evaluation.json \
  --review REVIEW_FINGERPRINT --consent-run --json
```

Review requires no runnable Docker installation. Its runtime/image fields remain
declarations. Run requires a local Linux Docker daemon with `runc`, builtin
seccomp, and memory/swap/CPU/PID limit support. It verifies the already-local
image's exact ID, repository digest and Linux OS. Image `VOLUME` declarations are
refused. Remote daemon endpoints, Windows/macOS hosts and unavailable native
staging/cleanup authority are refused. A malformed/missing environment is not a
successful evaluation. There is no weaker execution fallback.

The review fingerprint binds exact config bytes, canonical source path, completed
native payload bytes/modes and object generations, request bytes and the versioned
isolation policy. Editing a prompt, context, executable/socket, image, arguments,
limits, payload or even config whitespace requires a fresh review. Source objects
must remain unchanged between review and run. Review reports contain all prompts,
context, argv and payload hashes; treat them as potentially sensitive.

## Evaluator protocol

SkillView copies only completed, rehashed native captures into private temporary
inputs, never executing from the installed package. `/input/skill` contains the
captured regular files/directories. `/input/request.json` contains:

```json
{
  "schemaVersion": 1,
  "skillPath": "/input/skill",
  "evaluator": "the configured evaluator description",
  "context": "the exact configured context",
  "prompts": [{"id": "typical-task", "text": "the exact configured text"}]
}
```

The invoked argv is always the explicit entrypoint, configured arguments, then
`/input/request.json`. Image CMD is never used, even with empty arguments. The
chosen evaluator must understand this protocol, load the skill, perform the
representative tasks and emit its observations on stdout/stderr. No output schema
or evaluator-authored pass/score is interpreted as a SkillView verdict. Raw JSON
strings preserve untrusted output; human text removes terminal escapes. Truncated
streams are explicitly marked, and failures/cancellation remain failures.

Limits: config 1 MiB, 1–16 unique prompt IDs, 32,768 characters per prompt/context,
32 arguments of up to 4,096 characters, payload 1,024 entries/16 MiB, main-run deadline
1–300 seconds plus bounded termination/cleanup time, RAM 64–8,192 MiB with no additional swap, CPU 1, PIDs 64, 65,536
retained characters per stream. Links, special files and `.git` metadata are
refused. File bytes and executable bits are copied; ACLs, xattrs, ownership and
installer authority are not restored. Evaluation does not edit the source.

## Isolation limits and cleanup

Docker uses networking `none`, read-only root/input, UID/GID 65534, all capabilities
dropped, no new privileges, private PID/IPC/cgroup namespaces and fixed builtin
seccomp through `runc`. PID isolation uses Docker's default namespace, with no
`--pid` override and an inspected empty `PidMode`. Image healthchecks are disabled
with `--no-healthcheck`; inspected `Healthcheck.Test` must be exactly `["NONE"]`,
so image health commands cannot add auxiliary execution. Only private input is bind mounted. HOME/CWD and agent
config locations are private; `/work` and `/tmp` are bounded no-exec tmpfs mounts.
Ambient host environment, agent configuration and credentials are never forwarded;
no Git repository or API socket is mounted. The selected payload/request and trusted
image content are intentionally available to the evaluator, so review those inputs
for secrets before consenting. Docker CLI itself receives a cleared environment and
private config/home/CWD, plus only fixed locale/PATH/temp/config values. Docker
must access its explicitly selected host socket; that socket never enters the
container. Image-baked environment/content remains part of the trusted image.

Created-container settings are checked before start. Docker/its local daemon,
the explicit CLI binary, host kernel and image remain trusted prerequisites;
containers share the host kernel. This is not proof that hostile code cannot
escape Docker, nor a claim of independent runtime verification. Administrators
must protect the Docker executable/socket/image store and avoid changing daemon
policy during a run. A process with the same host UID can tamper with private
staging, and host/container administration authority can invalidate isolation. Held native input is rechecked
just before create/start; Docker ultimately mounts a pathname, so these checks
do not make the final pathname-mount/start intervals atomic against same-UID
interference. Result bindings describe the frozen requested inputs, not a
cryptographic attestation of bytes consumed by the evaluator. No actual
container/model isolation proof is provided by the fake-runner unit tests.

Cancellation/deadlines terminate the CLI and independently request forced
container removal, with a bounded cleanup deadline; killing the attach client
alone is insufficient. Private input cleanup retains native directory ownership.
Failures report the container name/endpoint or private input location requiring
attention. A crash, power loss or inaccessible daemon can leave a container or
private inputs behind. Do not treat uncertain cleanup as success.

Docker flag semantics: [container create](https://docs.docker.com/reference/cli/docker/container/create/),
[container run](https://docs.docker.com/reference/cli/docker/container/run/) and
[seccomp defaults](https://docs.docker.com/engine/security/seccomp/).
