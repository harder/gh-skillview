using System.Collections.Immutable;

namespace SkillView.Inventory.Models;

/// Result of a full inventory pass. Distinguishes CLI-source and scan-source
/// for downstream reporting.
public sealed record InventorySnapshot
{
    public required ImmutableArray<InstalledSkill> Skills { get; init; }
    public required ImmutableArray<ScanRoot> ScannedRoots { get; init; }
    public required bool UsedGhSkillList { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// Phase 9 — scan-level diagnostics for surfacing in doctor/rescan output.
    public bool IsPartial => !Diagnostics.InaccessiblePaths.IsEmpty || Providers.Any(provider => !provider.Succeeded);

    public ImmutableArray<ProviderObservation> Providers { get; init; } = [];
    /// Includes records whose payload is absent or excluded from skill discovery.
    public ImmutableArray<SkillPackage> OwnershipRecords { get; init; } = [];

    public ScanDiagnostics Diagnostics { get; init; } = ScanDiagnostics.Empty;

    public static InventorySnapshot Empty { get; } = new()
    {
        Skills = ImmutableArray<InstalledSkill>.Empty,
        ScannedRoots = ImmutableArray<ScanRoot>.Empty,
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };
}

/// Diagnostic information captured during an inventory scan pass.
public sealed record ScanDiagnostics
{
    /// Total wall-clock time for the filesystem scan phase.
    public TimeSpan FsScanDuration { get; init; }

    /// Total wall-clock time for the gh skill list phase (zero if unused).
    public TimeSpan GhListDuration { get; init; }

    /// Directories that couldn't be enumerated (permission denied, IO error).
    public ImmutableArray<string> InaccessiblePaths { get; init; } = ImmutableArray<string>.Empty;

    /// Broken symlinks encountered during scanning.
    public int BrokenSymlinksFound { get; init; }
    public ImmutableArray<DiscoveryLocation> Locations { get; init; } = [];

    public static ScanDiagnostics Empty { get; } = new();
}

public sealed record DiscoveryLocation(string Path, string State, string Reason);

/// An observation can establish absence only within its successfully covered roots.
public sealed record ProviderObservation(
    string Provider,
    bool Succeeded,
    ImmutableArray<string> CoveredRoots,
    DateTimeOffset CapturedAt,
    string? Error = null);
