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

        if (key.KeyCode == KeyCode.Esc)
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

    internal static string RenderDetail(InstalledSkill s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {s.Name}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        AppendDetailItem(sb, "Path", MarkdownTableFormatter.FormatCodeSpan(s.ResolvedPath));
        if (s.ObservedSubject is { } subject)
            OperationImpactFormatter.Append(sb, OperationImpact.Payload(subject, "inspect"));
        AppendDetailItem(sb, "Location", MarkdownTableFormatter.FormatTableCell(InstalledInventoryFormatter.DescribeLocation(s)));
        AppendDetailItem(sb, "Source", MarkdownTableFormatter.FormatTableCell(InstalledInventoryFormatter.DescribeProvenance(s)));
        AppendDetailItem(sb, "Loading", "Press **E** in Installed to analyze an agent/profile/project. Filesystem presence does not establish active-session loading.");
        AppendDetailItem(sb, "Package check", "Press **K** in Installed for bounded local metadata/reference checks. Agent compatibility and source review remain unassessed.");
        AppendDetailItem(sb, "Health", MarkdownTableFormatter.FormatTableCell(
            s.Validity == ValidityState.Valid ? "✅ Valid" : $"⚠️ {s.Validity}"));
        AppendDetailItem(sb, "Symlinked", MarkdownTableFormatter.FormatTableCell(FormatBool(s.IsSymlinked)));
        AppendDetailItem(sb, "Pinned", MarkdownTableFormatter.FormatTableCell(FormatBool(s.Pinned)));
        if (s.FrontMatter.GithubRef is { } repositoryRef) AppendDetailItem(sb, "Repository ref", MarkdownTableFormatter.FormatCodeSpan(repositoryRef));
        if (s.FrontMatter.PinnedRef is { } pinnedRef) AppendDetailItem(sb, "Pinned ref", MarkdownTableFormatter.FormatCodeSpan(pinnedRef));
        if (s.FrontMatter.GithubPath is { } sourcePath) AppendDetailItem(sb, "Source path", MarkdownTableFormatter.FormatCodeSpan(sourcePath));
        if (s.FrontMatter.LocalPath is { } localPath)
        {
            AppendDetailItem(sb, "Local source", MarkdownTableFormatter.FormatCodeSpan(localPath));
            AppendDetailItem(sb, "Local reimport", "Press **R** to review replacement from this same recorded local source, retaining the exact installed original. All consumers are affected; recovery uses frozen evidence after the first retain.");
        }
        AppendDetailItem(sb, "Ignored", MarkdownTableFormatter.FormatTableCell(FormatBool(s.Ignored)));
        if (s.CleanupSuppression is { } suppression)
            AppendDetailItem(sb, "Cleanup suppression", MarkdownTableFormatter.FormatTableCell(
                $"{suppression.State}; legacy={suppression.LegacyMarker}, external={suppression.External}. {suppression.Diagnostic}"));
        if (!string.IsNullOrWhiteSpace(s.TreeSha))
        {
            AppendDetailItem(sb, "Tree SHA", MarkdownTableFormatter.FormatCodeSpan(s.TreeSha));
        }
        if (!string.IsNullOrWhiteSpace(s.FrontMatter.Version))
        {
            AppendDetailItem(sb, "Version", MarkdownTableFormatter.FormatTableCell(s.FrontMatter.Version));
        }
        if (s.FrontMatter.Upstream is { Length: > 0 } upstream)
        {
            AppendDetailItem(sb, "Upstream", FormatTableLink(upstream));
        }
        if (s.InstalledAt is { } when_)
        {
            AppendDetailItem(sb, "Installed", MarkdownTableFormatter.FormatTableCell(
                when_.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }
        if (s.Package is { } pkg)
        {
            sb.AppendLine();
            sb.AppendLine("## Package");
            sb.AppendLine();
            AppendDetailItem(sb, "Package source", MarkdownTableFormatter.FormatCodeSpan(pkg.Source));
            AppendDetailItem(sb, "Package type", MarkdownTableFormatter.FormatTableCell(pkg.SourceType));
            if (pkg.InstallationMode == "managed-read-only")
            {
                AppendDetailItem(sb, "Managed by", MarkdownTableFormatter.FormatTableCell(pkg.Provider));
                if (pkg.PluginName is { } plugin) AppendDetailItem(sb, "Plugin", MarkdownTableFormatter.FormatTableCell(plugin));
                if (pkg.PackageVersion is { } version) AppendDetailItem(sb, "Distributor version", MarkdownTableFormatter.FormatTableCell(version));
                AppendDetailItem(sb, "Maintenance", "Read-only; use the owning agent or package manager");
                AppendDetailItem(sb, "Loading", "Enabled and active session state unknown");
            }
            if (pkg.SourceUrl is { Length: > 0 } url)
            {
                AppendDetailItem(sb, "Package URL", FormatTableLink(url));
            }
            if (pkg.UpdatedAt is { } u)
            {
                AppendDetailItem(sb, "Updated", MarkdownTableFormatter.FormatTableCell(
                    u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }
        }
        if (s.FrontMatter.Description is { Length: > 0 } desc)
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(desc);
        }
        foreach (var diagnostic in s.FrontMatter.Diagnostics.Concat(s.MetadataConflicts).Concat(s.OwnershipDiagnostics))
            sb.AppendLine($"- ⚠️ {MarkdownTableFormatter.FormatTableCell(diagnostic)}");
        if (s.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Agents");
            sb.AppendLine();
            foreach (var a in s.Agents)
            {
                var kind = a.IsSymlink ? "symlink" : "direct";
                sb.Append("- ");
                sb.Append(TuiHelpers.AgentIcon(a.AgentId));
                sb.Append(" **");
                sb.Append(MarkdownTableFormatter.FormatTableCell(a.AgentId));
                sb.Append("** — ");
                sb.Append(MarkdownTableFormatter.FormatTableCell(kind));
                sb.AppendLine();
                sb.Append("  Path: ");
                sb.AppendLine(MarkdownTableFormatter.FormatCodeSpan(a.Path));
                if (a.Profile is { } profile) sb.AppendLine("  Profile: " + MarkdownTableFormatter.FormatTableCell(profile));
                if (a.Scope is { } scope) sb.AppendLine("  Scope: " + scope);
                if (a.Context is { } context) sb.AppendLine("  Context: " + MarkdownTableFormatter.FormatCodeSpan(context));
                sb.AppendLine();
            }
        }
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";

    private static void AppendDetailItem(System.Text.StringBuilder sb, string label, string value)
    {
        sb.Append("- **");
        sb.Append(label);
        sb.Append(":** ");
        sb.AppendLine(value);
        sb.AppendLine();
    }

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
