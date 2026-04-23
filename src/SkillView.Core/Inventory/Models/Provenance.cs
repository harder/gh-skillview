namespace SkillView.Inventory.Models;

/// Where an `InstalledSkill` record was observed. Reconciliation in
/// `LocalInventoryService` sets this. §7.1.A, §10.4.
public enum Provenance
{
    /// Only the filesystem scan knows about this install.
    FsScan,
    /// Only `gh skill list` knows about this install (never observed on disk).
    CliList,
    /// Both `gh skill list` and the filesystem scan agree on this install.
    Both,
}
