using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class LocalInventoryServiceMergeTests
{
    private static InstalledSkill Scan(string name, string path, Scope scope = Scope.User) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = path,
        Scope = scope,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    [Fact]
    public void Empty_cli_returns_fs_scan_unchanged()
    {
        var scanned = ImmutableArray.Create(Scan("foo", "/a/foo"));
        var merged = LocalInventoryService.Merge(scanned, ImmutableArray<GhSkillListRecord>.Empty);
        Assert.Single(merged);
        Assert.Equal(Provenance.FsScan, merged[0].Provenance);
    }

    [Fact]
    public void Matching_records_collapse_to_both_provenance()
    {
        var scanned = ImmutableArray.Create(Scan("foo", "/a/foo"));
        var gh = ImmutableArray.Create(new GhSkillListRecord { Name = "foo", Path = "/a/foo" });
        var merged = LocalInventoryService.Merge(scanned, gh);
        Assert.Single(merged);
        Assert.Equal(Provenance.Both, merged[0].Provenance);
    }

    [Fact]
    public void Cli_only_records_surface_as_CliList_provenance()
    {
        var gh = ImmutableArray.Create(new GhSkillListRecord
        {
            Name = "solo",
            Path = "/b/solo",
            Agent = "claude",
            Scope = "user",
        });
        var merged = LocalInventoryService.Merge(ImmutableArray<InstalledSkill>.Empty, gh);
        var solo = Assert.Single(merged);
        Assert.Equal(Provenance.CliList, solo.Provenance);
        Assert.Equal(Scope.User, solo.Scope);
    }

    [Fact]
    public void Fs_only_orphan_is_preserved_when_cli_is_nonempty()
    {
        var scanned = ImmutableArray.Create(Scan("foo", "/a/foo"), Scan("bar", "/a/bar"));
        var gh = ImmutableArray.Create(new GhSkillListRecord { Name = "foo", Path = "/a/foo" });
        var merged = LocalInventoryService.Merge(scanned, gh);
        Assert.Equal(2, merged.Length);
        var bar = merged.Single(m => m.Name == "bar");
        Assert.Equal(Provenance.FsScan, bar.Provenance);
        var foo = merged.Single(m => m.Name == "foo");
        Assert.Equal(Provenance.Both, foo.Provenance);
    }

    [Fact]
    public void Scope_parser_handles_known_strings()
    {
        Assert.Equal(Scope.Project, LocalInventoryService.ParseScope("project"));
        Assert.Equal(Scope.User, LocalInventoryService.ParseScope("User"));
        Assert.Equal(Scope.Custom, LocalInventoryService.ParseScope("CUSTOM"));
        Assert.Null(LocalInventoryService.ParseScope("global"));
        Assert.Null(LocalInventoryService.ParseScope(""));
        Assert.Null(LocalInventoryService.ParseScope(null));
    }
}
