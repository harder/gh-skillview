using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class IndexedOwnershipMatchingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-owner-index-");
    public void Dispose() => _root.Delete(true);

    [Fact]
    public void EmptyCandidateIndexDoesNotInspectUnrelatedSkillAddresses()
    {
        var skill = Skill("unowned", Path.Combine(_root.FullName, "unowned")) with { ResolvedPath = "\0" };
        var packages = new[] { new PackageObservation("elsewhere", Claim("legacy", _root.FullName, null).Package) }.ToImmutableArray();
        var result = LocalInventoryService.EnrichOwnership([skill], packages, TestContext.Current.CancellationToken);
        Assert.Same(skill, Assert.Single(result));
    }

    [Fact]
    public void NameBoundClaimsDoNotInspectUnrelatedSkillAddresses()
    {
        var tracked = Skill("demo", Directory.CreateDirectory(Path.Combine(_root.FullName, "demo")).FullName);
        var unrelated = Skill("other", Path.Combine(_root.FullName, "other")) with { ResolvedPath = "\0" };
        var claim = Claim("legacy", _root.FullName, null);
        var result = LocalInventoryService.EnrichOwnership([tracked, unrelated], [claim], TestContext.Current.CancellationToken);
        Assert.Single(result[0].OwnershipClaims);
        Assert.Same(unrelated, result[1]);
    }

    [Fact]
    public void UncachedComparisonReobservesAliasRetargetsInTheSameInstance()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix symlink fixture."); return; }
        var first = Directory.CreateDirectory(Path.Combine(_root.FullName, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root.FullName, "second")).FullName;
        var alias = Path.Combine(_root.FullName, "moving-alias");
        Directory.CreateSymbolicLink(alias, first);
        var cached = new InventoryPathComparison();
        var strict = new InventoryPathComparison(cacheObservations: false);
        Assert.True(cached.Same(alias, first));
        Assert.True(strict.Same(alias, first));
        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, second);
        Assert.True(cached.Same(alias, first));
        Assert.False(strict.Same(alias, first));
        Assert.True(strict.Same(alias, second));
        var region = new ManagedSkillRegion(second, "fixture", "codex", "plugin");
        Assert.True(region.ObserveCoverage(alias, strict));
        Assert.True(region.Covers(alias));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexRetainsEveryClaimAndOriginalOrder(bool useAncestorAlias)
    {
        if (useAncestorAlias && OperatingSystem.IsWindows()) return;
        var ordinary = Directory.CreateDirectory(Path.Combine(_root.FullName, "ordinary")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root.FullName, "other")).FullName;
        Directory.CreateDirectory(Path.Combine(ordinary, "demo"));
        Directory.CreateDirectory(Path.Combine(ordinary, "nested", "demo"));
        Directory.CreateDirectory(Path.Combine(other, "demo"));
        var alias = ordinary;
        if (useAncestorAlias)
        {
            alias = Path.Combine(_root.FullName, "alias");
            Directory.CreateSymbolicLink(alias, ordinary);
        }
        var skills = new[]
        {
            Skill("demo", Path.Combine(ordinary, "demo")),
            Skill("other-name", Path.Combine(other, "demo"), Path.Combine(alias, "demo")),
            Skill("nested/demo", Path.Combine(ordinary, "nested", "demo")),
            Skill("missing", Path.Combine(ordinary, "missing")),
            Skill("DEMO", Path.Combine(ordinary, "DEMO")),
        }.ToImmutableArray();
        var packages = new[]
        {
            Claim("first", alias, "demo"),
            Claim("second", ordinary, "demo"),
            Claim("elsewhere", other, "demo"),
            Claim("namespace", ordinary, "nested/demo", "gh-vercel-global"),
            Claim("missing", ordinary, "missing"),
            Claim("legacy", ordinary, null),
            Claim("invalid", ordinary, "../demo"),
        }.ToImmutableArray();
        var actual = LocalInventoryService.EnrichOwnership(skills, packages, TestContext.Current.CancellationToken);
        for (var i = 0; i < skills.Length; i++)
        {
            var skill = skills[i];
            var expected = packages.Where(entry => (entry.Package.Selector is not null || entry.Name == skill.Name)
                    && entry.Package.Describes(skill)).Select(entry => entry.Package).ToArray();
            Assert.Equal(expected, actual[i].OwnershipClaims);
        }
    }

    [Fact]
    public void CapturedReadOnlyComparisonCannotChangeFreshManagedAdmission()
    {
        if (OperatingSystem.IsWindows()) return;
        var managed = Directory.CreateDirectory(Path.Combine(_root.FullName, "managed")).FullName;
        var ordinary = Directory.CreateDirectory(Path.Combine(_root.FullName, "ordinary")).FullName;
        var alias = Path.Combine(_root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, managed);
        var region = new ManagedSkillRegion(managed, "fixture", "claude", "plugin");
        var paths = new InventoryPathComparison();
        Assert.True(region.ObserveCoverage(alias, paths));
        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, ordinary);
        Assert.False(region.Covers(alias));
        Assert.False(region.ObserveCoverage(alias, new InventoryPathComparison()));
    }

    private static PackageObservation Claim(string source, string root, string? selector, string provider = "fixture") =>
        new("demo", new SkillPackage(source, "local", null, null, null)
        { InstallationRoot = root, Selector = selector, Provider = provider });

    private static InstalledSkill Skill(string name, string path, string? exposure = null) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = Path.GetDirectoryName(path)!,
        Scope = Scope.User,
        FrontMatter = SkillFrontMatter.Empty,
        Agents = exposure is null ? [] : [new("claude", exposure, false)],
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };
}
