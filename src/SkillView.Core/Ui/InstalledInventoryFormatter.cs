using SkillView.Inventory.Models;

namespace SkillView.Ui;

/// Pure, stateless formatting helpers for the installed-skills inventory view.
/// All methods are free functions over <see cref="InstalledSkill"/> — no UI
/// or Terminal.Gui dependency.
internal static class InstalledInventoryFormatter
{
    /// Returns a short human-readable label for the install location (scope).
    internal static string DescribeLocation(InstalledSkill skill) => skill.Scope switch
    {
        Scope.User => "User",
        Scope.Project => "Project",
        _ => "Custom",
    };

    /// Returns a short human-readable label for the provenance.
    internal static string DescribeProvenance(InstalledSkill skill) => skill.Provenance switch
    {
        Provenance.FsScan => "Disk",
        Provenance.CliList => "List",
        Provenance.Both => "Disk+List",
        _ => "Unknown",
    };

}
