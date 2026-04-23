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

    public static InventorySnapshot Empty { get; } = new()
    {
        Skills = ImmutableArray<InstalledSkill>.Empty,
        ScannedRoots = ImmutableArray<ScanRoot>.Empty,
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };
}
