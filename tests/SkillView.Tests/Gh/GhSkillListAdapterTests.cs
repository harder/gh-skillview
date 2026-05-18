using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillListAdapterTests
{
    // Upstream-canonical payload per cli/cli PR #13418 (closes #13245).
    // Field set: skillName, hosts[], scope, sourceURL, version, pinned, path.
    [Fact]
    public void Parses_upstream_canonical_payload()
    {
        const string json = """
            [
              {
                "skillName": "code-review",
                "hosts": ["claude-code"],
                "scope": "user",
                "sourceURL": "https://github.com/monalisa/skills-repo",
                "version": "v2.0.0",
                "pinned": true,
                "path": "/home/m/.claude/skills/code-review"
              }
            ]
            """;

        var records = GhSkillListAdapter.Parse(json);

        Assert.Single(records);
        var r = records[0];
        Assert.Equal("code-review", r.Name);
        Assert.Equal("user", r.Scope);
        Assert.Equal(new[] { "claude-code" }, r.Hosts);
        Assert.Equal("https://github.com/monalisa/skills-repo", r.SourceUrl);
        Assert.Equal("v2.0.0", r.Version);
        Assert.True(r.Pinned);
        Assert.Equal("/home/m/.claude/skills/code-review", r.Path);
    }

    // Shared-install directories like `.agents/skills` are deduped by upstream
    // into a single record with multiple `hosts`.
    [Fact]
    public void Parses_shared_install_with_multiple_hosts()
    {
        const string json = """
            [{
              "skillName": "git-commit",
              "hosts": ["claude-code", "cursor"],
              "scope": "project",
              "sourceURL": "https://github.com/monalisa/skills-repo",
              "version": "v1.0.0",
              "pinned": false,
              "path": "/repo/.agents/skills/git-commit"
            }]
            """;

        var records = GhSkillListAdapter.Parse(json);

        Assert.Equal(new[] { "claude-code", "cursor" }, records[0].Hosts);
        Assert.Equal("project", records[0].Scope);
    }

    // `--dir` (custom) scans emit scope="custom" and an empty hosts array.
    [Fact]
    public void Parses_custom_dir_record_with_empty_hosts()
    {
        const string json = """
            [{
              "skillName": "local-helper",
              "hosts": [],
              "scope": "custom",
              "sourceURL": "/src/local-helper",
              "version": "",
              "pinned": false,
              "path": "/repo/custom-skills/local-helper"
            }]
            """;

        var records = GhSkillListAdapter.Parse(json);

        Assert.Equal("custom", records[0].Scope);
        Assert.Empty(records[0].Hosts);
        Assert.Equal("/src/local-helper", records[0].SourceUrl);
    }

    // Legacy-shape payloads (older keys) still parse; the adapter keeps
    // alternate keys as defensive fallbacks against schema drift.
    [Fact]
    public void Parses_legacy_field_names_as_fallback()
    {
        const string json = """
            [
              { "name": "foo", "path": "/a/foo", "repo": "o/r", "pinned": true,
                "agents": ["claude", "copilot"] },
              { "name": "bar", "path": "/a/bar", "repo": "o/r2" }
            ]
            """;

        var records = GhSkillListAdapter.Parse(json);

        Assert.Equal(2, records.Length);
        Assert.Equal("foo", records[0].Name);
        Assert.True(records[0].Pinned);
        Assert.Equal("/a/foo", records[0].Path);
        Assert.Equal("o/r", records[0].Repo);
        Assert.Equal(new[] { "claude", "copilot" }, records[0].Hosts);
    }

    [Fact]
    public void Parses_object_wrapped_array()
    {
        const string json = """
            { "skills": [ { "skillName": "foo", "path": "/a/foo" } ] }
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Single(records);
        Assert.Equal("foo", records[0].Name);
    }

    [Fact]
    public void Accepts_multiple_alternative_field_names_for_sha()
    {
        const string json = """
            [
              { "skillName": "a", "github-tree-sha": "sha1" },
              { "skillName": "b", "treeSha": "sha2" },
              { "skillName": "c", "sha": "sha3" }
            ]
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Equal("sha1", records[0].GithubTreeSha);
        Assert.Equal("sha2", records[1].GithubTreeSha);
        Assert.Equal("sha3", records[2].GithubTreeSha);
    }

    [Fact]
    public void Empty_or_garbage_yields_empty_array()
    {
        Assert.Empty(GhSkillListAdapter.Parse(""));
        Assert.Empty(GhSkillListAdapter.Parse("not json"));
        Assert.Empty(GhSkillListAdapter.Parse("42"));
    }

    // Belt-and-braces: when both `hosts` and `agents` are present (transition
    // window), upstream-canonical `hosts` wins.
    [Fact]
    public void Hosts_wins_over_legacy_agents_when_both_present()
    {
        const string json = """
            [{
              "skillName": "x",
              "hosts": ["claude-code"],
              "agents": ["should-be-ignored"]
            }]
            """;

        var records = GhSkillListAdapter.Parse(json);

        Assert.Equal(new[] { "claude-code" }, records[0].Hosts);
    }

    // Same idea for `skillName` vs legacy `name`.
    [Fact]
    public void SkillName_wins_over_legacy_name_when_both_present()
    {
        const string json = """
            [{ "skillName": "canonical", "name": "legacy" }]
            """;
        var records = GhSkillListAdapter.Parse(json);
        Assert.Equal("canonical", records[0].Name);
    }
}
