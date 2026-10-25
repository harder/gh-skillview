using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.Text;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledFiltersTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Scope.User)]
    [InlineData(Scope.Custom)]
    public void ScopeAloneRetainsPhysicalScopeButCombinedMembershipFacetsRequireOneMatchingExposure(Scope physicalScope)
    {
        var skill = InstalledOverviewTests.Copy("/physical/demo", "demo") with
        { Scope = physicalScope, Agents = [new("claude", "/Beta/demo", true, Scope.Project, "/work/Beta")] };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var filters = InstalledOverview.Build(snapshot, Token).Filters!;
        var agent = Assert.Single(filters.Agents).Key;
        var location = Assert.Single(filters.Locations).Key;
        foreach (var scope in Enum.GetValues<Scope>())
            Assert.Equal(LocalInventoryService.MatchesFilters(skill, scope, null), filters.Matches(skill, new(), scope, null, 0));
        Assert.True(filters.Matches(skill, new(), physicalScope, null, 0));
        Assert.True(filters.Matches(skill, new(), Scope.Project, null, 0));
        foreach (var selected in new[] { new InstalledFilterSelection(Agent: agent), new(Location: location), new(Agent: agent, Location: location) })
        {
            Assert.False(filters.Matches(skill, selected, physicalScope, null, 0));
            Assert.True(filters.Matches(skill, selected, Scope.Project, null, 0));
        }
        // Source is independent of membership; it does not remove G's legacy fallback.
        Assert.True(filters.Matches(skill, new(Source: "unknown-source"), physicalScope, null, 0));
    }

    [Fact]
    public void AgentAndLocationAndLegacyScopeMustMatchOneExposureWithoutLosingOtherConsumers()
    {
        var skill = InstalledOverviewTests.Copy("/physical/demo", "demo") with
        { Agents = [new("claude", "/home/claude/demo", true, Scope.User, "/home/alex"), new("codex", "/Beta/demo", true, Scope.Project, "/work/Beta")] };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var overview = InstalledOverview.Build(snapshot, Token);
        var filters = overview.Filters!;
        var claude = filters.Agents.Single(c => c.Label == "claude").Key;
        var codex = filters.Agents.Single(c => c.Label == "codex").Key;
        var beta = filters.Locations.Single(c => c.Label.Contains("Beta", StringComparison.Ordinal)).Key;
        Assert.False(filters.Matches(skill, new(Agent: claude, Location: beta), null, null, 1));
        Assert.False(filters.Matches(skill, new(Agent: claude), Scope.Project, null, 1));
        Assert.True(filters.Matches(skill, new(Agent: codex, Location: beta), Scope.Project, null, 1));
        var selected = overview.View(s => filters.Matches(s, new(Agent: codex, Location: beta), null, null, 1), "name",
            overview.Groups.Select(g => g.Key).ToHashSet(), null, Token);
        Assert.Equal(1, selected.MatchingCopies);
        Assert.Equal(2, selected.Rows.Single(r => r.Copy is not null).Copy!.Target!.Agents.Length);
        Assert.Equal(2, overview.Placements);
    }

    [Fact]
    public void AllContextualOwnersAndSourcesMatchWhileGroupingAndUnknownDomainsStayHonest()
    {
        var first = new SkillPackage("team/one", "github", null, null, null) { Provider = "first", SkillPath = "skills/demo" };
        var second = first with { Source = "team/two", Provider = "second" };
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with { OwnershipClaims = [first, second] };
        var managed = InstalledOverviewTests.Copy("/managed/demo", "demo") with
        { Package = new("tool-pack", "unknown", null, null, null) { Provider = "claude-plugin", ReadOnly = true, PackageVersion = "3.4" }, FrontMatter = new() { Version = "0.4" } };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill, managed] };
        var overview = InstalledOverview.Build(snapshot, Token);
        var filters = overview.Filters!;
        foreach (var choice in filters.Sources.Where(c => c.Label.Contains("team/", StringComparison.Ordinal)))
            Assert.True(filters.Matches(skill, new(Source: choice.Key), null, null, 1));
        Assert.True(filters.Matches(skill, new(Source: "conflicting-source"), null, null, 1));
        Assert.True(filters.Matches(managed, new(Source: "unknown-source"), null, null, 1));
        Assert.True(filters.Matches(managed, new(Source: "managed-owner"), null, null, 1));
        Assert.False(filters.Matches(skill, new(Source: "managed-owner"), null, null, 1));
        Assert.Equal(2, overview.Groups.Count);
        Assert.Contains(overview.Groups, g => g.Source.StartsWith("Conflicting", StringComparison.Ordinal));
        Assert.Contains(overview.Groups, g => g.Source.StartsWith("Unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(filters.Sources, c => c.Label.Contains("0.4", StringComparison.Ordinal) || c.Label.Contains("3.4", StringComparison.Ordinal));
    }

    [Fact]
    public void NoConsumersUsesOnlyDeclaredCopyScopeAndRootWithUnknownAgent()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo");
        var filters = InstalledOverview.Build(InventorySnapshot.Empty with { Skills = [skill] }, Token).Filters!;
        var agent = Assert.Single(filters.Agents);
        Assert.Equal("Unknown agent", agent.Label);
        var location = Assert.Single(filters.Locations);
        Assert.Contains("no recorded consumers", location.Label);
        Assert.True(filters.Matches(skill, new(agent.Key, location.Key), Scope.User, null, 0));
        Assert.False(filters.Matches(skill, new(agent.Key, location.Key), Scope.Project, null, 0));
    }

    [Fact]
    public void NotLoadedStaleMismatchedAndPartialIssuesNeverEstablishCleanAbsence()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo");
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var filters = InstalledOverview.Build(snapshot, Token).Filters!;
        Assert.True(filters.Matches(skill, new(Issue: "unknown"), null, null, 1));
        Assert.False(filters.Matches(skill, new(Issue: "none-observed"), null, null, 1));
        var empty = new MaintenanceQueue(snapshot.CapturedAt, false, [], []);
        var evidence = InstalledIssueEvidence.Capture(snapshot, 1, empty);
        Assert.True(filters.Matches(skill, new(Issue: "none-observed"), null, evidence, 1));
        Assert.False(filters.Matches(skill, new(Issue: "unknown"), null, evidence, 1));
        foreach (var other in new[] { evidence with { Generation = 0 }, evidence with { Snapshot = snapshot with { } }, evidence with { Partial = true } })
        {
            Assert.False(filters.Matches(skill, new(Issue: "none-observed"), null, other, 1));
            Assert.True(filters.Matches(skill, new(Issue: "unknown"), null, other, 1));
            Assert.DoesNotContain(filters.IssueChoices(other, 1), c => c.Key == "none-observed");
        }
    }

    [Fact]
    public void FindingsUseExactCapturedPhysicalSubjectAndDoNotInventCopiesOrTreatReviewAsProblem()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo");
        var sibling = skill with { ResolvedPath = "/else/demo" };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill, sibling] };
        var filters = InstalledOverview.Build(snapshot, Token).Filters!;
        MaintenanceQueueEntry Entry(InstalledSkill s, string issue) => new(issue, issue, "demo", OperationSubject.From(s), [], "observed", "reason", "review", false, false);
        var queue = new MaintenanceQueue(snapshot.CapturedAt, true, [], [Entry(skill, "Malformed"), Entry(sibling, "installation-review"),
            Entry(skill with { ResolvedPath = "/missing" }, "Ghost")]);
        var evidence = InstalledIssueEvidence.Capture(snapshot, 1, queue);
        Assert.True(filters.Matches(skill, new(Issue: "Malformed"), null, evidence, 1));
        Assert.True(filters.Matches(skill, new(Issue: "unknown"), null, evidence, 1));
        Assert.False(filters.Matches(sibling, new(Issue: "any-finding"), null, evidence, 1));
        Assert.False(filters.Matches(sibling, new(Issue: "none-observed"), null, evidence, 1));
        Assert.DoesNotContain(filters.IssueChoices(evidence, 1), c => c.Key is "Ghost" or "installation-review");
        Assert.DoesNotContain(filters.IssueChoices(evidence with { Generation = 0 }, 1), c => c.Key == "Malformed");
    }

    [Fact]
    public void FilteredGroupsShowObservedDenominatorAndCoverageRetainsOnlyCapturedRoots()
    {
        var first = InstalledOverviewTests.Copy("/home/demo", "demo", "team/tools") with { Agents = [new("claude", "/home/demo", false, Context: "/home/alex")] };
        var second = first with { ResolvedPath = "/Alpha/demo", Agents = [new("codex", "/Alpha/demo", false, Scope.Project, "/work/Alpha")] };
        var snapshot = InventorySnapshot.Empty with
        {
            Skills = [first, second], ConfiguredRoots = [new("/home", Scope.User, "claude"), new("/Gamma", Scope.Project, "codex")],
            ScannedRoots = [new("/home", Scope.User, "claude")], ConfiguredRootsComplete = false,
            Diagnostics = new() { InaccessiblePaths = ["/Gamma"], Locations = [new("/hidden", "omitted", "hidden directory excluded")] },
        };
        var overview = InstalledOverview.Build(snapshot, Token);
        var filters = overview.Filters!;
        var agent = filters.Agents.Single(c => c.Label == "claude");
        var rows = overview.View(s => filters.Matches(s, new(Agent: agent.Key), null, null, 0), "name", overview.Groups.Select(g => g.Key).ToHashSet(), null, Token);
        Assert.Equal(1, rows.MatchingCopies);
        Assert.Contains("1 of 2 observed copies", rows.Rows[0].Summary);
        Assert.Contains("Could not be scanned: `/Gamma`", filters.CoverageDetail);
        Assert.Contains("Configured: `Project /Gamma`", filters.CoverageDetail);
        Assert.Contains("Other projects are scanned only when configured", filters.CoverageDetail);
        Assert.DoesNotContain("Referenced, not scanned", filters.CoverageDetail);
        Assert.Contains("Partial", overview.Coverage);
    }

    [Fact]
    public void LabelsAreBoundedSanitizedCollisionDistinctWhileKeysRetainOriginalIdentity()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with
        { Agents = [new("claude\u001b[31m", "/a", false), new("claude", "/b", false),
            new(new string('x', 200) + "a", "/c", false), new(new string('x', 200) + "b", "/d", false),
            new(new string('界', 80) + "a", "/e", false), new(new string('界', 80) + "b", "/f", false)] };
        var filters = InstalledOverview.Build(InventorySnapshot.Empty with { Skills = [skill] }, Token).Filters!;
        Assert.Equal(6, filters.Agents.Select(c => c.Key).Distinct().Count());
        Assert.Equal(6, filters.Agents.Select(c => c.Label).Distinct().Count());
        Assert.All(filters.Agents, c => { Assert.DoesNotContain('\u001b', c.Label); Assert.InRange(c.Label.Length, 1, 64); Assert.InRange(c.Label.GetColumns(), 1, 64); });
        Assert.All(filters.Agents, c => Assert.True(filters.Matches(skill, new(Agent: c.Key), null, null, 0)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => InstalledOverview.Build(filters.Snapshot, cancel.Token));
    }
    [Fact]
    public void UnknownContextualOwnerStillMatchesWhenPrimaryTrackingSourceIsKnown()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo", "team/tools") with
        { OwnershipClaims = [new("unresolved", "unknown", null, null, null)] };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var overview = InstalledOverview.Build(snapshot, Token);
        Assert.StartsWith("team/tools", Assert.Single(overview.Groups).Source);
        Assert.True(overview.Filters!.Matches(skill, new(Source: "unknown-source"), null, null, 0));
        Assert.True(overview.Filters.Matches(skill, new(Source: overview.Filters.Sources.Single(c => c.Key.StartsWith("source:", StringComparison.Ordinal)).Key), null, null, 0));
    }

    [Fact]
    public void CoverageHasAnAggregateBudgetAndOnlyExplicitOwnerContextNamesUnscannedProjects()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with
        {
            Agents = [new("claude", "/agent/demo", true, Context: "/not-an-enumerated-project")],
            Package = new("owner", "unknown", null, null, null) { ContextRoot = "/explicit-owner-context" },
        };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var filters = InstalledOverview.Build(snapshot, Token).Filters!;
        Assert.Contains("Referenced, not scanned: `/explicit-owner-context`", filters.CoverageDetail);
        Assert.DoesNotContain("not-an-enumerated-project", filters.CoverageDetail);
        var noisy = snapshot with { Diagnostics = new() { InaccessiblePaths = [.. Enumerable.Range(0, 1000).Select(i => "/denied/" + i + new string('p', 500))] } };
        var detail = InstalledOverview.Build(noisy, Token).Filters!.CoverageDetail;
        Assert.InRange(detail.Length, 1, MaintenanceQueueFormatter.MaxCoverageCharacters);
        Assert.Contains("entries omitted", detail);
        Assert.Contains("Partial scan", detail);
    }

}
