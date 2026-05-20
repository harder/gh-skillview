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
        Scope.User    => "User",
        Scope.Project => "Project",
        _             => "Custom",
    };

    /// Returns the agent badge string for the skill, e.g. "🤖 🖥️" for
    /// multi-agent installs. Delegates to <see cref="TuiHelpers.AgentBadges"/>
    /// so the badge icons stay in one place.
    internal static string DescribeAgents(InstalledSkill skill) =>
        TuiHelpers.AgentBadges(skill.Agents.Select(a => a.AgentId));
}
