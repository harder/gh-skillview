namespace SkillView.Inventory.Models;

/// One agent's view of a physical install. Multiple memberships may point to
/// the same `InstalledSkill` when agents share a directory or symlink into the
/// same canonical copy.
public sealed record AgentMembership(
    string AgentId,
    string Path,
    bool IsSymlink
);
