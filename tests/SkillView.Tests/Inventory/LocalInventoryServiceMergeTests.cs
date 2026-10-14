using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class LocalInventoryServiceMergeTests
{
    [Fact]
    public void OwnershipKeepsBothContextualLocksAndLeavesUnrelatedCopyUnowned()
    {
        var root = Path.Combine(Path.GetTempPath(), "owners");
        var roots = new[] { Path.Combine(root, "one", "skills"), Path.Combine(root, "two", "skills") };
        var packages = roots.Select((path, index) => new PackageObservation("demo",
            new SkillPackage($"owner{index}/repo", "github", null, null, null)
            { InstallationRoot = path, LockFilePath = Path.Combine(Path.GetDirectoryName(path)!, ".skill-lock.json") })).ToImmutableArray();
        var enriched = LocalInventoryService.EnrichOwnership([
            Scan("demo", Path.Combine(roots[0], "demo")),
            Scan("demo", Path.Combine(roots[1], "demo")),
            Scan("demo", Path.Combine(root, "custom", "demo"))], packages, TestContext.Current.CancellationToken);
        Assert.Equal("owner0/repo", enriched[0].Package!.Source);
        Assert.Equal("owner1/repo", enriched[1].Package!.Source);
        Assert.Single(enriched[0].OwnershipClaims);
        Assert.Single(enriched[1].OwnershipClaims);
        Assert.Null(enriched[2].Package);
        Assert.Empty(enriched[2].OwnershipClaims);
    }

    [Fact]
    public void EmptyGhTrackingDoesNotInventChangedFilesystemMetadata()
    {
        var skill = Scan("demo", Path.Combine(Path.GetTempPath(), "demo"));
        var merged = Assert.Single(LocalInventoryService.Merge([skill],
            [new GhSkillListRecord { Name = "demo", Path = skill.ResolvedPath, Version = "", Repo = "", GithubTreeSha = "" }]));
        Assert.Null(merged.FrontMatter.Upstream);
        Assert.Null(merged.FrontMatter.GithubRef);
        Assert.Null(merged.TreeSha);
    }

    [Fact]
    public void MergeWithCancellation_HonorsPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LocalInventoryService.MergeWithCancellation(
                ImmutableArray<InstalledSkill>.Empty,
                ImmutableArray<GhSkillListRecord>.Empty,
                cancellation.Token));
    }

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
    public void Ancestor_symlink_aliases_collapse_to_one_install_on_unix()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        var root = Directory.CreateTempSubdirectory("skillview-merge-alias-");
        try
        {
            var target = Path.Combine(root.FullName, "target");
            var alias = Path.Combine(root.FullName, "alias");
            var skill = Path.Combine(target, "foo");
            Directory.CreateDirectory(skill);
            Directory.CreateSymbolicLink(alias, target);

            var scanned = ImmutableArray.Create(Scan("foo", skill));
            var gh = ImmutableArray.Create(new GhSkillListRecord
            {
                Name = "foo",
                Path = Path.Combine(alias, "foo"),
            });

            var merged = LocalInventoryService.Merge(scanned, gh);

            Assert.Equal(Provenance.Both, Assert.Single(merged).Provenance);
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
