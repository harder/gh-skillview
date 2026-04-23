using System.Collections.Immutable;

namespace SkillView.Gh;

/// Snapshot of which `gh skill` subcommands and flag tokens are available on
/// the `gh` binary SkillView is talking to. Built by scanning help-output
/// token membership (§5.4, §11.3) — we never parse help-text structure.
public sealed record CapabilityProfile
{
    public required bool SkillSubcommandPresent { get; init; }
    public required ImmutableHashSet<string> SearchFlags { get; init; }
    public required ImmutableHashSet<string> InstallFlags { get; init; }
    public required ImmutableHashSet<string> UpdateFlags { get; init; }
    public required ImmutableHashSet<string> ListFlags { get; init; }
    public required ImmutableHashSet<string> PreviewFlags { get; init; }
    public required bool ListSubcommandPresent { get; init; }

    // Capability flags consumed by higher-level services. Each one tracks a
    // specific ask in the PRD (§11.3, §5.2, §5.3).
    public bool SupportsAllowHiddenDirs => InstallFlags.Contains("--allow-hidden-dirs");
    public bool SupportsUpstream => InstallFlags.Contains("--upstream");
    public bool SupportsRepoPath => InstallFlags.Contains("--repo-path");
    public bool SupportsFromLocal => InstallFlags.Contains("--from-local");
    public bool SupportsUpdateDryRun => UpdateFlags.Contains("--dry-run");
    public bool SupportsUpdateAll => UpdateFlags.Contains("--all");
    public bool SupportsUpdateForce => UpdateFlags.Contains("--force");
    public bool SupportsUpdateUnpin => UpdateFlags.Contains("--unpin");
    public bool SupportsUpdateYes => UpdateFlags.Contains("--yes") || UpdateFlags.Contains("--non-interactive");
    public bool SupportsUpdateJson => UpdateFlags.Contains("--json");
    public bool HasSkillList => ListSubcommandPresent && ListFlags.Contains("--json");
    public bool SupportsListAgent => ListFlags.Contains("--agent");
    public bool SupportsListScope => ListFlags.Contains("--scope");
    public bool SupportsSearchJson => SearchFlags.Contains("--json");
    public bool SupportsSearchOwner => SearchFlags.Contains("--owner");
    public bool SupportsSearchLimit => SearchFlags.Contains("--limit");

    public static CapabilityProfile Empty { get; } = new()
    {
        SkillSubcommandPresent = false,
        ListSubcommandPresent = false,
        SearchFlags = ImmutableHashSet<string>.Empty,
        InstallFlags = ImmutableHashSet<string>.Empty,
        UpdateFlags = ImmutableHashSet<string>.Empty,
        ListFlags = ImmutableHashSet<string>.Empty,
        PreviewFlags = ImmutableHashSet<string>.Empty,
    };
}
