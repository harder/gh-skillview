using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledOverviewTests
{
    [Fact]
    public void FivePhysicalCopiesRetainSixPlacementsAndFourSources()
    {
        var overview = InstalledOverview.Build(Fixture(), TestContext.Current.CancellationToken);
        Assert.Equal(5, overview.Copies);
        Assert.Equal(6, overview.Placements);
        Assert.Equal(4, overview.Groups.Count);
        var shared = Assert.Single(overview.Groups, g => g.Copies.Count == 2);
        Assert.True(shared.RefsDiffer);
        Assert.False(shared.RefsIncomplete);
        Assert.Equal(2, shared.Copies.Single(c => c.Path.StartsWith("/home/", StringComparison.Ordinal)).Target!.Agents.Length);
        var rows = View(overview);
        Assert.Equal(9, rows.Rows.Count);
        Assert.Contains(rows.Rows, r => r.Summary.Contains("1 read-only", StringComparison.Ordinal));
        Assert.Contains("Partial", overview.Coverage);
        Assert.DoesNotContain("Gamma", overview.Coverage); // No invented project names.
    }

    [Fact]
    public void SourceKeyAndCaseInsensitiveNameGroupButUnknownConflictAndNullKeysStaySingletons()
    {
        var first = Copy("/a/demo", "demo", "team/tools", "v1");
        var copies = new[] { first, first with { Name = "DEMO", ResolvedPath = "/b/demo" },
            first with { Name = "renamed", ResolvedPath = "/c/demo" },
            Copy("/d/demo", "demo"), Copy("/e/demo", "demo"),
            first with { ResolvedPath = "/f/demo", MetadataConflicts = ["source disagreement"] },
            first with { ResolvedPath = "/g/demo", FrontMatter = first.FrontMatter with { GithubPath = null } } };
        var overview = Build(copies);
        Assert.Equal(6, overview.Groups.Count);
        Assert.Single(overview.Groups, g => g.Copies.Count == 2);
        var singletons = overview.Groups.Where(g => g.Source.StartsWith("Unknown", StringComparison.Ordinal) || g.Source.StartsWith("Conflicting", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, singletons.Length);
        Assert.Equal(4, singletons.Select(g => g.Source).Distinct().Count());
    }

    [Fact]
    public void DuplicateIdsCountOnceRetainAllClaimsAndHaveNoTarget()
    {
        var first = Copy("/a/demo", "demo", "team/tools", "v1");
        var other = first with { Package = new("other/owner", "unknown", null, null, null) };
        var overview = Build(first, other);
        Assert.Equal(1, overview.Copies);
        Assert.Equal(1, overview.AmbiguousCopies);
        var copy = Assert.Single(Assert.Single(overview.Groups).Copies);
        Assert.Null(copy.Target);
        Assert.Equal(2, copy.Records.Count);
        Assert.Contains(View(overview).Rows, r => r.Summary.Contains("Ambiguous", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("v1", false)]
    [InlineData("v2", true)]
    public void RefDisagreementRequiresTwoKnownRefsInSameDomain(string? secondRef, bool differs)
    {
        var first = Copy("/a/demo", "demo", "team/tools", "v1");
        var other = Copy("/b/demo", "demo", "team/tools", secondRef) with
        { Package = new("team/tools", "github", null, null, null) { PackageVersion = "99.0" } };
        var group = Assert.Single(Build(first, other).Groups);
        Assert.Equal(differs, group.RefsDiffer);
        Assert.Equal(secondRef is null, group.RefsIncomplete);
    }

    [Fact]
    public void AvailabilityCountsPerCopyAndManagedPolicyDoesNotInventChecks()
    {
        var fixture = Fixture();
        var session = new UpdateAvailabilitySession();
        var request = session.Begin(fixture, 1, "context");
        session.Publish(request, 1, "context", fixture.Skills[0], new(UpdateAvailabilityStatus.Available, "new ref"));
        session.Publish(request, 1, "context", fixture.Skills[1], new(UpdateAvailabilityStatus.NoUpdate, "equal ref"));
        session.Complete(request);
        var overview = InstalledOverview.Build(fixture, TestContext.Current.CancellationToken);
        var groups = overview.View(_ => true, "name", overview.Groups.Select(g => g.Key).ToHashSet(), session.Get, TestContext.Current.CancellationToken).Rows;
        Assert.Contains(groups, r => r.Copy is null && r.Summary.Contains("1 available, 1 no update", StringComparison.Ordinal));
        Assert.All(groups.Where(r => r.Copy?.ReadOnly == false || r.Group?.Copies.All(c => !c.ReadOnly) == true),
            r => Assert.Contains("Updates:", r.Summary));
        Assert.All(groups.Where(r => r.Copy?.ReadOnly == true || r.Group?.Copies.All(c => c.ReadOnly) == true),
            r => Assert.DoesNotContain("Updates:", r.Summary));
        session.Invalidate();
        groups = overview.View(_ => true, "name", overview.Groups.Select(g => g.Key).ToHashSet(), session.Get, TestContext.Current.CancellationToken).Rows;
        Assert.Contains(groups, r => r.Summary.Contains("2 stale", StringComparison.Ordinal));
        Assert.Contains(groups, r => r.Summary.Contains("1 read-only", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalActionStatesStaySeparateFromAvailabilityForOrdinaryReadOnlyAmbiguousAndMixedGroups()
    {
        var ordinary = Copy("/ordinary/demo", "demo", "team/tools", "v1");
        var managed = ordinary with { ResolvedPath = "/managed/demo", Package = new("team/tools", "github", null, null, null) { ReadOnly = true } };
        var duplicate = Copy("/ambiguous/other", "other", "other/tools", "v1");
        var snapshot = InventorySnapshot.Empty with { Skills = [ordinary, managed, duplicate, duplicate] };
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        var calls = new List<string>();
        var rows = overview.View(_ => true, "name", overview.Groups.Select(g => g.Key).ToHashSet(), skill =>
        {
            calls.Add(skill.ResolvedPath);
            Assert.Equal(ordinary.ResolvedPath, skill.ResolvedPath);
            return new(UpdateAvailabilityStatus.Available, "fixture observation");
        }, TestContext.Current.CancellationToken).Rows;
        var ordinaryRow = Assert.Single(rows, r => r.Copy?.Path == ordinary.ResolvedPath);
        Assert.EndsWith("; Updates: Available", ordinaryRow.Summary);
        var managedRow = Assert.Single(rows, r => r.Copy?.Path == managed.ResolvedPath);
        Assert.EndsWith("; Read-only", managedRow.Summary); Assert.DoesNotContain("Updates:", managedRow.Summary);
        var ambiguousRow = Assert.Single(rows, r => r.Copy?.Ambiguous == true);
        Assert.EndsWith("; Ambiguous; actions disabled", ambiguousRow.Summary); Assert.DoesNotContain("Updates:", ambiguousRow.Summary);
        var mixed = Assert.Single(rows, r => r.Copy is null && r.Group?.Copies.Count == 2);
        Assert.EndsWith("; 1 read-only; Updates: 1 available", mixed.Summary);
        var ambiguousGroup = Assert.Single(rows, r => r.Copy is null && r.Group?.Copies.Any(c => c.Ambiguous) == true);
        Assert.EndsWith("; 1 ambiguous", ambiguousGroup.Summary); Assert.DoesNotContain("Updates:", ambiguousGroup.Summary);
        Assert.NotEmpty(calls);
        var uncheckedRows = View(overview).Rows;
        Assert.EndsWith("; Updates: Not checked", Assert.Single(uncheckedRows, r => r.Copy?.Path == ordinary.ResolvedPath).Summary);
        Assert.EndsWith("; 1 read-only; Updates: 1 not checked", Assert.Single(uncheckedRows, r => r.Copy is null && r.Group?.Copies.Count == 2).Summary);
    }

    [Fact]
    public void DisplayBudgetIncludesOmissionRowAndFilteringKeepsPhysicalCounts()
    {
        var overview = Build(Enumerable.Range(0, 5000).Select(i => Copy($"/skills/s{i}", $"s{i}")).ToArray());
        var rows = View(overview);
        Assert.Equal(InstalledOverview.MaxRows, rows.Rows.Count);
        Assert.Equal(1809, rows.OmittedRows);
        Assert.Equal("omitted", rows.Rows[^1].Key);
        Assert.Equal(5000, rows.MatchingCopies);
        var filtered = overview.View(s => s.Name == "s1", "location", overview.Groups.Select(g => g.Key).ToHashSet(), null, TestContext.Current.CancellationToken);
        Assert.Equal(1, filtered.MatchingCopies);
        Assert.Equal(2, filtered.Rows.Count);
    }

    [Fact]
    public void ExactRowBudgetFitsWithoutInventingOmissionsAndSanitizedNameCollisionsStayDistinct()
    {
        var overview = Build(Enumerable.Range(0, 4096).Select(i => Copy($"/skills/s{i}", $"s{i}")).ToArray());
        var rows = View(overview);
        Assert.Equal(InstalledOverview.MaxRows, rows.Rows.Count);
        Assert.Equal(0, rows.OmittedRows);
        var collision = Build(Copy("/a/skills/demo", "demo\u001b[31m", "team/tools"), Copy("/b/skills/demo", "demo", "team/tools"));
        Assert.Equal(2, collision.Groups.Count);
        Assert.Equal(2, collision.Groups.Select(g => g.Name + g.Source).Distinct().Count());
    }

    [Fact]
    public void SanitizedCollisionsRemainDistinguishableAndCancellationStopsProjection()
    {
        var overview = Build(Copy("/a\u001b[31m/demo", "demo"), Copy("/a/demo", "demo"));
        var labels = overview.Groups.Select(g => g.Source).ToArray();
        Assert.Equal(2, labels.Distinct().Count());
        Assert.All(labels, label => { Assert.DoesNotContain('\u001b', label); Assert.Contains('#', label); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => InstalledOverview.Build(Fixture(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => overview.View(_ => true, "name", new HashSet<string>(), null, cancellation.Token));
    }

    internal static InventorySnapshot Fixture() => InventorySnapshot.Empty with
    {
        Skills = [Copy("/home/alex/.agents/skills/demo", "demo", "team/tools", "v1") with
            { Agents = [new("claude", "/home/alex/.claude/skills/demo", true, Scope.User), new("codex", "/home/alex/.codex/skills/demo", true, Scope.User)] },
            Copy("/work/Alpha/.agents/skills/demo", "demo", "team/tools", "v2") with
            { Scope = Scope.Project, Agents = [new("claude", "/work/Alpha/.claude/skills/demo", true, Scope.Project)],
                FrontMatter = Copy("/x", "demo", "team/tools", "v2").FrontMatter with { Pinned = true, PinnedRef = "v2" } },
            Copy("/home/alex/manual/demo", "demo", "other/tools", "v9") with { Agents = [new("pi", "/home/alex/manual/demo", false)] },
            Copy("/work/Beta/.codex/skills/demo", "demo") with { Agents = [new("codex", "/work/Beta/.codex/skills/demo", false)], FrontMatter = new() { Version = "0.4" } },
            Copy("/home/alex/.claude/plugins/tools-pack/skills/review", "review") with
            { Agents = [new("claude", "/home/alex/.claude/plugins/tools-pack/skills/review", false)],
                Package = new("tools-pack", "unknown", null, null, null) { ReadOnly = true, InstallationMode = "managed-read-only", PackageVersion = "3.4" } }],
        ConfiguredRoots = [new("/home/alex", Scope.User, "universal"), new("/work/Alpha", Scope.Project, "claude"), new("/work/Beta", Scope.Project, "codex"), new("/work/Gamma", Scope.Project, "codex")],
        Diagnostics = ScanDiagnostics.Empty with { InaccessiblePaths = ["/work/Gamma"] },
    };

    internal static InstalledSkill Copy(string path, string name, string? repository = null, string? gitRef = null) => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = [],
        FrontMatter = new()
        {
            Name = name,
            Description = "Explains commands and tools for a project.",
            Upstream = repository is null ? null : "https://github.com/" + repository,
            GithubPath = repository is null ? null : "skills/demo",
            GithubRef = gitRef
        },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };
    private static InstalledOverview Build(params InstalledSkill[] skills) => InstalledOverview.Build(InventorySnapshot.Empty with { Skills = [.. skills] }, TestContext.Current.CancellationToken);
    private static InstalledOverviewRows View(InstalledOverview overview) => overview.View(_ => true, "name", overview.Groups.Select(g => g.Key).ToHashSet(), null, TestContext.Current.CancellationToken);
}
