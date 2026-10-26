using System.Globalization;
using System.Text;
using SkillView.Inventory.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
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

    internal static string RenderDetail(InstalledSkill s, bool showDetails = false, string? homeDirectory = null, bool showShortcuts = true, int? descriptionWidth = null, int? recordedPlacementCount = null)
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
            var length = Math.Min(description.Length, 2048);
            if (length < description.Length && char.IsHighSurrogate(description[length - 1]) && char.IsLowSurrogate(description[length]))
                length--;
            var text = (TerminalEscapeSanitizer.Sanitize(description[..length]) ?? "").ReplaceLineEndings("\n");
            text = string.Concat(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? '\uFFFD' : c));
            // Wrap only normalized source lines, never generated Markdown. An unknown
            // viewport defers wrapping; every reflow starts again from the source.
            if (descriptionWidth is > 0)
                text = string.Join("\n", text.Split('\n').SelectMany(line => line.Length == 0
                    ? [""] : TextFormatter.WordWrapText(line, descriptionWidth.Value, tabWidth: 4)));
            var longestRun = 0;
            var run = 0;
            foreach (var c in text)
            {
                run = c == '`' ? run + 1 : 0;
                longestRun = Math.Max(longestRun, run);
            }
            // Compute after final wrapping, which can create standalone fence lines.
            var fence = new string('`', Math.Max(3, longestRun + 1));
            sb.AppendLine(fence);
            sb.AppendLine(text);
            sb.AppendLine(fence);
            sb.AppendLine();
            if (length < description.Length) sb.AppendLine("[truncated]\n");
        }

        if (!showDetails)
        {
            var recorded = s.ObservedSubject?.Source ?? s.SourceIdentity;
            sb.AppendLine("Source: " + Literal(recorded.Key is null
                ? (recorded.Conflicted ? "Conflicting source" : "Unknown or incomplete source") + (recorded.Origin is null ? "" : " · recorded " + InstalledOverview.SourceLabel(recorded))
                : InstalledOverview.SourceLabel(recorded)));
            sb.AppendLine();
            var revision = s.Revision;
            var policy = PinDisplayPolicy.From(s);
            var facts = new List<string> { s.Scope.ToString() };
            if (revision.RepositoryRef is { } gitRef)
                facts.Add(gitRef.StartsWith("refs/tags/", StringComparison.Ordinal)
                    ? "Git tag " + Literal(gitRef["refs/tags/".Length..]) : "Recorded ref " + Literal(gitRef));
            facts.Add("Pin: " + Literal(policy.Description));
            sb.AppendLine(string.Join(" · ", facts));
            sb.AppendLine();
            var placements = recordedPlacementCount ?? s.Agents.Distinct().Count();
            sb.AppendLine("1 physical copy · " + PlacementCount(placements));
            sb.AppendLine();
            sb.AppendLine("- **Location:** " + Literal(s.ResolvedPath));
            sb.AppendLine();
            sb.Append(RenderPlacements(s));
            if (revision.AuthoredVersion is { } authored) AppendDetailItem(sb, "Authored version", Literal(authored));
            if (revision.DistributorVersion is { } distributor) AppendDetailItem(sb, "Distributor version", Literal(distributor));
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
            sb.AppendLine("## Agent/profile placements (recorded)");
            var count = recordedPlacementCount ?? s.Agents.Distinct().Count();
            sb.AppendLine(PlacementCount(count) + (count == 1 ? " shares" : " share") + " this one physical copy.");
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

    internal const int MaxRecordedPlacements = 128;
    internal const int MaxPlacementCharacters = 32768;
    private const int PlacementNoticeReserve = 256;

    internal static string PlacementCount(int count) => $"{count} recorded placement{(count == 1 ? "" : "s")}";

    private static string NormalizeLiteral(string value) => string.Concat(
        (TerminalEscapeSanitizer.Sanitize(value) ?? "").ReplaceLineEndings(" ")
        .Select(c => c == '\t' ? ' ' : char.IsControl(c) ? '\uFFFD' : c));

    private static string Literal(string value) => MarkdownTableFormatter.FormatCodeSpan(NormalizeLiteral(value));

    internal static string RenderPlacements(InstalledSkill skill)
    {
        // Equality remains the existing structural AgentMembership record equality.
        // Grouping and normalized labels never change the observed denominator.
        var ordered = skill.Agents.Distinct()
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .ThenBy(a => a.Scope ?? skill.Scope)
            .ThenBy(a => a.Context is null)
            .ThenBy(a => a.Context, StringComparer.Ordinal)
            .ThenBy(a => NormalizeLiteral(a.AgentId), StringComparer.Ordinal)
            .ThenBy(a => a.Profile is null ? null : NormalizeLiteral(a.Profile), StringComparer.Ordinal)
            .ThenBy(a => a, Comparer<AgentMembership>.Create(CompareMemberships)).ToArray();
        var included = new List<AgentMembership>();
        var block = PlacementBlock(skill, included);
        foreach (var member in ordered.Take(MaxRecordedPlacements))
        {
            included.Add(member);
            var candidate = PlacementBlock(skill, included);
            if (candidate.Length > MaxPlacementCharacters - PlacementNoticeReserve)
            {
                included.RemoveAt(included.Count - 1);
                break;
            }
            block = candidate;
        }
        var omitted = ordered.Length - included.Count;
        if (omitted > 0) block += $"{omitted} more recorded placement{(omitted == 1 ? "" : "s")} omitted\n\n";
        if (ordered.Length == 0) block += "No consumers recorded; other consumers may exist.\n\n";
        return block;
    }

    private static string PlacementBlock(InstalledSkill skill, IEnumerable<AgentMembership> members)
    {
        var sb = new StringBuilder("## Agent/profile placements (recorded)\n\n");
        foreach (var path in members.GroupBy(a => a.Path, StringComparer.Ordinal))
        {
            sb.AppendLine(string.Equals(path.Key, skill.ResolvedPath, StringComparison.Ordinal)
                ? "Same location" : "Location: " + Literal(path.Key));
            sb.AppendLine();
            foreach (var group in path.GroupBy(a => (Scope: a.Scope ?? skill.Scope, a.Context)))
            {
                sb.Append(group.Key.Scope);
                if (group.Key.Context is { } context) sb.Append(" · Context: " + Literal(context));
                else if (group.Key.Scope == Scope.Project) sb.Append(" — project root not recorded");
                sb.Append(": ");
                sb.AppendLine(string.Join(", ", group.GroupBy(a => (Agent: NormalizeLiteral(a.AgentId), Profile: a.Profile is null ? null : NormalizeLiteral(a.Profile)))
                    .Select(label => MarkdownTableFormatter.FormatCodeSpan(label.Key.Agent)
                        + (label.Key.Profile is { } profile ? " (Profile: " + MarkdownTableFormatter.FormatCodeSpan(profile) + ")" : "")
                        + (label.Count() > 1 ? $" ×{label.Count()}" : ""))));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static int CompareMemberships(AgentMembership a, AgentMembership b)
    {
        // Declaration-order ties: ordinal strings (null first), ascending enums
        // and false before true, including evidence hidden in ordinary detail.
        var comparison = StringComparer.Ordinal.Compare(a.AgentId, b.AgentId);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(a.Path, b.Path);
        if (comparison != 0) return comparison;
        comparison = a.IsSymlink.CompareTo(b.IsSymlink);
        if (comparison != 0) return comparison;
        comparison = Nullable.Compare(a.Scope, b.Scope);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(a.Context, b.Context);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(a.DiscoveryRoot, b.DiscoveryRoot);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(a.Profile, b.Profile);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(a.AgentVersion, b.AgentVersion);
        if (comparison != 0) return comparison;
        comparison = Nullable.Compare(a.Enabled, b.Enabled);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.LoadingRule, b.LoadingRule);
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
