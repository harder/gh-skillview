using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class ScanRootResolverTests
{
    [Fact]
    public void Resolves_existing_user_seeds_only()
    {
        using var temp = new TempHome();
        Directory.CreateDirectory(Path.Combine(temp.Home, ".claude", "skills"));
        Directory.CreateDirectory(Path.Combine(temp.Home, ".cursor", "skills"));
        // No .codex/skills so that seed must be dropped.

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>()));

        Assert.Contains(roots, r => r.AgentHint == "claude" && r.Scope == Scope.User);
        Assert.Contains(roots, r => r.AgentHint == "cursor" && r.Scope == Scope.User);
        Assert.DoesNotContain(roots, r => r.AgentHint == "codex");
    }

    [Fact]
    public void Resolves_user_scope_agents_skills_seed()
    {
        // gh 2.96.0: `gh skill install --agent universal --scope user` writes
        // to ~/.agents/skills, distinct from any per-agent home directory.
        using var temp = new TempHome();
        Directory.CreateDirectory(Path.Combine(temp.Home, ".agents", "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>()));

        Assert.Contains(roots, r =>
            r.Scope == Scope.User
            && r.AgentHint == "agents"
            && r.Path == Path.Combine(temp.Home, ".agents", "skills"));
    }

    [Fact]
    public void Resolves_pi_default_user_scope_root()
    {
        using var temp = new TempHome();
        var skills = Path.Combine(temp.Home, ".pi", "agent", "skills");
        Directory.CreateDirectory(skills);

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>()));

        Assert.Contains(roots, root =>
            root.Scope == Scope.User
            && root.AgentHint == "pi"
            && ScanRootResolver.NormalizeKey(root.Path) == ScanRootResolver.NormalizeKey(skills));
    }

    [Fact]
    public void Resolves_project_seeds_when_inside_git()
    {
        using var temp = new TempHome();
        var repo = Path.Combine(temp.Home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".claude", "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: repo,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>()));

        Assert.Contains(roots, r => r.Scope == Scope.Project && r.AgentHint == "claude");
    }

    [Fact]
    public void Skips_project_seeds_outside_git()
    {
        using var temp = new TempHome();
        var repo = Path.Combine(temp.Home, "not-a-repo");
        Directory.CreateDirectory(Path.Combine(repo, ".claude", "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: repo,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>()));

        Assert.DoesNotContain(roots, r => r.Scope == Scope.Project);
    }

    [Fact]
    public void Resolves_claude_user_scope_from_CLAUDE_CONFIG_DIR_when_set()
    {
        // gh ≥ 2.95 (cli/cli#13523) writes Claude user-scope skills to
        // $CLAUDE_CONFIG_DIR/skills; the scanner must look there too.
        using var temp = new TempHome();
        var configDir = Path.Combine(temp.Home, "xdg-claude");
        var configSkills = Path.Combine(configDir, "skills");
        Directory.CreateDirectory(configSkills);

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>(),
            ClaudeUserConfigDir: configDir));

        Assert.Contains(roots, r =>
            r.AgentHint == "claude"
            && r.Scope == Scope.User
            && ScanRootResolver.NormalizeKey(r.Path) == ScanRootResolver.NormalizeKey(configSkills));
    }

    [Fact]
    public void Scans_both_default_and_CLAUDE_CONFIG_DIR_claude_roots()
    {
        // The override is additive: a leftover ~/.claude/skills still surfaces.
        using var temp = new TempHome();
        Directory.CreateDirectory(Path.Combine(temp.Home, ".claude", "skills"));
        var configDir = Path.Combine(temp.Home, "xdg-claude");
        Directory.CreateDirectory(Path.Combine(configDir, "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>(),
            ClaudeUserConfigDir: configDir));

        var claudeUserRoots = roots
            .Where(r => r.AgentHint == "claude" && r.Scope == Scope.User)
            .ToArray();
        Assert.Equal(2, claudeUserRoots.Length);
    }

    [Fact]
    public void Ignores_CLAUDE_CONFIG_DIR_when_unset_or_missing()
    {
        using var temp = new TempHome();
        Directory.CreateDirectory(Path.Combine(temp.Home, ".claude", "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>(),
            ClaudeUserConfigDir: null));

        var claudeUserRoots = roots
            .Where(r => r.AgentHint == "claude" && r.Scope == Scope.User)
            .ToArray();
        Assert.Single(claudeUserRoots);
    }

    [Fact]
    public void Resolves_pi_user_scope_from_PI_CODING_AGENT_DIR_when_set()
    {
        // gh ≥ 2.99 (cli/cli#14260) writes Pi user-scope skills to
        // $PI_CODING_AGENT_DIR/skills; the scanner must look there too.
        using var temp = new TempHome();
        var configDir = Path.Combine(temp.Home, "pi-config");
        var configSkills = Path.Combine(configDir, "skills");
        Directory.CreateDirectory(configSkills);

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>(),
            PiCodingAgentDir: configDir));

        Assert.Contains(roots, root =>
            root.AgentHint == "pi"
            && root.Scope == Scope.User
            && ScanRootResolver.NormalizeKey(root.Path) == ScanRootResolver.NormalizeKey(configSkills));
    }

    [Fact]
    public void Scans_both_default_and_PI_CODING_AGENT_DIR_pi_roots()
    {
        using var temp = new TempHome();
        Directory.CreateDirectory(Path.Combine(temp.Home, ".pi", "agent", "skills"));
        var configDir = Path.Combine(temp.Home, "pi-config");
        Directory.CreateDirectory(Path.Combine(configDir, "skills"));

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: Array.Empty<string>(),
            PiCodingAgentDir: configDir));

        Assert.Equal(2, roots.Count(root => root.AgentHint == "pi" && root.Scope == Scope.User));
    }

    [Fact]
    public void Adds_custom_roots_when_they_exist()
    {
        using var temp = new TempHome();
        var custom = Path.Combine(temp.Home, "mine");
        Directory.CreateDirectory(custom);

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: new[] { custom }));

        Assert.Contains(roots, r => r.Scope == Scope.Custom && r.Path == Path.GetFullPath(custom));
    }

    [Fact]
    public void Deduplicates_overlapping_roots()
    {
        using var temp = new TempHome();
        var claude = Path.Combine(temp.Home, ".claude", "skills");
        Directory.CreateDirectory(claude);

        var resolver = new ScanRootResolver();
        var roots = resolver.Resolve(new ScanRootResolver.Options(
            CurrentDirectory: temp.Home,
            HomeDirectory: temp.Home,
            CustomRoots: new[] { claude }));

        var normalized = roots.Select(r => ScanRootResolver.NormalizeKey(r.Path)).ToArray();
        Assert.Equal(normalized.Length, normalized.Distinct().Count());
    }

    private sealed class TempHome : IDisposable
    {
        public string Home { get; }

        public TempHome()
        {
            Home = Path.Combine(Path.GetTempPath(), "skillview-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Home);
        }

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
        }
    }
}
