using SkillView.Bootstrapping;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    internal static string RenderHelpMarkdown(AppOptions options)
    {
        var command = GetCommandName(options.InvocationMode);
        var alternateCommand = options.InvocationMode == InvocationMode.GhExtension ? "skillview" : "gh skillview";

        return $$"""
            # SkillView

            SkillView is a terminal UI and scriptable CLI for discovering, previewing, installing, updating, removing, and cleaning up AI agent skills on top of `gh skill`.

            SkillView complements `gh skill`: use SkillView for guided TUI flows and automation-friendly CLI wrappers, and reach for raw `gh skill` when you need a brand-new upstream preview flag before SkillView exposes it.

            You are running the `{{command}}` entrypoint. The alternate entrypoint is `{{alternateCommand}}`.

            ## Usage

            ```text
            {{command}} [global options]
            {{command}} <subcommand> [subcommand options]
            ```

            With no subcommand, SkillView launches the full-screen TUI.

            ## Quick start

            ```bash
            {{command}}
            {{command}} doctor
            {{command}} search terraform
            {{command}} list --json
            {{command}} update --dry-run
            {{command}} cleanup
            ```

            ## Global options

            | Global flag | What it does |
            | --- | --- |
            | `--help`, `-h` | Show this Markdown help view. |
            | `--version`, `-V` | Show the SkillView version. |
            | `--debug` | Enable debug logging. This flag works before or after the subcommand and streams logs to stderr in CLI mode. |
            | `--theme <name>` | Select the TUI theme. Supported values are `default` and `high-contrast`. You can also set `SKILLVIEW_THEME`. |
            | `--scan-root <path>` | Add a custom scan root. Repeat this flag to scan multiple roots. |
            | `--workspace <path>` | Observe known project skill directories in another workspace. Repeat for a collection. |
            | `--profile <agent[:name]=skills-directory>` | Observe an explicit agent profile without guessing enabled state. |

            ## Subcommands

            | Subcommand | Purpose | Key options |
            | --- | --- | --- |
            | `doctor` | Inspect `gh`, auth state, `gh skill` availability, log path, and optional inventory. | `--json`, `--inventory`, `--clear-logs` |
            | `list` | Show installed skills from the filesystem and, when supported, `gh skill list`. | `--json`, `--scope`, `--agent`, `--dir`, `--allow-hidden-dirs` |
            | `check` | Check bounded local package metadata, references and literal declarations without running skill code. | `--path` or `--install-id`, `--agent`, `--agent-version`, `--allow-hidden-dirs`, `--json` |
            | `queue` | Refresh read-only maintenance observations grouped by issue and physical installation. Update availability and running-session state remain unknown. | `--install-id`, `--allow-hidden-dirs`, `--json` (schema 1) |
            | `inspect` | Inspect one exact installation, or read an old plan reference for specialist recovery guidance. | `--path` / `--install-id`, or `--plan-file`, `--json` |
            | `rescan` | Rebuild the local inventory snapshot and print a summary. | _none_ |
            | `search <query>` | Search public skill repositories. | `--owner`, `--limit`, `--page`, `--json` |
            | `preview OWNER/REPO [SKILL]` | Render a skill preview without installing it. | `--version`, `--allow-hidden-dirs`, `--rendered`, `--json` |
            | `install OWNER/REPO [SKILL]` | Install one skill, or every skill in the repo with `--all`, then rescan inventory. | `--all`, `--agent`, `--scope`, `--path`, `--version`, `--pin`, `--force`, `--upstream`, `--from-local`, `--allow-hidden-dirs`, `--json` |
            | `update [SKILL...]` | Check availability or stage one exact saved update for review. Other selected targets remain not started until this review is applied/skipped. | `--all`, `--path`, `--install-id`, `--dry-run`, `--unpin`, `--allow-hidden-dirs`, `--json`; `--force` never approves replacement |
            | `compare` | Compare complete payloads and recorded source/revision facts for two exact installations. Performs no remote check or mutation. | `--path`, `--other-path`, `--json` |
            | `backup` | Capture bounded payload evidence outside installed skills. Does not replace a payload or grant restore authority. | `--path` or `--install-id`, `--json` |
            | `registry-restore-plan` | Review replacing one missing/corrupt ordinary copy from its attached gh registry tree; placement is nonexclusive. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `registry-restore-inspect`, `registry-restore-ack-source`, `registry-restore-apply`, `registry-restore-recover`, `registry-restore-undo` | Separate source and retention approvals; raw original retained for exact Undo. Unpinned historical ref stays unknown. | `--id`, `--expect`, `--review`, `--yes`, `--disposition retain-original`, `--json` |
            | `restore-plan` | Stage the recorded immutable Git tree and save an exact replacement plan. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `update-plan` | Stage an immutable latest revision or chosen Git tag, retaining a recorded base and local-change review. | `--path` or `--install-id`, `--tag TAG` (unpin separately), `--unpin`, `--allow-hidden-dirs`, `--json` |
            | `source-switch-plan` | Review an ownerless local installation switching to a different existing durable ordinary local source. | exactly one `--path` or `--install-id`, `--new-source <absolute directory>`, `--json` |
            | `source-switch-inspect`, `source-switch-apply`, `source-switch-recover`, `source-switch-undo` | Inspect or execute schema6; exact original retained; frozen recovery after retain. | `--id`, `--expect`, `--yes`, `--disposition replace-local-source-retain-original`, `--json` |
            | `reimport-plan` | Save a baseline-less replacement from the same recorded local source; retain the exact installed original. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `reimport-inspect`, `reimport-apply`, `reimport-recover`, `reimport-undo` | Inspect or execute a saved local reimport. Recovery after the first retain uses frozen evidence. | `--id`, `--expect`, `--yes`, `--disposition replace-local-retain-original`, `--json` |
            | `unpin-plan` | Save a pin-only change to one physical installation, preserving installed content and its recorded source revision. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `unpin-inspect`, `unpin-apply`, `unpin-recover`, `unpin-undo`, `unpin-repair-owner` | Inspect or execute a saved pin-only transaction. Apply retains the original directory. | `--id`, `--expect`, `--yes`, `--disposition unpin-only`, `--json` |
            | `update-preserve` | Verify and keep the current installation without moving payloads, writing its owner or changing preferences. | `--id`, `--expect <plan fingerprint>`, `--json` |
            | `update-ack-source`, `reimport-ack-source`, `source-switch-ack-source`, `restore-ack-source` | Declare review of an exact frozen diff, independently of retention approval. No authenticity or safety claim. | `--id --expect <plan> --review <source-review fingerprint> --yes`, `--json` |
            | `update-inspect`, `update-apply`, `update-recover`, `update-undo` | Inspect, apply, recover or undo a saved revision update. Apply must match its saved replacement or merged disposition. | `--id`, `--expect`, `--yes`, `--disposition retain-original` / `merge-reviewed`, `--json` |
            | `update-repair-owner` | Repair only a recognized torn registry from its explicitly reviewed retained image. Inspect again before resuming payload recovery. | `--id`, `--yes --expect`, `--json` |
            | `restore-inspect` | Verify saved plan locations and obtain a recovery review fingerprint. | `--id`, `--json` |
            | `restore-apply` | Apply a saved plan, retaining the original directory. | `--id`, `--yes --disposition retain-original --expect`, `--json` |
            | `restore-recover`, `restore-undo` | Resume or undo after inspecting current evidence; never overwrite another object. | `--id`, `--yes --expect`, `--json` |
            | `quarantine` | Preview relocation of one standalone custom-directory copy. Apply requires a reviewed fingerprint and explicit confirmation. | `--path` or `--install-id`, `--yes --standalone --expect`, `--json` |
            | `undo` | Preview or restore an unchanged quarantined directory to its vacant original location. | `--id`, `--yes`, `--json` |
            | `quarantine-history` | Inspect recorded relocation states; incomplete operations require inspection. | `--json` |
            | `replacement-history` | List up to 16 retained replacement operations using metadata/name hints only. Inspect to verify; no retirement or recovery action. | `--json` |
            | `history` | Verify retained backup journals and every payload blob; reports interrupted, orphaned and corrupt evidence without extracting it. | `--id`, `--json` |
            | `remove [SKILL]` | Safely remove one exact installation. Defaults to dry-run until you pass confirmation. | `--path` or `--install-id`, `--agent`, `--yes`, `--json` |
            | `cleanup` | Find duplicates, residue, malformed installs, and other cleanup candidates. | `--candidates`, `--apply`, `--yes`, `--json`, `--output` |
            | `ignore` | Set external cleanup suppression for one physical install; never changes skill files or agent loading. | `--path` or `--install-id`, `--clear`, `--json` |

            ## Common examples

            ```bash
            {{command}} doctor --json
            {{command}} list --scope user --json
            {{command}} search prompt --owner github
            {{command}} preview github/awesome-copilot documentation-writer --rendered
            {{command}} install github/awesome-copilot git-commit --agent claude-code --scope user
            {{command}} update --all --dry-run
            {{command}} remove git-commit --yes
            {{command}} cleanup --apply --yes
            ```

            ## Automation

            SkillView is automation-friendly when you want safer wrappers than raw `gh skill`.

            - Prefer `--json` on `doctor`, `list`, `search`, `preview`, `install`, `update`, `remove`, and `cleanup`.
            - Use exit codes in scripts: `0` success, `2` invalid usage, `10` environment/setup problems, `20` no matches, `130` canceled.
            - Put global flags like `--scan-root` and `--theme` before the subcommand; only `--debug` is accepted after the subcommand.

            ## Notes

            - SkillView requires `gh` 2.97.0 or newer and checks that `gh skill` is available.
            - `--debug` is the only global flag accepted after a subcommand. Put other global flags before the subcommand.
            - `SKILLVIEW_LOG=debug` is the environment-variable alternative to `--debug`.
            - On Apple Silicon macOS and x86_64 Linux, install the standalone app with `brew install harder/tap/skillview`.
            - WinGet manifests are generated for release review, but are not submitted automatically.

            ## Exit codes

            | Code | Meaning |
            | --- | --- |
            | `0` | Success or nothing to do |
            | `1` | User-level error |
            | `2` | Invalid usage |
            | `10` | Environment error |
            | `20` | No matches |
            | `130` | Canceled by the caller or Ctrl+C |
            """;
    }

    private static int PrintHelp(AppOptions options)
    {
        Console.Out.WriteLine(RenderHelpMarkdown(options));
        return ExitCodes.Success;
    }

    internal static string RenderVersionText(AppOptions options)
    {
        var version = typeof(CliDispatcher).Assembly.GetName().Version;
        var versionText = version is null
            ? "0.0.0"
            : version.Revision == 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : version.ToString();
        var terminalGuiVersion = typeof(Terminal.Gui.Views.Window).Assembly.GetName().Version?.ToString() ?? "unknown";

        return $"{GetCommandName(options.InvocationMode)} {versionText} (Terminal.Gui {terminalGuiVersion})";
    }

    private static int PrintVersion(AppOptions options)
    {
        Console.Out.WriteLine(RenderVersionText(options));
        return ExitCodes.Success;
    }

    private static string GetCommandName(InvocationMode invocationMode) =>
        invocationMode == InvocationMode.GhExtension ? "gh skillview" : "skillview";
}
