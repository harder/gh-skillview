namespace SkillView.Inventory.Models;

/// A resolved directory that the scanner will visit. `AgentHint` is the agent
/// whose skill directory this root is (e.g. `claude` for `~/.claude/skills`);
/// `null` means the agent should be inferred per-install (custom roots).
public sealed record ScanRoot(
    string Path,
    Scope Scope,
    string? AgentHint
);
