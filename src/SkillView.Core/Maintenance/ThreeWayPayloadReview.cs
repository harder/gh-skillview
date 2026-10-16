using System.Collections.Immutable;

namespace SkillView.Maintenance;

internal sealed record LocalPathChoice(string Path, string Side);

internal sealed record LocalPathReview(string Path, PayloadEntry? Recorded,
    PayloadEntry? Installed, PayloadEntry? Source, string? Selection, string Reason, PayloadEntry? Resolution = null)
{
    internal PayloadEntry? Selected => Selection switch
    {
        "installed" => Installed,
        "source" => Source,
        "resolved" => Resolution,
        _ => null
    };
    internal bool Unresolved => Selection is null;
}

internal sealed record LocalRootModeReview(int? Recorded, int? Installed, int? Source,
    string Selection, string Reason)
{
    internal int? Selected => Selection == "installed" ? Installed : Source;
}

/// Path-level authored-content choices only. Materialization must separately
/// capture bytes/metadata through held native objects and bind approved tracking.
/// A successful analysis is never an execution or filesystem authority.
internal sealed record ThreeWayPayloadReview(ImmutableArray<LocalPathReview> Paths,
    LocalRootModeReview RootMode, ImmutableArray<string> TopologyConflicts)
{
    internal bool CanMaterialize => !Paths.Any(path => path.Unresolved) && TopologyConflicts.IsEmpty;

    internal static ThreeWayPayloadReview Analyze(PayloadSnapshot recorded,
        PayloadSnapshot installed, PayloadSnapshot source,
        IReadOnlyList<LocalPathChoice>? choices = null, Func<string, string>? addressKey = null,
        IReadOnlyList<PayloadEntry>? resolvedFiles = null)
    {
        var baseline = Index(recorded); var local = Index(installed); var incoming = Index(source);
        var resolutions = Index(new PayloadSnapshot("resolution-evidence", "", "", (resolvedFiles ?? []).ToImmutableArray()));
        if (resolutions.Values.Any(entry => entry.Kind != "file"))
            throw new IOException("A resolved-file choice needs captured regular-file evidence.");
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var choice in choices ?? [])
        {
            if (choice is null || string.IsNullOrEmpty(choice.Path) || choice.Side is not ("installed" or "source" or "resolved")
                || choice.Side == "resolved" && (choice.Path == "/" || !resolutions.ContainsKey(choice.Path))
                || !selected.TryAdd(choice.Path, choice.Side))
                throw new IOException("Every path needs at most one installed/source/captured-resolution choice.");
            if (choice.Path != "/" && !baseline.ContainsKey(choice.Path)
                && !local.ContainsKey(choice.Path) && !incoming.ContainsKey(choice.Path))
                throw new IOException("A choice refers to a path absent from all three captured payloads.");
        }
        if (resolutions.Keys.Any(path => selected.GetValueOrDefault(path) != "resolved"))
            throw new IOException("Every captured resolution must be bound to its exact reviewed choice.");
        var paths = ImmutableArray.CreateBuilder<LocalPathReview>();
        foreach (var path in baseline.Keys.Concat(local.Keys).Concat(incoming.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            baseline.TryGetValue(path, out var b); local.TryGetValue(path, out var i); incoming.TryGetValue(path, out var c);
            string? side; string reason;
            if (selected.TryGetValue(path, out var explicitSide))
            { side = explicitSide; reason = "explicit-choice"; }
            else if (Same(i, c))
            { side = "installed"; reason = "installed-and-source-agree; retain-installed-presentation"; }
            else if (Same(i, b))
            { side = "source"; reason = "installed-unchanged-from-recorded"; }
            else if (Same(c, b))
            { side = "installed"; reason = "source-unchanged-from-recorded"; }
            else
            { side = null; reason = "both-sides-changed; explicit-choice-required"; }
            paths.Add(new(path, b, i, c, side, reason, resolutions.GetValueOrDefault(path)));
        }
        // Root permissions govern the entire installation. Keep the installed
        // mode unless the review explicitly selects the approved source mode.
        var rootSide = selected.GetValueOrDefault("/", "installed");
        var root = new LocalRootModeReview(recorded.RootUnixMode, installed.RootUnixMode,
            source.RootUnixMode, rootSide, selected.ContainsKey("/") ? "explicit-choice" : "preserve-installed-root-mode");
        var result = paths.ToImmutable();
        return new(result, root, CheckTopology(result, addressKey ?? (path => path)));
    }

    private static Dictionary<string, PayloadEntry> Index(PayloadSnapshot snapshot)
    {
        if (snapshot.Entries.IsDefault || snapshot.Entries.Length > PayloadSnapshot.MaxEntries)
            throw new IOException("Three-way payload entry budget exceeded or capture is unavailable.");
        var entries = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Path) || Path.IsPathRooted(entry.Path)
                || entry.Path.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\0'))
                || OperatingSystem.IsWindows() && entry.Path.Contains('\\')
                || entry.Kind is not ("file" or "directory" or "link")
                || entry.Bytes < 0 || entry.Bytes > PayloadSnapshot.MaxBytes
                || entry.Kind != "file" && entry.Bytes != 0
                || !entries.TryAdd(entry.Path, entry))
                throw new IOException("Three-way capture has an invalid, duplicate or unsupported relative entry.");
        }
        return entries;
    }

    private static bool Same(PayloadEntry? left, PayloadEntry? right) =>
        left is null ? right is null : right is not null && left.Kind == right.Kind
        && left.UnixMode == right.UnixMode && left.ComparisonHash == right.ComparisonHash;

    private static ImmutableArray<string> CheckTopology(ImmutableArray<LocalPathReview> paths,
        Func<string, string> addressKey)
    {
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var entries = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        foreach (var row in paths.Where(row => !row.Unresolved && row.Selected is not null))
        {
            var entry = row.Selected!;
            if (!entries.TryAdd(addressKey(entry.Path), entry))
                conflicts.Add($"Multiple selected paths address the same target entry: {entry.Path}");
        }
        if (entries.Count > PayloadSnapshot.MaxEntries)
            conflicts.Add("Merged payload exceeds the entry budget.");
        if (entries.Values.Sum(entry => entry.Bytes) > PayloadSnapshot.MaxBytes)
            conflicts.Add("Merged payload exceeds the byte budget.");
        foreach (var entry in entries.Values)
        {
            var slash = entry.Path.LastIndexOf('/');
            if (slash < 0) continue;
            var parent = entry.Path[..slash];
            if (!entries.TryGetValue(addressKey(parent), out var ancestor) || ancestor.Kind != "directory")
                conflicts.Add($"Selected path {entry.Path} requires its selected directory parent {parent}.");
            else if (ancestor.Path != parent)
                conflicts.Add($"Selected path {entry.Path} uses a different parent spelling than {ancestor.Path}.");
        }
        return conflicts.ToImmutable();
    }
}
