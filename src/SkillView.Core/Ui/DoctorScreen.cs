using System.Text;
using SkillView.Diagnostics;
using SkillView.Gh;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Full-screen Doctor view shown inside the TUI (bound to `d`). Renders
/// the same `EnvironmentReport` as the CLI `doctor` subcommand in a
/// Markdown view that fills the terminal so the main view doesn't bleed
/// through. Esc/q returns to the main view.
public static class DoctorScreen
{
    public static void Show(IApplication app, EnvironmentReport report)
    {
        var body = Render(report);

        using var window = new Window
        {
            Title = "Doctor",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var text = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Text = body,
        };
        TuiHelpers.ConfigureMarkdownPane(text, "Base");

        var statusBar = new StatusBar(
        [
            new Shortcut { Key = Key.Esc, Title = "Esc", HelpText = "Back" },
            new Shortcut { Title = "q", HelpText = "Quit" },
        ]);

        TuiHelpers.ApplyScheme("Base", window, text, statusBar);

        window.Add(text, statusBar);
        window.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc || key.AsRune.Value == 'q' || key.AsRune.Value == 'Q')
            {
                app.RequestStop();
                key.Handled = true;
            }
        };

        app.Run(window);
    }

    public static string Render(EnvironmentReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Environment");
        sb.AppendLine();
        var ghVerOk = r.GhMeetsMinimum ? "✅" : "❌ too old";
        sb.AppendLine($"- **gh**         `{r.GhPath ?? "(not found)"}`");
        sb.AppendLine($"- **version**    `{r.GhVersionRaw ?? "(unknown)"}` (minimum `{GhBinaryLocator.MinimumVersion}` {ghVerOk})");
        sb.AppendLine($"- **baseline**   {(r.BaselineOk ? "✅ ok" : "⚠️ degraded")}");
        sb.AppendLine();

        sb.AppendLine("## Auth");
        sb.AppendLine();
        if (!r.Auth.LoggedIn)
        {
            sb.AppendLine("❌ Not logged in — run `gh auth login`");
        }
        else
        {
            sb.AppendLine($"- **active**     {r.Auth.Account ?? "?"}@{r.Auth.ActiveHost ?? "?"}");
            if (r.Auth.Hosts.Length > 1)
            {
                sb.AppendLine($"- **other hosts** {string.Join(", ", r.Auth.Hosts)}");
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
            // Grouped by subcommand and limited to flags that exist in the
            // current gh release. Entries below correspond 1:1 to the probed
            // tokens in CapabilityProbeParser.ProbedTokens — keep them in sync.
            // Capabilities for flags that aren't in gh yet (--repo-path,
            // --yes/--json on update, list subcommand) are intentionally
            // omitted; they would always be ❌ and read as defects rather
            // than as "not yet shipped upstream".
            sb.AppendLine("**install**");
            sb.AppendLine($"- `--allow-hidden-dirs`  {Mark(c.SupportsAllowHiddenDirs)}");
            sb.AppendLine($"- `--upstream`           {Mark(c.SupportsUpstream)}");
            sb.AppendLine($"- `--from-local`         {Mark(c.SupportsFromLocal)}");
            sb.AppendLine();
            sb.AppendLine("**update**");
            sb.AppendLine($"- `--dry-run`            {Mark(c.SupportsUpdateDryRun)}");
            sb.AppendLine($"- `--all`                {Mark(c.SupportsUpdateAll)}");
            sb.AppendLine($"- `--force`              {Mark(c.SupportsUpdateForce)}");
            sb.AppendLine($"- `--unpin`              {Mark(c.SupportsUpdateUnpin)}");
            sb.AppendLine();
            sb.AppendLine("**search**");
            sb.AppendLine($"- `--json`               {Mark(c.SupportsSearchJson)}");
            sb.AppendLine($"- `--owner`              {Mark(c.SupportsSearchOwner)}");
            sb.AppendLine($"- `--limit`              {Mark(c.SupportsSearchLimit)}");
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
            foreach (var (agent, path) in agents)
            {
                sb.AppendLine($"- **{agent}**  `{path}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Logs");
        sb.AppendLine();
        sb.AppendLine($"`{r.LogDirectory ?? "(unset)"}`");

        return sb.ToString();
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
