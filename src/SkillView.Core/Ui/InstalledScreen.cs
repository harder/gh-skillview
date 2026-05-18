using System.Globalization;
using SkillView.Inventory.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Static helpers for the Installed view's keyboard dispatch + detail
/// rendering. The interactive view itself is now <see cref="Tabs.InstalledTabView"/>;
/// the modal Show() subloop was retired in Phase 4 of the winget-tui
/// redesign in favor of an embedded tab. The helpers below stay on this
/// type so the test surface (BuildShortcuts / DecideShortcut / RenderDetail /
/// ShortcutCommand) is unchanged.
public static class InstalledScreen
{
    internal enum ShortcutCommand { None, Close, GoToSearch, FocusFilter, FocusTable, CycleSort, Remove, Open }
    internal readonly record struct ShortcutDecision(ShortcutCommand Command, bool RequestStop);


    internal static Shortcut[] BuildShortcuts(bool canRemove, bool hasPackages)
    {
        var list = new List<Shortcut>
        {
            new() { Key = (Key)'/', Title = "/", HelpText = "Search" },
            new() { Key = (Key)'f', Title = "f", HelpText = "Filter" },
        };
        if (canRemove) list.Add(new() { Key = (Key)'x', Title = "x", HelpText = "Remove" });
        list.Add(new() { Key = (Key)'o', Title = "o", HelpText = "Open" });
        if (hasPackages) list.Add(new() { Key = (Key)'s', Title = "s", HelpText = "Sort" });
        list.Add(new() { Key = Key.Esc, Title = "Esc", HelpText = "Back" });
        list.Add(new() { Key = (Key)'q', Title = "q", HelpText = "Quit" });
        return list.ToArray();
    }

    internal static ShortcutDecision DecideShortcut(Key key, bool filterHasFocus, bool canRemove)
    {
        if (filterHasFocus)
        {
            if (key.AsRune.Value == '/')
            {
                return new ShortcutDecision(ShortcutCommand.GoToSearch, RequestStop: true);
            }

            return key.KeyCode == KeyCode.Esc
                ? new ShortcutDecision(ShortcutCommand.FocusTable, RequestStop: false)
                : default;
        }

        if (key.KeyCode == KeyCode.Esc || key.AsRune.Value == 'q' || key.AsRune.Value == 'Q')
        {
            return new ShortcutDecision(ShortcutCommand.Close, RequestStop: true);
        }

        var r = key.AsRune.Value;
        if (r == '/')
        {
            return new ShortcutDecision(ShortcutCommand.GoToSearch, RequestStop: true);
        }
        if (r == 'f' || r == 'F')
        {
            return new ShortcutDecision(ShortcutCommand.FocusFilter, RequestStop: false);
        }
        if (r == 's' || r == 'S')
        {
            return new ShortcutDecision(ShortcutCommand.CycleSort, RequestStop: false);
        }
        if (canRemove && (r == 'x' || r == 'X'))
        {
            return new ShortcutDecision(ShortcutCommand.Remove, RequestStop: false);
        }
        if (r == 'o' || r == 'O')
        {
            return new ShortcutDecision(ShortcutCommand.Open, RequestStop: false);
        }

        return default;
    }

    private static string DisplayScope(Scope s) => s switch
    {
        Scope.User => "Global",
        _ => s.ToString(),
    };

    internal static string RenderDetail(InstalledSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {s.Name}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Path | {MarkdownTableFormatter.FormatCodeSpan(s.ResolvedPath)} |");
        sb.AppendLine($"| Scope | {MarkdownTableFormatter.FormatTableCell(DisplayScope(s.Scope))} |");
        sb.AppendLine($"| Provenance | {MarkdownTableFormatter.FormatTableCell(s.Provenance.ToString())} |");
        sb.AppendLine($"| Validity | {MarkdownTableFormatter.FormatTableCell(s.Validity == ValidityState.Valid ? "✅ Valid" : $"⚠️ {s.Validity}")} |");
        sb.AppendLine($"| Symlinked | {MarkdownTableFormatter.FormatTableCell(FormatBool(s.IsSymlinked))} |");
        sb.AppendLine($"| Pinned | {MarkdownTableFormatter.FormatTableCell(FormatBool(s.Pinned))} |");
        sb.AppendLine($"| Ignored | {MarkdownTableFormatter.FormatTableCell(FormatBool(s.Ignored))} |");
        sb.AppendLine($"| Tree SHA | {MarkdownTableFormatter.FormatCodeSpan(s.TreeSha ?? "(unset)")} |");
        sb.AppendLine($"| Version | {MarkdownTableFormatter.FormatTableCell(s.FrontMatter.Version ?? "(unset)")} |");
        if (s.FrontMatter.Upstream is { Length: > 0 } upstream)
        {
            sb.AppendLine($"| Upstream | {FormatTableLink(upstream)} |");
        }
        if (s.InstalledAt is { } when_)
        {
            sb.AppendLine($"| Installed | {MarkdownTableFormatter.FormatTableCell(when_.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} |");
        }
        if (s.Package is { } pkg)
        {
            sb.AppendLine();
            sb.AppendLine("## Package");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine($"| Source | {MarkdownTableFormatter.FormatCodeSpan(pkg.Source)} |");
            sb.AppendLine($"| Type | {MarkdownTableFormatter.FormatTableCell(pkg.SourceType)} |");
            if (pkg.SourceUrl is { Length: > 0 } url)
            {
                sb.AppendLine($"| Package URL | {FormatTableLink(url)} |");
            }
            if (pkg.UpdatedAt is { } u)
            {
                sb.AppendLine($"| Updated | {MarkdownTableFormatter.FormatTableCell(u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} |");
            }
        }
        if (s.FrontMatter.Description is { Length: > 0 } desc)
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(desc);
        }
        if (s.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Agents");
            sb.AppendLine();
            sb.AppendLine("| Agent | Link | Path |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var a in s.Agents)
            {
                var kind = a.IsSymlink ? "symlink" : "direct";
                sb.AppendLine($"| {TuiHelpers.AgentIcon(a.AgentId)} **{MarkdownTableFormatter.FormatTableCell(a.AgentId)}** | {MarkdownTableFormatter.FormatTableCell(kind)} | {MarkdownTableFormatter.FormatCodeSpan(a.Path)} |");
            }
        }
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";

    private static string FormatTableLink(string value)
    {
        var normalized = MarkdownTableFormatter.NormalizeTableValue(value);
        return $"[{MarkdownTableFormatter.FormatTableCell(normalized)}]({EscapeMarkdownLinkDestination(normalized)})";
    }

    private static string EscapeMarkdownLinkDestination(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);

    /// Tiny `EnumerableTableSource<InstalledSkill>` shim — exists only so we
    /// can keep a stable named type for the `currentSource` field reassignment
    /// pattern (avoids capturing inferred-anonymous generic locals).
    private sealed class InstalledTableSource : EnumerableTableSource<InstalledSkill>
    {
        public InstalledTableSource(IReadOnlyList<InstalledSkill> rows, Dictionary<string, Func<InstalledSkill, object>> cols)
            : base(rows, cols) { }
    }
}
