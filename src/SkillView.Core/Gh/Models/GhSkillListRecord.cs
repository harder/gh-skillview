using System.Collections.Immutable;

namespace SkillView.Gh.Models;

/// SkillView-internal projection of a `gh skill list --json` record. The
/// upstream schema is defined by cli/cli PR #13418 (closing #13245) and the
/// canonical field set is:
///
///   skillName · hosts[] · scope · sourceURL · version · pinned · path
///
/// Where `scope` is "project" | "user" | "custom" and `hosts` is always an
/// array of agent IDs (empty when `--dir` is used). `sourceURL` is a full URL
/// for GitHub-installed skills, or a local filesystem path for local-source
/// skills.
///
/// The adapter parses tolerantly (see <see cref="GhSkillListAdapter"/>) and
/// keeps a small set of legacy/alternate JSON key names as fallbacks in case
/// the upstream schema shifts before the PR merges.
public sealed record GhSkillListRecord
{
    /// Skill name as reported by gh — read from JSON key `skillName` (or
    /// legacy `name` / `skill_name`).
    public string? Name { get; init; }

    /// Filesystem path to the skill directory — read from JSON key `path`
    /// (or legacy `installPath` / `install_path` / `resolvedPath`).
    public string? Path { get; init; }

    /// Canonical filesystem path after symlink resolution. Kept for
    /// SkillView's own merge logic; upstream `gh skill list` does not emit
    /// this distinctly from `path`.
    public string? ResolvedPath { get; init; }

    /// Full source URL — read from JSON key `sourceURL`. For GitHub
    /// installs this is `https://github.com/{owner}/{repo}`; for local
    /// installs (--dir / local-path metadata) this is the filesystem path.
    public string? SourceUrl { get; init; }

    /// Legacy compact repo identifier (e.g. `owner/repo`). Read from
    /// `repo` / `repository` if upstream emits it; otherwise derived from
    /// <see cref="SourceUrl"/> by the consumer.
    public string? Repo { get; init; }

    /// Singular agent — legacy field. Modern upstream emits an array via
    /// <see cref="Hosts"/>; this stays for back-compat with older parsing.
    public string? Agent { get; init; }

    /// Installation scope: "project", "user", or "custom" (the last for
    /// `--dir`-driven scans). SkillView maps this to <c>Scope</c> in the
    /// merge step.
    public string? Scope { get; init; }

    /// Short ref / version string from `version` (upstream applies
    /// `discovery.ShortRef`, e.g. `refs/tags/v1.0.0` → `v1.0.0`).
    public string? Version { get; init; }

    /// SKILL.md frontmatter tree SHA — not currently emitted by upstream
    /// `gh skill list`. Populated only when a legacy/alternative JSON key
    /// is present.
    public string? GithubTreeSha { get; init; }

    public bool Pinned { get; init; }

    /// Whether the install is a symlink to a shared location. Upstream
    /// `gh skill list` does not surface this — SkillView's filesystem scan
    /// owns the determination. Kept on the record so legacy JSON payloads
    /// that emit `isSymlink` are still read.
    public bool IsSymlink { get; init; }

    /// Agent IDs the skill is registered with. Primary source is the
    /// upstream `hosts` array; falls back to legacy `agents` array.
    public ImmutableArray<string> Hosts { get; init; } = ImmutableArray<string>.Empty;

    /// Legacy alias for <see cref="Hosts"/>. Kept on the record so any
    /// SkillView call site that referenced `Agents` continues to work, but
    /// the parser writes to <see cref="Hosts"/> first.
    public ImmutableArray<string> Agents
    {
        get => Hosts;
        init => Hosts = value;
    }
}
