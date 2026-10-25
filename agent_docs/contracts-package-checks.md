# Package checks and authoring validation contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Authoring validation is explicit `authoring-validate --repository PATH --dry-run`,
  separate from installed `check` and updates. gh dry-run may perform advisory
  API reads and cache writes; never infer safety/source review or comprehensive
  discovery from exit zero. Preserve authored bytes and report changes, never
  invoke `--fix`. See `agent_docs/maintenance-authoring-validation.md`.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.
