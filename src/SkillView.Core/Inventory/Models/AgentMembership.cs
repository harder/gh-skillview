namespace SkillView.Inventory.Models;

/// One discovery path, preserving context even when several paths share a payload.
public sealed record AgentMembership(
    string AgentId,
    string Path,
    bool IsSymlink,
    Scope? Scope = null,
    string? Context = null,
    string? DiscoveryRoot = null
);
