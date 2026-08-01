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

    /// Compact location code used by the Installed table to keep the package
    /// column visible without sacrificing scanability.
    internal static string DescribeTableLocation(InstalledSkill skill) => skill.Scope switch
    {
        Scope.User => "USR",
        Scope.Project => "PRJ",
        _ => "CUS",
    };

    /// Returns a short human-readable label for the provenance.
    internal static string DescribeProvenance(InstalledSkill skill) => skill.Provenance switch
    {
        Provenance.FsScan => "Disk",
        Provenance.CliList => "List",
        Provenance.Both => "Disk+List",
        _ => "Unknown",
    };

    /// Compact package source label used in the Installed table. Keeps the
    /// repository name intact while trimming repetitive owner prefixes.
    internal static string DescribeTablePackageSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var slash = source.IndexOf('/');
        if (slash <= 0 || slash == source.Length - 1)
        {
            return source;
        }

        var owner = source[..slash];
        var repo = source[(slash + 1)..];
        if (owner.Length <= 4)
        {
            return source;
        }

        return $"{owner[..3]}…/{repo}";
    }

    /// Returns the compact agent label string for the skill, e.g. "CLD GHC"
    /// for multi-agent installs. Delegates to <see cref="TuiHelpers.AgentBadges"/>
    /// so the label mapping stays in one place.
    internal static string DescribeAgents(InstalledSkill skill) =>
        TuiHelpers.AgentBadges(skill.Agents.Select(a => a.AgentId));
}
