using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal sealed record InstalledCopy(string Id, IReadOnlyList<InstalledSkill> Records, string Location)
{
    internal bool Ambiguous => Records.Count != 1;
    internal InstalledSkill? Target => Ambiguous ? null : Records[0];
    internal string Path => Records[0].ResolvedPath;
    internal string Key => "copy:" + Id + ":" + Path;
    internal bool ReadOnly => Records.Any(s => s.AllOwnershipClaims.Any(o => o.ReadOnly || o.InstallationMode == "managed-read-only"));
}

internal sealed record InstalledSourceGroup(string Key, string Name, string Source, IReadOnlyList<InstalledCopy> Copies,
    bool RefsDiffer, bool RefsIncomplete);

internal sealed record InstalledOverviewRow(string Key, InstalledSourceGroup? Group, InstalledCopy? Copy, string Label, string Summary);
internal sealed record InstalledOverviewRows(IReadOnlyList<InstalledOverviewRow> Rows, int ShownCopies, int OmittedRows, int MatchingCopies);

/// Transient presentation over one captured inventory. Construct off the UI thread;
/// filtering, navigation and drawing only consume the frozen values below.
internal sealed record InstalledOverview(IReadOnlyList<InstalledSourceGroup> Groups, int Copies, int Placements, int AmbiguousCopies, string Coverage)
{
    internal const int MaxRows = 8192;
    internal static InstalledOverview Empty { get; } = new([], 0, 0, 0, "Inventory not loaded");

    internal static InstalledOverview Build(InventorySnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = new List<(string Id, InstalledSkill Skill)>();
        foreach (var skill in snapshot.Skills)
        {
            token.ThrowIfCancellationRequested();
            captured.Add((skill.ObservedSubject?.InstallationId ?? skill.InstallationId, skill));
        }
        var copies = new List<InstalledCopy>();
        foreach (var named in captured.GroupBy(s => Text(s.Skill.Name, 64), StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var labels = DistinguishingPathLabels.Build(named.Select(s => s.Skill.ResolvedPath), named.Key, 48, token);
            foreach (var row in named)
            {
                token.ThrowIfCancellationRequested();
                copies.Add(new(row.Id, [row.Skill], labels[row.Skill.ResolvedPath]));
            }
        }
        // Repeated IDs are inspect-only, including repeated records at the same path.
        // Retain every record/claim, without choosing one as mutation authority.
        copies = copies.GroupBy(c => c.Id, StringComparer.Ordinal).Select(group => new InstalledCopy(group.Key,
            group.SelectMany(c => c.Records).OrderBy(s => s.ResolvedPath, StringComparer.Ordinal).ToArray(), group.First().Location)).ToList();
        var sources = new Dictionary<string, (string Name, string Source, List<InstalledCopy> Copies)>(StringComparer.Ordinal);
        foreach (var copy in copies)
        {
            token.ThrowIfCancellationRequested();
            var skill = copy.Records[0];
            var source = skill.ObservedSubject?.Source ?? skill.SourceIdentity;
            var knownKey = source.Key;
            var key = !copy.Ambiguous && knownKey is not null
                ? "source:" + knownKey + ":" + skill.Name.ToUpperInvariant() : "singleton:" + copy.Key;
            var label = source.Conflicted ? "Conflicting source" : knownKey is null ? "Unknown source" : SourceLabel(source);
            if (copy.Ambiguous || knownKey is null) label += " [" + copy.Location + "]";
            if (!sources.TryGetValue(key, out var group))
            {
                group = (Text(skill.Name, 64), Text(label, 140), []);
                sources.Add(key, group);
            }
            group.Copies.Add(copy);
        }
        var groups = new List<InstalledSourceGroup>();
        foreach (var (key, group) in sources)
        {
            token.ThrowIfCancellationRequested();
            var refs = group.Copies.SelectMany(c => c.Records).Select(s => s.Revision.RepositoryRef).ToArray();
            groups.Add(new(key, group.Name, group.Source, group.Copies.OrderBy(c => c.Path, StringComparer.Ordinal).ToArray(),
                refs.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).Take(2).Count() >= 2,
                refs.Any(string.IsNullOrWhiteSpace)));
        }
        // Distinct raw names/sources can become equal after sanitization/bounding;
        // same-path observations can also carry different captured IDs. Preserve
        // ordinal visible distinctions without changing any frozen routing key.
        foreach (var collision in groups.GroupBy(g => (g.Name, g.Source)).Where(g => g.Count() > 1).ToArray())
        {
            var index = 0;
            foreach (var group in collision.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                groups[groups.IndexOf(group)] = group with { Source = group.Source + $" #{++index}" };
            }
        }
        var partial = snapshot.IsPartial || !snapshot.ConfiguredRootsComplete;
        var coverage = $"{(partial ? "Partial" : "Captured")}: {snapshot.ScannedRoots.Length} scanned / {snapshot.ConfiguredRoots.Length} configured roots"
            + (partial ? $"; {snapshot.Diagnostics.InaccessiblePaths.Length} access limits; other consumers may exist" : "; observed placements only");
        return new(groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray(),
            copies.Count, copies.Sum(c => c.Records.SelectMany(s => s.Agents).Distinct().Count()), copies.Count(c => c.Ambiguous), coverage);
    }

    internal InstalledOverviewRows View(Func<InstalledSkill, bool> matches, string sort, ISet<string> expanded,
        Func<InstalledSkill, UpdateAvailability>? availability, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var groups = Groups.Select(g => (Group: g, Copies: g.Copies.Where(c => c.Records.Any(matches)).ToArray()))
            .Where(g => g.Copies.Length > 0);
        groups = sort switch
        {
            "source" => groups.OrderBy(g => g.Group.Source, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Group.Key, StringComparer.Ordinal),
            "location" => groups.OrderBy(g => g.Copies[0].Path, StringComparer.Ordinal).ThenBy(g => g.Group.Key, StringComparer.Ordinal),
            _ => groups.OrderBy(g => g.Group.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Group.Key, StringComparer.Ordinal),
        };
        var rows = new List<InstalledOverviewRow>();
        var totalRows = 0;
        var matching = 0;
        void Add(InstalledOverviewRow row)
        {
            totalRows++;
            if (rows.Count < MaxRows) rows.Add(row);
        }
        foreach (var (group, copies) in groups)
        {
            token.ThrowIfCancellationRequested();
            matching += copies.Length;
            var states = copies.GroupBy(c => c.Ambiguous ? "ambiguous" : c.ReadOnly ? "read-only"
                : availability?.Invoke(c.Target!).TableLabel.ToLowerInvariant() ?? "not checked")
                .Select(s => $"{s.Count()} {s.Key}");
            var summary = $"{copies.Length}{(copies.Length == group.Copies.Count ? "" : " of " + group.Copies.Count)} {(group.Copies.Count == 1 ? "copy" : "copies")}"
                + (group.RefsDiffer ? "; refs differ" : "") + (group.RefsIncomplete && group.Copies.Count > 1 ? "; refs incomplete" : "") + "; " + string.Join(", ", states);
            Add(new(group.Key, group, null, (expanded.Contains(group.Key) ? "v " : "> ") + group.Name + " · " + group.Source, summary));
            if (!expanded.Contains(group.Key)) continue;
            foreach (var copy in copies)
            {
                token.ThrowIfCancellationRequested();
                var s = copy.Records[0];
                var pin = PinDisplayPolicy.From(s);
                var revision = s.Revision.RepositoryRef is { Length: > 0 } r ? "@" + r
                    : s.Revision.AuthoredVersion is { Length: > 0 } a ? "authored " + a
                    : s.Revision.DistributorVersion is { Length: > 0 } d ? "pack " + d : "ref unknown";
                var state = copy.Ambiguous ? "Ambiguous; actions disabled" : copy.ReadOnly ? "Read-only" : availability?.Invoke(s).TableLabel ?? "Not checked";
                Add(new(copy.Key, group, copy, "  " + Text(revision, 36) + " · " + copy.Location + (pin.HasPin ? " · pin " + pin.Label : ""),
                    Text(string.Join(" + ", copy.Records.SelectMany(s => s.Agents).Select(a => a.AgentId).Distinct(StringComparer.Ordinal)), 64) + "; " + state));
            }
        }
        if (totalRows > MaxRows) rows.RemoveAt(rows.Count - 1);
        var omitted = totalRows - rows.Count;
        if (omitted > 0) rows.Add(new("omitted", null, null, $"… {omitted} display rows omitted", "Narrow search or filters"));
        return new(rows, rows.Count(r => r.Copy is not null), omitted, matching);
    }

    internal static string SourceLabel(SkillSourceIdentity source)
    {
        var origin = source.Origin ?? "Unknown source";
        // URI normalization can percent-encode terminal controls before display.
        // Strip their decoded sequence too, without changing the grouping identity.
        var decoded = Uri.UnescapeDataString(origin);
        if (decoded.Any(c => c is '\x1b' or '\x07' or '\x9b' or '\x9d'))
            origin = TerminalEscapeSanitizer.Sanitize(decoded) ?? "";
        return origin.Replace("https://github.com/", "", StringComparison.Ordinal)
            + (source.Subpath is { Length: > 0 } subpath ? " / " + subpath : "");
    }
    internal static string Text(string value, int width) => DistinguishingPathLabels.Bound(DistinguishingPathLabels.Clean(value), width);
}
