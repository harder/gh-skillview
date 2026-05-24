using System.Text;
using SkillView.Diagnostics;
using SkillView.Gh;

namespace SkillView.Ui;

/// Static helper for rendering the Doctor report as Markdown. The
/// interactive view is now <see cref="Tabs.DoctorTabView"/>; the modal
/// Show() subloop was retired in Phase 8b. Render() stays on this type so
/// the 3 DoctorScreenTests pass unchanged.
public static class DoctorScreen
{
    public static string Render(EnvironmentReport r)
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

        sb.AppendLine("## `gh skill` capabilities");
        sb.AppendLine();
        var c = r.Capabilities;
        if (!c.SkillSubcommandPresent)
        {
            sb.AppendLine("❌ `gh skill` subcommand not detected on this gh install.");
        }
        else
        {
            // Capability entries must correspond 1:1 to CapabilityProbeParser.ProbedTokens.
            // Keep in sync when adding/removing flags. Only show flags in current gh release;
            // omit future flags to avoid appearing as defects.
            AppendCapabilitySection(
                sb,
                "install",
                ("`--allow-hidden-dirs`", c.SupportsAllowHiddenDirs),
                ("`--upstream`", c.SupportsUpstream),
                ("`--from-local`", c.SupportsFromLocal));
            AppendCapabilitySection(
                sb,
                "preview",
                ("`--allow-hidden-dirs`", c.SupportsPreviewAllowHiddenDirs));
            AppendCapabilitySection(
                sb,
                "update",
                ("`--dry-run`", c.SupportsUpdateDryRun),
                ("`--all`", c.SupportsUpdateAll),
                ("`--force`", c.SupportsUpdateForce),
                ("`--unpin`", c.SupportsUpdateUnpin));
            AppendCapabilitySection(
                sb,
                "search",
                ("`--json`", c.SupportsSearchJson),
                ("`--owner`", c.SupportsSearchOwner),
                ("`--limit`", c.SupportsSearchLimit));
        }
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

        return sb.ToString();
    }

    private static void AppendCapabilitySection(StringBuilder sb, string name, params (string Flag, bool Supported)[] rows)
    {
        sb.AppendLine($"### {name}");
        sb.AppendLine();
        sb.AppendLine("| Flag | Supported |");
        sb.AppendLine("| --- | --- |");
        foreach (var (flag, supported) in rows)
        {
            sb.AppendLine(
                $"| {MarkdownTableFormatter.FormatTableCell(flag)} | " +
                $"{MarkdownTableFormatter.FormatTableCell(Mark(supported))} |");
        }
        sb.AppendLine();
    }

    private static string Mark(bool on) => on ? "✅" : "❌";

    /// Mirrors the heuristic in `InstallScreen.DetectInstalledAgents` so the
    /// Doctor view shows what the install dialog will pre-check by default.
    private static List<(string Agent, string Path)> DetectInstalledAgents()
    {
        var found = new List<(string Agent, string Path)>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return found;
            return InstallAgentCatalog.DetectInstalledDisplayEntries(home);
        }
        catch { /* best-effort */ }
        return found;
    }
}
