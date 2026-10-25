using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed record InstalledFilterChoice(string Key, string Label);
internal sealed record InstalledFilterSelection(string? Agent = null, string? Location = null, string? Source = null, string? Issue = null);

/// Only the queue already produced by Issues is handed to Installed. Reference
/// identity and the catalog generation both have to agree; a partial queue can
/// establish a finding, never safe absence. No classification is performed here.
internal sealed record InstalledIssueEvidence(InventorySnapshot Snapshot, long Generation, bool Partial,
    IReadOnlyDictionary<(string Id, string Path), string[]> Findings)
{
    internal static InstalledIssueEvidence Capture(InventorySnapshot snapshot, long generation, MaintenanceQueue queue) =>
        new(snapshot, generation, queue.Partial || snapshot.IsPartial || !snapshot.ConfiguredRootsComplete,
            queue.Entries.Where(e => e.Issue != "installation-review")
                .GroupBy(e => (e.Subject.InstallationId, e.Subject.Path))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Issue).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));

    internal bool IsCurrent(InventorySnapshot snapshot, long generation) => ReferenceEquals(snapshot, Snapshot) && Generation == generation;
}

/// Frozen facets over captured data. Membership addresses are kept together so
/// Agent + Project/location (+ legacy Scope) cannot join different exposures.
internal sealed class InstalledFilters
{
    private sealed record Membership(string Agent, string Location, Scope Scope);
    private sealed record Facts(string Id, string Path, Membership[] Memberships, string[] Sources);
    private readonly Dictionary<InstalledSkill, Facts> _facts = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyList<InstalledFilterChoice> Agents { get; }
    internal IReadOnlyList<InstalledFilterChoice> Locations { get; }
    internal IReadOnlyList<InstalledFilterChoice> Sources { get; }
    internal string CoverageDetail { get; }
    internal InventorySnapshot Snapshot { get; }

    internal InstalledFilters(InventorySnapshot snapshot, CancellationToken token, IEnumerable<(string Id, InstalledSkill Skill)> captured)
    {
        Snapshot = snapshot;
        var addresses = new Dictionary<InstalledSkill, string>(ReferenceEqualityComparer.Instance);
        foreach (var item in captured) addresses.TryAdd(item.Skill, item.Id);
        var agents = new Dictionary<string, string>(StringComparer.Ordinal);
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skill in snapshot.Skills)
        {
            token.ThrowIfCancellationRequested();
            var memberships = skill.Agents.Select(a =>
            {
                var scope = a.Scope ?? skill.Scope;
                // Context is captured, not inferred by walking parents. A root
                // or exact placement is an honest fallback for legacy records.
                var location = Key("location", scope.ToString(), a.Context ?? a.DiscoveryRoot ?? a.Path);
                locations.TryAdd(location, scope + ": " + (a.Context ?? a.DiscoveryRoot ?? a.Path));
                var agent = Key("agent", a.AgentId);
                agents.TryAdd(agent, a.AgentId == "unknown" ? "Unknown agent" : a.AgentId);
                return new Membership(agent, location, scope);
            }).ToArray();
            if (memberships.Length == 0)
            {
                // No agent availability is invented. Physical scope/root is
                // still usable as a copy-level location with unknown consumer.
                var location = Key("copy-location", skill.Scope.ToString(), skill.ScanRoot);
                locations.TryAdd(location, skill.Scope + ": " + skill.ScanRoot + " (no recorded consumers)");
                memberships = [new(Key("agent", "unknown"), location, skill.Scope)];
                agents.TryAdd(Key("agent", "unknown"), "Unknown agent");
            }
            var source = skill.ObservedSubject?.Source ?? skill.SourceIdentity;
            var sourceKeys = new List<string>();
            var primaryKey = source.Conflicted ? "conflicting-source" : SourceKey(source) ?? "unknown-source";
            sources.TryAdd(primaryKey, source.Conflicted ? "Conflicting source claims" : SourceKey(source) is null ? "Unknown source" : "Source: " + InstalledOverview.SourceLabel(source));
            sourceKeys.Add(primaryKey);
            foreach (var owner in skill.AllOwnershipClaims)
            {
                // Presentation keys retain contextual owner/provider domains.
                // Reuse captured strings; no hashing or path inspection here.
                var ownerContext = owner.ManagedRegion?.Context ?? owner.ContextRoot;
                var key = "owner:" + Key(owner.Provider, owner.SourceType, owner.Source, owner.SourceUrl ?? "",
                    owner.PluginName ?? "", ownerContext ?? "", owner.InstallationRoot ?? "", owner.LockFilePath ?? "");
                sources.TryAdd(key, "Owner: " + owner.Provider + " / " + (owner.PluginName ?? owner.SourceUrl ?? owner.Source)
                    + (ownerContext is { } context ? " [" + context + "]" : ""));
                sourceKeys.Add(key);
                var claimedSource = SkillSourceIdentity.From(skill with
                {
                    FrontMatter = SkillFrontMatter.Empty,
                    Package = owner,
                    OwnershipClaims = [],
                    MetadataConflicts = [],
                    ObservedSubject = null
                });
                if (SourceKey(claimedSource) is { } claimKey)
                {
                    sources.TryAdd(claimKey, "Source: " + InstalledOverview.SourceLabel(claimedSource));
                    sourceKeys.Add(claimKey);
                }
                else
                {
                    sources.TryAdd("unknown-source", "Unknown source");
                    sourceKeys.Add("unknown-source");
                }
                if (owner.ReadOnly || owner.InstallationMode == "managed-read-only")
                { sources.TryAdd("managed-owner", "Managed / read-only owner"); sourceKeys.Add("managed-owner"); }
            }
            _facts.TryAdd(skill, new(addresses[skill], skill.ResolvedPath, memberships, sourceKeys.ToArray()));
        }
        Agents = Choices(agents); Locations = Choices(locations); Sources = Choices(sources);
        CoverageDetail = BuildCoverage(snapshot, token);
    }

    internal bool Matches(InstalledSkill skill, InstalledFilterSelection selection, Scope? scope,
        InstalledIssueEvidence? issues, long generation)
    {
        if (!_facts.TryGetValue(skill, out var facts)) return false;
        // G alone preserves the CLI/legacy physical-scope OR exposure-scope
        // rule. Once either membership facet is selected, all three predicates
        // must describe the same exposure; copy scope cannot fill a missing match.
        return ((selection.Agent is null && selection.Location is null && skill.Scope == scope)
                || facts.Memberships.Any(m => (selection.Agent is null || m.Agent == selection.Agent)
                    && (selection.Location is null || m.Location == selection.Location) && (scope is null || m.Scope == scope)))
            && (selection.Source is null || facts.Sources.Contains(selection.Source, StringComparer.Ordinal))
            && MatchesIssue(facts, selection.Issue, issues, generation);
    }

    private bool MatchesIssue(Facts facts, string? issue, InstalledIssueEvidence? evidence, long generation)
    {
        if (issue is null) return true;
        var current = evidence?.IsCurrent(Snapshot, generation) == true;
        var findings = current && evidence!.Findings.TryGetValue((facts.Id, facts.Path), out var found) ? found : [];
        return issue switch
        {
            "unknown" => !current || evidence!.Partial,
            "none-observed" => current && !evidence!.Partial && findings.Length == 0,
            "any-finding" => findings.Length > 0,
            _ => findings.Contains(issue, StringComparer.Ordinal),
        };
    }

    internal IReadOnlyList<InstalledFilterChoice> IssueChoices(InstalledIssueEvidence? issues, long generation)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal) { ["unknown"] = "Unknown / incomplete checks" };
        if (issues?.IsCurrent(Snapshot, generation) == true)
        {
            choices.Add("any-finding", "Has captured findings");
            if (!issues.Partial) choices.Add("none-observed", "No local findings observed (captured scan)");
            // Entries without a matching physical address remain Issues-only.
            foreach (var facts in _facts.Values)
                if (issues.Findings.TryGetValue((facts.Id, facts.Path), out var found))
                    foreach (var issue in found) choices.TryAdd(issue, issue);
        }
        return Choices(choices);
    }

    private static string? SourceKey(SkillSourceIdentity source) => source.Conflicted || source.Origin is null
        || (source.Provider == "repository" && source.Subpath is null) ? null
        : "source:" + Key(source.Provider, source.Origin, source.Subpath is null ? "absent" : "present", source.Subpath ?? "");

    internal static string Key(params string[] parts) => string.Concat(parts.Select(p => p.Length + ":" + p));
    internal static string ChoiceText(string value, int columns) => TuiHelpers.Truncate(InstalledOverview.Text(value, columns), columns);

    private static IReadOnlyList<InstalledFilterChoice> Choices(Dictionary<string, string> values)
    {
        var choices = values.Select(v => new InstalledFilterChoice(v.Key, ChoiceText(v.Value, 64))).ToArray();
        foreach (var collision in choices.GroupBy(c => c.Label).Where(g => g.Count() > 1))
        {
            var i = 0;
            foreach (var choice in collision.OrderBy(c => c.Key, StringComparer.Ordinal))
                choices[Array.IndexOf(choices, choice)] = choice with { Label = ChoiceText(choice.Label, 52) + $" #{++i}" };
        }
        return choices.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
    }

    private static string BuildCoverage(InventorySnapshot snapshot, CancellationToken token)
    {
        const int limit = 128;
        var lines = new List<string> { "## Captured coverage", "Other projects are scanned only when configured. Recorded placements do not establish running or enabled agents.",
            snapshot.IsPartial || !snapshot.ConfiguredRootsComplete ? "Partial scan; other copies and consumers may exist." : "Observed placements within captured roots only." };
        var remaining = MaintenanceQueueFormatter.MaxCoverageCharacters - 1024 - lines.Sum(line => line.Length + 2);
        void Add(string label, IEnumerable<string> values)
        {
            var all = values.Distinct(StringComparer.Ordinal).ToArray();
            var omitted = all.Length;
            foreach (var value in all.Take(limit))
            {
                token.ThrowIfCancellationRequested();
                var line = label + ": " + MarkdownTableFormatter.FormatCodeSpan(InstalledOverview.Text(value, 320));
                if (line.Length + 2 > remaining) continue;
                lines.Add(line); remaining -= line.Length + 2; omitted--;
            }
            if (omitted > 0) lines.Add($"{omitted} additional {label.ToLowerInvariant()} entries omitted.");
        }
        Add("Could not be scanned", snapshot.Diagnostics.InaccessiblePaths);
        Add("Captured limitation", snapshot.Diagnostics.Locations.Select(l => l.Path + " — " + l.State + ": " + l.Reason)
            .Concat(snapshot.Providers.Where(p => !p.Succeeded).Select(p => p.Provider + ": " + (p.Error ?? "capture failed")))
            .Concat(snapshot.PreferenceDiagnostics));
        if (!snapshot.ConfiguredRootsComplete) lines.Add("Configured-root discovery is incomplete.");
        Add("Scanned", snapshot.ScannedRoots.Select(r => r.Scope + " " + r.Path));
        Add("Configured", snapshot.ConfiguredRoots.Select(r => r.Scope + " " + r.Path));
        // Do not enumerate arbitrary projects or infer unscanned locations from
        // path parents. Only explicit retained owner-context/link evidence.
        var scannedContexts = snapshot.ScannedRoots.Select(r => r.Context ?? r.Path).ToHashSet(StringComparer.Ordinal);
        Add("Referenced, not scanned", snapshot.Skills.SelectMany(s => s.AllOwnershipClaims.Select(o => o.ContextRoot).OfType<string>())
            .Where(context => !scannedContexts.Contains(context) && !snapshot.ConfiguredRoots.Any(r => r.Context == context || r.Path == context)));
        return string.Join("\n\n", lines);
    }
}
