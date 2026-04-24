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
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| gh path | `{r.GhPath ?? "(not found)"}` |");
        sb.AppendLine($"| gh version | `{r.GhVersionRaw ?? "(unknown)"}` |");
        sb.AppendLine($"| gh minimum | `{GhBinaryLocator.MinimumVersion}` {(r.GhMeetsMinimum ? "✅" : "❌ TOO OLD")} |");
        sb.AppendLine($"| baseline | {(r.BaselineOk ? "✅ ok" : "⚠️ degraded")} |");
        sb.AppendLine();

        sb.AppendLine("## Auth");
        sb.AppendLine();
        if (!r.Auth.LoggedIn)
        {
            sb.AppendLine("❌ Not logged in — run `gh auth login`");
        }
        else
        {
            sb.AppendLine($"**Active:** {r.Auth.Account ?? "?"}@{r.Auth.ActiveHost ?? "?"}");
            if (r.Auth.Hosts.Length > 1)
            {
                sb.AppendLine($"**Other hosts:** {string.Join(", ", r.Auth.Hosts)}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Capabilities (`gh skill`)");
        sb.AppendLine();
        var c = r.Capabilities;
        if (!c.SkillSubcommandPresent)
        {
            sb.AppendLine("❌ `gh skill` subcommand not detected on this gh install.");
        }
        else
        {
            sb.AppendLine("| Capability | Status |");
            sb.AppendLine("|------------|--------|");
            sb.AppendLine($"| `list --json` | {Mark(c.HasSkillList)} |");
            sb.AppendLine($"| `install --allow-hidden-dirs` | {Mark(c.SupportsAllowHiddenDirs)} |");
            sb.AppendLine($"| `install --upstream` | {Mark(c.SupportsUpstream)} |");
            sb.AppendLine($"| `install --repo-path` | {Mark(c.SupportsRepoPath)} |");
            sb.AppendLine($"| `install --from-local` | {Mark(c.SupportsFromLocal)} |");
            sb.AppendLine($"| `update --dry-run` | {Mark(c.SupportsUpdateDryRun)} |");
            sb.AppendLine($"| `update --all` | {Mark(c.SupportsUpdateAll)} |");
            sb.AppendLine($"| `update --force` | {Mark(c.SupportsUpdateForce)} |");
            sb.AppendLine($"| `update --unpin` | {Mark(c.SupportsUpdateUnpin)} |");
            sb.AppendLine($"| `update --yes` | {Mark(c.SupportsUpdateYes)} |");
            sb.AppendLine($"| `update --json` | {Mark(c.SupportsUpdateJson)} |");
            sb.AppendLine($"| `search --json` | {Mark(c.SupportsSearchJson)} |");
            sb.AppendLine($"| `search --owner` | {Mark(c.SupportsSearchOwner)} |");
            sb.AppendLine($"| `search --limit` | {Mark(c.SupportsSearchLimit)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Logs");
        sb.AppendLine();
        sb.AppendLine($"**Directory:** `{r.LogDirectory ?? "(unset)"}`");

        return sb.ToString();
    }

    private static string Mark(bool on) => on ? "✅" : "❌";
}
