namespace SkillView.Gh.Models;

/// One skill discovered inside a single repository, as surfaced by
/// `gh skill install <repo>` run non-interactively (gh ≥ 2.95.0,
/// cli/cli#13548). When stdout is piped, gh renders the picker contents as
/// tab-separated `SKILL\tDESCRIPTION` rows; this is the parsed shape.
public sealed record RepoSkill(string Name, string Description);
