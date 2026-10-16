using System.Text;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

/// Static helper for rendering the Doctor report as Markdown. The
/// interactive view is now <see cref="Tabs.DoctorTabView"/>; the modal
/// Show() subloop was retired in Phase 8b. Render() stays on this type so
/// the 3 DoctorScreenTests pass unchanged.
public static class DoctorScreen
{
    public static string Render(EnvironmentReport r, InventorySnapshot? inventory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Environment");
        sb.AppendLine();
        var ghVerOk = r.GhMeetsMinimum ? "✅" : "❌ too old";
        sb.AppendLine("| Item | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| gh | {MarkdownTableFormatter.FormatCodeSpan(r.GhPath ?? "(not found)")} |");
        sb.AppendLine(
            $"| version | {MarkdownTableFormatter.FormatCodeSpan(r.GhVersionRaw ?? "(unknown)")} " +
            $"(minimum {MarkdownTableFormatter.FormatCodeSpan(GhBinaryLocator.MinimumVersion.ToString())} {ghVerOk}) |");
        sb.AppendLine($"| baseline | {MarkdownTableFormatter.FormatTableCell(r.BaselineOk ? "✅ ok" : "⚠️ degraded")} |");
        sb.AppendLine();

        sb.AppendLine("## Auth");
        sb.AppendLine();
        if (!r.Auth.LoggedIn)
        {
            sb.AppendLine("❌ Not logged in — run `gh auth login`");
        }
        else
        {
            sb.AppendLine("| Item | Value |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine($"| active | {MarkdownTableFormatter.FormatTableCell($"{r.Auth.Account ?? "?"}@{r.Auth.ActiveHost ?? "?"}")} |");
            if (r.Auth.Hosts.Length > 1)
            {
                sb.AppendLine($"| other hosts | {MarkdownTableFormatter.FormatTableCell(string.Join(", ", r.Auth.Hosts))} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## `gh skill`");
        sb.AppendLine();
        sb.AppendLine(r.GhSkillAvailable
            ? "✅ `gh skill` present. SkillView requires gh ≥ 2.97.0, so the full " +
              "`skill` surface (`list`, `install --all`, `update --all`, nested-dir " +
              "discovery) is available."
            : "❌ `gh skill` subcommand not detected on this gh install.");
        sb.AppendLine();

        sb.AppendLine("## Detected agents");
        sb.AppendLine();
        var agents = DetectInstalledAgents();
        if (agents.Count == 0)
        {
            sb.AppendLine("_(no agent home directories detected under `~/`)_");
        }
        else
        {
            sb.AppendLine("| Agent | Path |");
            sb.AppendLine("| --- | --- |");
            foreach (var (agent, path) in agents)
            {
                sb.AppendLine(
                    $"| {MarkdownTableFormatter.FormatTableCell(agent)} | " +
                    $"{MarkdownTableFormatter.FormatCodeSpan(path)} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Logs");
        sb.AppendLine();
        sb.AppendLine("| Item | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| directory | {MarkdownTableFormatter.FormatCodeSpan(r.LogDirectory ?? "(unset)")} |");

        if (inventory is not null) sb.Append(RenderManagedInventory(inventory));
        else sb.AppendLine("\nManaged skill diagnostics: open Installed, or run `skillview doctor --inventory`.");
        return sb.ToString();
    }

    internal static string RenderManagedInventory(InventorySnapshot inventory)
    {
        var rows = inventory.Skills.Where(skill => skill.AllOwnershipClaims.Any(owner => owner.InstallationMode == "managed-read-only")).ToArray();
        var sb = new StringBuilder("\n## Managed resources\n\n");
        sb.AppendLine($"{rows.Length} managed skill installation(s) observed; capture {inventory.CapturedAt:O}.");
        sb.AppendLine(inventory.IsPartial ? "Coverage is partial; inspect inventory diagnostics." : "Configured inventory coverage is complete.");
        sb.AppendLine("Read-only resources; enabled and active session state are unknown.");
        sb.AppendLine("For contextual loading rules, select a skill in Installed and press E, or run `skillview effective --agent <id> --project <directory> --json`.");
        sb.AppendLine();
        sb.AppendLine("| Provider | Distributor version | Path |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var skill in rows.Take(32))
        {
            var owner = skill.AllOwnershipClaims.First(owner => owner.InstallationMode == "managed-read-only");
            sb.AppendLine($"| {MarkdownTableFormatter.FormatTableCell(owner.Provider)} | {MarkdownTableFormatter.FormatTableCell(owner.PackageVersion ?? "unknown")} | {MarkdownTableFormatter.FormatCodeSpan(skill.ResolvedPath)} |");
        }
        if (rows.Length > 32) sb.AppendLine("Additional managed rows are available in Installed or `list --json`.");
        foreach (var location in inventory.Diagnostics.Locations.Where(location => location.State == "unsupported").Take(16))
            sb.AppendLine($"- {MarkdownTableFormatter.FormatCodeSpan(location.Path)}: {MarkdownTableFormatter.FormatTableCell(location.Reason)}");
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? "";
    }

    /// Mirrors the heuristic in `InstallScreen.DetectInstalledAgents` so the
    /// Doctor view shows what the install dialog will pre-check by default.
    private static List<(string Agent, string Path)> DetectInstalledAgents()
    {
        var found = new List<(string Agent, string Path)>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return found;
            return InstallAgentCatalog.DetectInstalledDisplayEntries(
                home,
                Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"), Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
        }
        catch { /* best-effort */ }
        return found;
    }
}
