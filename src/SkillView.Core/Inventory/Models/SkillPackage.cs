using System.Collections.Immutable;

namespace SkillView.Inventory.Models;

/// Origin metadata for a skill installed via a "package" tool (e.g. `npx
/// skills`) that records its source repo in a `.skill-lock.json` lockfile.
/// Lets the UI group/sort by package and surface where a skill came from.
public sealed record SkillPackage(
    string Source,
    string SourceType,
    string? SourceUrl,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? UpdatedAt
)
{
    public string? LockFilePath { get; init; }
    public string? InstallationRoot { get; init; }
    public string? ContextRoot { get; init; }
    public string? SkillPath { get; init; }
    public string? RepositoryRef { get; init; }
    public string? ContentHash { get; init; }
    public string? HashKind { get; init; }
    public string? InstallationMode { get; init; }
    public string? PinnedRef { get; init; }
    public string? PluginName { get; init; }
    public int? SchemaVersion { get; init; }
    public string Provider { get; init; } = "legacy-lock";
    public bool ReadOnly { get; init; }
    public string? Diagnostic { get; init; }
    public bool IsExclusiveOwner { get; init; } = true;
    public ImmutableArray<string> CoveredRoots { get; init; } = [];
    public string? Selector { get; init; }
    public string? RecordFingerprint { get; init; }
    public string? EntryFingerprint { get; init; }
    public string? PackageVersion { get; init; }
    // Live observation only; saved schema1/2 owner JSON and hashes stay exact.
    [System.Text.Json.Serialization.JsonIgnore]
    internal ManagedSkillRegion? ManagedRegion { get; init; }

    /// A same-name lock entry is evidence only within its installation context.
    public bool Describes(InstalledSkill skill)
        => Describes(skill, OwnershipPath.Same);

    internal bool Describes(InstalledSkill skill, Func<string, string, bool> same)
    {
        var selector = Selector ?? skill.Name;
        if (selector.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl)))
            return false;
        // gh records InstallName (namespace/name) but materializes skill.Name
        // directly in the selected root. Retain both observed conventions.
        var selectors = Provider == "gh-vercel-global" && selector.Contains('/')
            ? new[] { selector, selector.Split('/')[^1] } : [selector];
        return Roots().Any(root => selectors.Any(name => same(skill.ResolvedPath, Path.Combine(root, name))
            || skill.Agents.Any(agent => same(agent.Path, Path.Combine(root, name)))));
    }

    internal IEnumerable<string> CandidatePaths(string fallbackName)
    {
        var selector = Selector ?? fallbackName;
        if (selector.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl)))
            yield break;
        foreach (var root in Roots())
        {
            yield return Path.Combine(root, selector);
            if (Provider == "gh-vercel-global" && selector.Contains('/'))
                yield return Path.Combine(root, selector.Split('/')[^1]);
        }
    }

    private IEnumerable<string> Roots() => CoveredRoots.IsEmpty
        ? InstallationRoot is { } root ? [root] : [] : CoveredRoots;
}
