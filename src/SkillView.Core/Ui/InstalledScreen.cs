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
/// source overview owns navigation and exact-copy handoff. These helpers retain
/// local shortcut policy and the shared ordinary/technical detail renderer.
public static class InstalledScreen
{
    internal enum ShortcutCommand { None, Close, FocusFilter, FocusTable, CycleSort, Remove, Open }
    internal readonly record struct ShortcutDecision(ShortcutCommand Command, bool RequestStop);


    internal static ShortcutDecision DecideShortcut(Key key, bool filterHasFocus, bool canRemove)
    {
        if (filterHasFocus)
        {
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
            return new ShortcutDecision(ShortcutCommand.FocusFilter, RequestStop: false);
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

    internal static string RenderDetail(InstalledSkill s, bool showDetails = false, string? homeDirectory = null, bool showShortcuts = true)
    {
        if (!showDetails && homeDirectory is null && s.OwnershipRunner is { } runner)
            homeDirectory = DisplayPath.CaptureHome(runner);
        string PathLabel(string path) => showDetails ? path : DisplayPath.Abbreviate(path, homeDirectory);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {MarkdownTableFormatter.FormatTableCell(InstalledOverview.Text(s.Name, 128))}");
        sb.AppendLine();
        if (s.FrontMatter.Description is { Length: > 0 } description)
        {
            sb.AppendLine("Description · from skill metadata (untrusted):");
            sb.AppendLine();
            var text = (TerminalEscapeSanitizer.Sanitize(description.Length > 2048 ? description[..2048] : description) ?? "")
                .ReplaceLineEndings("\n");
            text = string.Concat(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? '\uFFFD' : c));
            var longestRun = 0;
            var run = 0;
            foreach (var c in text)
            {
                run = c == '`' ? run + 1 : 0;
                longestRun = Math.Max(longestRun, run);
            }
            // Compute after bounding and sanitizing: neither embedded fences nor
            // controls that join backtick runs can escape this literal block.
            var fence = new string('`', Math.Max(3, longestRun + 1));
            sb.AppendLine(fence);
            sb.AppendLine(text);
            if (description.Length > 2048) sb.AppendLine("[truncated]");
            sb.AppendLine(fence);
            sb.AppendLine();
        }

        if (!showDetails)
        {
            var recorded = s.ObservedSubject?.Source ?? s.SourceIdentity;
            AppendDetailItem(sb, "Source", MarkdownTableFormatter.FormatCodeSpan(recorded.Key is null
                ? (recorded.Conflicted ? "Conflicting source" : "Unknown or incomplete source") + (recorded.Origin is null ? "" : " · recorded " + InstalledOverview.SourceLabel(recorded)) : InstalledOverview.SourceLabel(recorded)));
            AppendDetailItem(sb, "Location", MarkdownTableFormatter.FormatCodeSpan(s.ResolvedPath));
            AppendDetailItem(sb, "Scope", InstalledInventoryFormatter.DescribeLocation(s));
            var revision = s.Revision;
            if (revision.RepositoryRef is { } gitRef) AppendDetailItem(sb, "Recorded Git ref", MarkdownTableFormatter.FormatCodeSpan(gitRef));
            if (revision.AuthoredVersion is { } authored) AppendDetailItem(sb, "Authored version", MarkdownTableFormatter.FormatCodeSpan(authored));
            if (revision.DistributorVersion is { } distributor) AppendDetailItem(sb, "Distributor version", MarkdownTableFormatter.FormatCodeSpan(distributor));
            var policy = PinDisplayPolicy.From(s);
            AppendDetailItem(sb, "Pinned", MarkdownTableFormatter.FormatTableCell(policy.Label + " · " + policy.Description));
            if (policy.PinnedRef is { } summaryPinnedRef) AppendDetailItem(sb, "Pinned ref", MarkdownTableFormatter.FormatCodeSpan(summaryPinnedRef));
            sb.AppendLine();
            sb.AppendLine("## Used by · recorded placements");
            sb.AppendLine($"{s.Agents.Length} placement(s) share this one physical copy.");
            foreach (var agent in s.Agents)
                sb.AppendLine("- " + MarkdownTableFormatter.FormatTableCell(agent.AgentId + " · " + (agent.Scope ?? s.Scope))
                    + " · " + MarkdownTableFormatter.FormatCodeSpan(agent.Path)
                    + (agent.Context is { } context ? " · " + MarkdownTableFormatter.FormatCodeSpan(context) : "")
                    + (agent.Profile is { } profile ? " · Profile: " + MarkdownTableFormatter.FormatTableCell(profile) : ""));
            if (s.Agents.IsEmpty) sb.AppendLine("No consumers recorded; other consumers may exist.");
            sb.AppendLine();
            foreach (var owner in s.AllOwnershipClaims)
            {
                AppendDetailItem(sb, "Also recorded by", MarkdownTableFormatter.FormatCodeSpan(owner.Provider + ": " + owner.Source));
                if (owner.SourceUrl is { } url) AppendDetailItem(sb, "Recorded source URL", MarkdownTableFormatter.FormatCodeSpan(url));
                if (owner.SkillPath is { } path) AppendDetailItem(sb, "Recorded source path", MarkdownTableFormatter.FormatCodeSpan(path));
                if (owner.RepositoryRef is { } r) AppendDetailItem(sb, "Recorded Git ref", MarkdownTableFormatter.FormatCodeSpan(r));
                if (owner.PackageVersion is { } v) AppendDetailItem(sb, "Distributor version", MarkdownTableFormatter.FormatCodeSpan(v));
                if (owner.ReadOnly || owner.InstallationMode == "managed-read-only")
                    AppendDetailItem(sb, "Maintenance", "Read-only; use the owning agent or package manager");
            }
            if (!s.MetadataConflicts.IsEmpty || !s.OwnershipDiagnostics.IsEmpty)
                AppendDetailItem(sb, "Evidence limits", "Metadata or ownership conflicts remain; inspect F2 before requesting an operation.");
            if (showShortcuts) sb.AppendLine("\n**F2** Technical details.");
            return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? "";
        }

        sb.AppendLine("## Summary");
        sb.AppendLine();
        AppendDetailItem(sb, "Path", MarkdownTableFormatter.FormatCodeSpan(s.ResolvedPath));

        AppendDetailItem(sb, "Location", MarkdownTableFormatter.FormatTableCell(InstalledInventoryFormatter.DescribeLocation(s)));
        AppendDetailItem(sb, "Source", MarkdownTableFormatter.FormatTableCell(InstalledInventoryFormatter.DescribeProvenance(s)));
        AppendDetailItem(sb, "Health", MarkdownTableFormatter.FormatTableCell(
            s.Validity == ValidityState.Valid ? "✅ Valid" : $"⚠️ {s.Validity}"));
        if (showDetails) AppendDetailItem(sb, "Symlinked", MarkdownTableFormatter.FormatTableCell(FormatBool(s.IsSymlinked)));
        var pin = PinDisplayPolicy.From(s);
        AppendDetailItem(sb, "Pinned", MarkdownTableFormatter.FormatTableCell(pin.Label));
        if (pin.State is PinDisplayState.Conflicting or PinDisplayState.Unknown)
            AppendDetailItem(sb, "Pin policy", MarkdownTableFormatter.FormatTableCell(pin.Guidance));
        if (s.FrontMatter.GithubRef is { } repositoryRef) AppendDetailItem(sb, "Repository ref", MarkdownTableFormatter.FormatCodeSpan(repositoryRef));
        if (pin.PinnedRef is { } pinnedRef) AppendDetailItem(sb, "Pinned ref", MarkdownTableFormatter.FormatCodeSpan(pinnedRef));
        if (s.FrontMatter.GithubPath is { } sourcePath) AppendDetailItem(sb, "Source path", MarkdownTableFormatter.FormatCodeSpan(sourcePath));
        if (s.FrontMatter.LocalPath is { } localPath)
        {
            AppendDetailItem(sb, "Local source", MarkdownTableFormatter.FormatCodeSpan(PathLabel(localPath)));
            if (showShortcuts)
            {
                AppendDetailItem(sb, "Local source switch", "**T** Review a different local source.");
                AppendDetailItem(sb, "Local reimport", "**R** Review changes from this source. The installed original is retained for recovery.");
            }
        }
        if (showDetails) AppendDetailItem(sb, "Ignored", MarkdownTableFormatter.FormatTableCell(FormatBool(s.Ignored)));
        if (s.CleanupSuppression is { } suppression && showDetails)
            AppendDetailItem(sb, "Cleanup suppression", MarkdownTableFormatter.FormatTableCell(
                $"{suppression.State}; legacy={suppression.LegacyMarker}, external={suppression.External}. {suppression.Diagnostic}"));
        if (showDetails && !string.IsNullOrWhiteSpace(s.TreeSha))
        {
            AppendDetailItem(sb, "Tree SHA", MarkdownTableFormatter.FormatCodeSpan(s.TreeSha));
        }
        if (!string.IsNullOrWhiteSpace(s.FrontMatter.Version))
        {
            AppendDetailItem(sb, "Authored version", MarkdownTableFormatter.FormatTableCell(s.FrontMatter.Version));
        }
        if (s.FrontMatter.Upstream is { Length: > 0 } upstream)
        {
            AppendDetailItem(sb, "Upstream", FormatTableLink(upstream, "Open upstream", showDetails));
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
                if (showDetails) AppendDetailItem(sb, "Loading", "Enabled and active session state unknown");
            }
            if (pkg.SourceUrl is { Length: > 0 } url)
            {
                AppendDetailItem(sb, "Package URL", FormatTableLink(url, "Open package source", showDetails));
            }
            if (pkg.UpdatedAt is { } u)
            {
                AppendDetailItem(sb, "Updated", MarkdownTableFormatter.FormatTableCell(
                    u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }
        }
        foreach (var owner in s.AllOwnershipClaims)
        {
            AppendDetailItem(sb, "Also recorded by", MarkdownTableFormatter.FormatCodeSpan(owner.Provider + ": " + owner.Source));
            if (owner.SkillPath is { } ownerPath) AppendDetailItem(sb, "Recorded source path", MarkdownTableFormatter.FormatCodeSpan(ownerPath));
            if (owner.RepositoryRef is { } ownerRef) AppendDetailItem(sb, "Recorded Git ref", MarkdownTableFormatter.FormatCodeSpan(ownerRef));
            if (owner.PackageVersion is { } ownerVersion) AppendDetailItem(sb, "Distributor version", MarkdownTableFormatter.FormatCodeSpan(ownerVersion));
            if (owner.ReadOnly || owner.InstallationMode == "managed-read-only") AppendDetailItem(sb, "Maintenance", "Read-only; use the owning agent or package manager");
        }
        var source = s.ObservedSubject?.Source ?? s.SourceIdentity;
        AppendDetailItem(sb, "Source evidence", source.Conflicted ? "Conflicting" : source.Key is null ? "Unknown or incomplete" : "Known recorded source");
        if (!s.MetadataConflicts.IsEmpty || !s.OwnershipDiagnostics.IsEmpty)
            AppendDetailItem(sb, "Evidence limits", "Metadata or ownership conflicts remain; inspect F2 before requesting an operation.");
        if (showDetails) foreach (var diagnostic in s.FrontMatter.Diagnostics.Concat(s.MetadataConflicts).Concat(s.OwnershipDiagnostics))
                sb.AppendLine($"- ⚠️ {MarkdownTableFormatter.FormatTableCell(diagnostic)}");
        if (s.Agents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Used by · recorded placements");
            sb.AppendLine($"{s.Agents.Length} placement(s) share this one physical copy.");
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
                sb.AppendLine(MarkdownTableFormatter.FormatCodeSpan(PathLabel(a.Path)));
                if (a.Profile is { } profile) sb.AppendLine("  Profile: " + MarkdownTableFormatter.FormatTableCell(profile));
                if (a.Scope is { } scope) sb.AppendLine("  Scope: " + scope);
                if (a.Context is { } context) sb.AppendLine("  Context: " + MarkdownTableFormatter.FormatCodeSpan(PathLabel(context)));
                sb.AppendLine();
            }
        }
        sb.AppendLine();
        if (showShortcuts) sb.AppendLine("**K** Check package · **F2** Technical details.");
        if (showDetails) sb.AppendLine("Active-session loading is unknown.");
        if (showDetails && s.ObservedSubject is { } subject)
        {
            sb.AppendLine();
            sb.AppendLine("## Technical details");
            sb.AppendLine();
            OperationImpactFormatter.Append(sb, OperationImpact.Payload(subject, "inspect"));
        }
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";

    private static void AppendDetailItem(System.Text.StringBuilder sb, string label, string value)
    {
        sb.Append("- **");
        sb.Append(label);
        sb.Append(":** ");
        sb.AppendLine(value.Length > 2048 ? value[..2048] + " [truncated]" : value);
    }

    private static string FormatTableLink(string value, string label, bool showDestination)
    {
        var normalized = MarkdownTableFormatter.NormalizeTableValue(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return MarkdownTableFormatter.FormatCodeSpan(normalized);
        var link = $"[{label}]({EscapeMarkdownLinkDestination(normalized)})";
        return showDestination ? link + " · " + MarkdownTableFormatter.FormatCodeSpan(normalized) : link;
    }

    private static string EscapeMarkdownLinkDestination(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);

}
