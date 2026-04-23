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
