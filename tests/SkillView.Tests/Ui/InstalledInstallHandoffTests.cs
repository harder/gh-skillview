using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Input;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledInstallHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactNativeCopyRevealedAndFiltersClearedOnlyWhenHiding(bool hiding)
    {
        using var fixture = new Fixture();
        var target = fixture.Skill(fixture.Root, "materialized-name");
        var unrelated = fixture.Skill(fixture.Root, "peer", "other/repo");
        var snapshot = fixture.Snapshot(target, unrelated);
        var handoff = fixture.Resolve(snapshot);
        var loads = 0;
        var mutations = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(snapshot); }, (_, _, _) => { mutations++; return Task.CompletedTask; }, () => { });
        view.LoadSeeded(snapshot, handoff.Overview);
        view.SetFilterTextForTests(hiding ? "peer" : "demo");
        view.SelectFilter(0, handoff.Overview.Filters!.Agents.Single(choice => choice.Label.Contains("universal", StringComparison.OrdinalIgnoreCase)).Key);
        view.SelectFilter(1, handoff.Overview.Filters!.Locations.First(choice => choice.Label.Contains("materialized-name", StringComparison.Ordinal)).Key);
        view.SelectFilter(2, handoff.Overview.Filters!.Sources.Single(choice => choice.Label.Contains("owner/repo", StringComparison.Ordinal)).Key);
        view.SelectFilter(3, "unknown");
        view.FocusList();
        view.NewKeyDownEvent(new Key('G')); // Compatible User scope.
        view.NewKeyDownEvent(new Key('s')); // Keep Source sort through either branch.
        if (hiding) view.NewKeyDownEvent(new Key('P')); // Pinned only hides an unpinned target too.
        var selectedLabels = view.SubViews.OfType<Terminal.Gui.Views.Button>().Where(button => button.Text.Contains(":", StringComparison.Ordinal)).Select(button => button.Text).ToArray();
        Assert.Equal(4, selectedLabels.Length);
        if (!hiding) Assert.True(view.RowsForTests.Any(row => row.Copy?.Target == target), view.DetailTextForTests);
        Assert.True(view.RevealInstall(handoff));
        Assert.Same(target, view.GetSelectedSkill());
        Assert.True(view.DetailOpenForTests);
        Assert.Contains("materialized-name", view.DetailTextForTests);
        Assert.Equal(hiding, view.FooterTextForTests.Contains("Adjusted Installed filters", StringComparison.Ordinal));
        if (hiding) Assert.Equal("Adjusted Installed filters to show this result.", view.FooterTextForTests);
        // Revealing never launches another load or starts a copy operation.
        Assert.Equal(0, loads);
        Assert.Equal(0, mutations);
        Assert.Equal(hiding ? new[] { "demo", "demo" } : ["demo"], view.VisibleSkillNamesForTests);
        Assert.Equal(hiding ? "" : "demo", view.SubViews.OfType<Terminal.Gui.Views.TextField>().Single().Text);
        Assert.Contains(view.SubViews.OfType<Terminal.Gui.Views.Label>(), label => label.Text == (hiding ? "P:any G:any s:S" : "P:any G:user s:S"));
        if (!hiding) Assert.Equal(selectedLabels, view.SubViews.OfType<Terminal.Gui.Views.Button>().Where(button => button.Text.Contains(":", StringComparison.Ordinal)).Select(button => button.Text).ToArray());
    }

    [Fact]
    public void SharedConsumersAreOneExactCopyAndMultiplePhysicalCopiesSelectOnlySourceGroup()
    {
        using var fixture = new Fixture();
        var one = fixture.Skill(fixture.Root, "one");
        one = one with { Agents = [.. one.Agents, new("codex", one.ResolvedPath, false, Scope: Scope.User)] };
        one = fixture.Observe(one);
        Assert.Single(fixture.Resolve(fixture.Snapshot(one)).CopyKeys);
        var secondRoot = Path.Combine(fixture.Root, "other-placement");
        var two = fixture.Skill(secondRoot, "two");
        var handoff = fixture.Resolve(fixture.Snapshot(one, two), [fixture.Root, secondRoot]);
        Assert.Equal(2, handoff.CopyKeys.Count);
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No load"), (_, _, _) => throw new InvalidOperationException("No mutation"), () => { });
        Assert.True(view.RevealInstall(handoff));
        Assert.Equal(handoff.GroupKey, view.SelectedKeyForTests);
        Assert.Null(view.GetSelectedSkill());
        Assert.False(view.DetailOpenForTests);
    }

    [Theory]
    [InlineData("namesake")]
    [InlineData("wrong-subpath")]
    [InlineData("owner-conflict")]
    [InlineData("unknown")]
    [InlineData("conflict")]
    [InlineData("partial")]
    [InlineData("omitted")]
    [InlineData("legacy")]
    [InlineData("missing-destination")]
    [InlineData("changed-native")]
    [InlineData("duplicate")]
    public void UnverifiedEvidenceNeverFallsBackToPeer(string reason)
    {
        using var fixture = new Fixture();
        var target = fixture.Skill(fixture.Root, "exact");
        target = reason switch
        {
            "namesake" => target with { FrontMatter = target.FrontMatter with { Upstream = "https://github.com/other/repo" } },
            "wrong-subpath" => target with { FrontMatter = target.FrontMatter with { GithubPath = "other/source" } },
            "owner-conflict" => target with { OwnershipClaims = [new("other/repo", "github", null, null, null) { SkillPath = "skills/source" }] },
            "unknown" => target with { FrontMatter = new() },
            "conflict" => target with { MetadataConflicts = ["source conflict"] },
            _ => target,
        };
        var snapshot = fixture.Snapshot(target);
        snapshot = reason switch
        {
            "partial" => snapshot with { Diagnostics = new() { InaccessiblePaths = [fixture.Root] } },
            "omitted" => snapshot with { ScannedRoots = [], Providers = [] },
            "duplicate" => snapshot with { Skills = [target, target] },
            _ => snapshot,
        };
        if (reason == "changed-native")
        {
            var admitted = fixture.Resolve(snapshot);
            Assert.False(admitted.BrowseOnly);
            var exactCopy = Assert.Single(Assert.Single(admitted.Overview.Groups).Copies);
            Assert.Same(target, exactCopy.Target);
            Assert.Equal([exactCopy.Key], admitted.CopyKeys);
            // Retain the observed directory so its native ID cannot be reused;
            // replace its exact path without changing the authored payload.
            var retained = target.ResolvedPath + "-retained";
            Directory.Move(target.ResolvedPath, retained);
            var replacement = fixture.Skill(target.ScanRoot, "exact");
            Assert.Equal(target.ResolvedPath, replacement.ResolvedPath);
            Assert.NotEqual(target.ObservedSubject!.NativeObject.ObjectId, replacement.ObservedSubject!.NativeObject.ObjectId);
            Assert.Equal(File.ReadAllBytes(Path.Combine(retained, "SKILL.md")), File.ReadAllBytes(Path.Combine(replacement.ResolvedPath, "SKILL.md")));
        }
        var roots = reason == "missing-destination" ? new[] { fixture.Root, Path.Combine(fixture.Root, "absent") } : [fixture.Root];
        if (reason == "missing-destination")
        {
            Directory.CreateDirectory(roots[1]);
            snapshot = snapshot with
            {
                ScannedRoots = snapshot.ScannedRoots.Add(new(roots[1], Scope.User, "universal")),
                ConfiguredRoots = snapshot.ConfiguredRoots.Add(new(roots[1], Scope.User, "universal")),
                Providers = snapshot.Providers.Add(new("gh", true, [roots[1]], DateTimeOffset.UtcNow))
            };
        }
        var refusal = Assert.Throws<IOException>(() => fixture.Resolve(snapshot, roots, reason != "legacy"));
        if (reason == "missing-destination") Assert.Contains("every installation folder", refusal.Message);
        if (reason == "changed-native") Assert.Equal("The installed physical copy changed during verification; refresh Installed before choosing it.", refusal.Message);
    }

    [Theory]
    [InlineData("../bad")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    public void InvalidSourcePathsAreNotAnchors(string path) => Assert.Null(InstallNavigationEvidence.SourceAnchor("owner/repo", path));

    [Theory]
    [InlineData("custom")]
    [InlineData("all")]
    [InlineData("local")]
    [InlineData("upstream")]
    public void UnsupportedInstallModesHaveNoNavigationEvidence(string mode)
    {
        using var fixture = new Fixture();
        var options = new GhSkillInstallService.Options(Agents: ["universal"], Scope: "user", Path: mode == "custom" ? fixture.Root : null,
            All: mode == "all", FromLocal: mode == "local", Upstream: mode == "upstream")
        { NavigationSource = fixture.Anchor };
        Assert.Null(InstallNavigationEvidence.Capture(options, [fixture.Root], fixture.Runner));
    }

    [Theory]
    [InlineData("codex", "modern")]
    [InlineData("codex", "legacy")]
    [InlineData("codex", "both")]
    [InlineData("claude-code", "default")]
    [InlineData("claude-code", "override")]
    [InlineData("pi", "default")]
    [InlineData("pi", "override")]
    public async Task AlternativeFoldersBrowseVerifiedGroupWithoutCopyFocus(string agent, string placement)
    {
        using var fixture = new Fixture(overrides: true);
        var (evidence, folders) = await fixture.AgentFolders(agent);
        Assert.False(evidence.ExactPlacement);
        var alternatives = Assert.Single(folders);
        Assert.Equal(2, alternatives.Count);
        var indices = placement == "both" ? new[] { 0, 1 } : [placement is "override" or "legacy" ? 1 : 0];
        var skills = indices.Select(index => fixture.Skill(alternatives[index], "copy", agent: agent)).ToArray();
        var snapshot = fixture.Snapshot(skills) with { ConfiguredRoots = evidence.Roots.Select(root => new ScanRoot(root, Scope.User, agent)).ToImmutableArray() };
        var handoff = fixture.ResolveAlternatives(snapshot, evidence, folders);
        Assert.True(handoff.BrowseOnly);
        Assert.Equal(skills.Length, handoff.CopyKeys.Count);
        var reviews = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No duplicate load"), (_, _, _) => throw new InvalidOperationException("No mutation"),
            () => { }, onReviewUpdate: _ => reviews++);
        Assert.True(view.RevealInstall(handoff));
        Assert.Equal(handoff.GroupKey, view.SelectedKeyForTests);
        Assert.Equal(skills.Length + 1, view.RowsForTests.Count);
        Assert.Null(view.GetSelectedSkill());
        Assert.False(view.DetailOpenForTests);
        Assert.Contains("Showing verified copies of this skill; choose a copy to review it.", view.FooterTextForTests);
        Assert.False(view.SubViews.OfType<Terminal.Gui.Views.Button>().Single(button => button.Text == "More actions").Enabled);
        view.ReviewSelectedUpdate();
        Assert.Equal(0, reviews);
        Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Message.StartsWith("exec:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodexProjectStillOpensTheExactSingleCopy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses the isolated POSIX Git context for project-root discovery.");
        using var fixture = new Fixture();
        var options = new GhSkillInstallService.Options(Agents: ["codex"], Scope: "project", WorkingDirectory: fixture.Root)
        { NavigationSource = fixture.Anchor };
        var roots = await GhPlacementCatalog.DestinationsAsync(options, fixture.Runner, TestContext.Current.CancellationToken);
        var evidence = InstallNavigationEvidence.Capture(options, roots, fixture.Runner)!;
        var folders = await SkillViewWorkflowCoordinator.CaptureInstallAgentFoldersAsync(evidence, fixture.Runner, TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(folders));
        var target = fixture.Skill(roots[0], "copy", agent: "codex");
        target = fixture.Observe(target with { Scope = Scope.Project, Agents = [new("codex", target.ResolvedPath, false, Scope: Scope.Project)] });
        var snapshot = fixture.Snapshot(target) with { ScannedRoots = [new(roots[0], Scope.Project, "codex")], ConfiguredRoots = [new(roots[0], Scope.Project, "codex")] };
        var handoff = fixture.ResolveAlternatives(snapshot, evidence, folders);
        Assert.False(handoff.BrowseOnly);
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No duplicate load"), (_, _, _) => throw new InvalidOperationException("No mutation"), () => { });
        Assert.True(view.RevealInstall(handoff));
        Assert.Same(target, view.GetSelectedSkill());
        Assert.True(view.DetailOpenForTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlternativeFoldersStillRequireEachSingleFolderAgent(bool includeClaude)
    {
        using var fixture = new Fixture();
        var (evidence, folders) = await fixture.AgentFolders("codex", "claude-code");
        Assert.Equal(2, folders[0].Count);
        var claudeRoot = Assert.Single(folders[1]);
        var codex = fixture.Skill(folders[0][0], "copy", agent: "codex");
        var skills = includeClaude ? new[] { codex, fixture.Skill(claudeRoot, "copy", agent: "claude-code") } : [codex];
        var snapshot = fixture.Snapshot(skills);
        if (includeClaude) Assert.True(fixture.ResolveAlternatives(snapshot, evidence, folders).BrowseOnly);
        else Assert.Equal("Not all requested locations were verified.", Assert.Throws<IOException>(() =>
            fixture.ResolveAlternatives(snapshot, evidence, folders)).Message);
    }

    [Theory]
    [InlineData("unscanned")]
    [InlineData("provider")]
    [InlineData("partial")]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("unknown")]
    [InlineData("native")]
    [InlineData("ambiguous")]
    [InlineData("groups")]
    public async Task AlternativesRetainCoverageSourceOwnerAndNativeRefusals(string reason)
    {
        using var fixture = new Fixture();
        var (evidence, folders) = await fixture.AgentFolders("codex");
        var target = fixture.Skill(folders[0][0], "copy");
        target = reason switch
        {
            "owner" => target with { OwnershipClaims = [new("other/repo", "github", null, null, null) { SkillPath = "skills/source" }] },
            "source" => target with { FrontMatter = target.FrontMatter with { GithubPath = "other/source" } },
            "unknown" => target with { FrontMatter = new() },
            _ => target,
        };
        var snapshot = fixture.Snapshot(target);
        snapshot = reason switch
        {
            "unscanned" => snapshot with { ScannedRoots = [] },
            "provider" => snapshot with { Providers = [] },
            "partial" => snapshot with { ConfiguredRootsComplete = false },
            "ambiguous" => snapshot with { Skills = [target, target] },
            _ => snapshot,
        };
        if (reason == "native")
        {
            Assert.True(fixture.ResolveAlternatives(snapshot, evidence, folders).BrowseOnly);
            // A child addition need not change a Windows directory's observed
            // timestamps. Keep the original alive and replace the exact copy.
            var retained = target.ResolvedPath + "-retained";
            Directory.Move(target.ResolvedPath, retained);
            var replacement = fixture.Skill(target.ScanRoot, "copy");
            Assert.NotEqual(target.ObservedSubject!.NativeObject.ObjectId, replacement.ObservedSubject!.NativeObject.ObjectId);
            Assert.Equal(File.ReadAllBytes(Path.Combine(retained, "SKILL.md")), File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md")));
        }
        if (reason == "groups")
        {
            var other = fixture.Observe(fixture.Skill(folders[0][1], "other") with { Name = "different-name" });
            snapshot = fixture.Snapshot(target, other);
        }
        var error = Assert.Throws<IOException>(() => fixture.ResolveAlternatives(snapshot, evidence, folders));
        if (reason == "native") Assert.Contains("physical copy changed", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupBrowsingAlsoVerifiesOlderChildrenOutsideRequestedFolders(bool covered)
    {
        using var fixture = new Fixture();
        var (evidence, folders) = await fixture.AgentFolders("codex");
        var target = fixture.Skill(folders[0][0], "copy");
        var older = fixture.Skill(Path.Combine(fixture.Root, "other-context"), "copy");
        var snapshot = fixture.Snapshot(target, older);
        if (!covered) snapshot = snapshot with { ScannedRoots = snapshot.ScannedRoots.Where(root => root.Path != older.ScanRoot).ToImmutableArray() };
        if (covered) Assert.Equal(2, fixture.ResolveAlternatives(snapshot, evidence, folders).CopyKeys.Count);
        else Assert.Throws<IOException>(() => fixture.ResolveAlternatives(snapshot, evidence, folders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseOnlyPreservesCompatibleFiltersOrClearsHidingFilters(bool hiding)
    {
        using var fixture = new Fixture();
        var (evidence, folders) = await fixture.AgentFolders("codex");
        var target = fixture.Skill(folders[0][0], "copy");
        var handoff = fixture.ResolveAlternatives(fixture.Snapshot(target), evidence, folders);
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No load"), (_, _, _) => throw new InvalidOperationException("No mutation"), () => { });
        view.LoadSeeded(handoff.Snapshot, handoff.Overview);
        view.SetFilterTextForTests(hiding ? "hidden" : "demo");
        if (hiding) Assert.Empty(view.RowsForTests);
        Assert.True(view.RevealInstall(handoff));
        Assert.Equal(hiding ? "" : "demo", view.SubViews.OfType<Terminal.Gui.Views.TextField>().Single().Text);
        Assert.Equal(handoff.GroupKey, view.SelectedKeyForTests);
        Assert.Null(view.GetSelectedSkill());
        Assert.False(view.DetailOpenForTests);
        Assert.Equal(2, view.RowsForTests.Count);
        Assert.Equal(handoff.GroupKey, view.RowsForTests[0].Key);
        Assert.Equal(Assert.Single(handoff.CopyKeys), view.RowsForTests[1].Key);
        Assert.Equal((hiding ? "Adjusted Installed filters to show this result. " : "")
            + "Showing verified copies of this skill; choose a copy to review it.", view.FooterTextForTests);
    }

    [Fact]
    public void RepositoryBrowseShowsAllVerifiedSourcesWithoutSelectingAnArbitraryCopy()
    {
        using var fixture = new Fixture();
        var one = fixture.Skill(fixture.Root, "one");
        var two = fixture.Observe(fixture.Skill(fixture.Root, "two") with
        {
            Name = "second",
            FrontMatter = new() { Upstream = "https://github.com/owner/repo", GithubPath = "skills/second" },
        });
        var other = fixture.Skill(fixture.Root, "other", "other/repo");
        var snapshot = fixture.Snapshot(one, two, other);
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        var loads = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(snapshot); }, (_, _, _) => throw new InvalidOperationException("No mutation"), () => { });
        Assert.True(view.RevealInstall(new(snapshot, overview, "", []) { BrowseOnly = true, BrowseRepository = "https://github.com/owner/repo" }));
        Assert.Equal(2, view.RowsForTests.Count);
        Assert.All(view.RowsForTests, row => Assert.NotNull(row.Group));
        Assert.Null(view.GetSelectedSkill());
        Assert.Null(view.SelectedKeyForTests);
        Assert.False(view.DetailOpenForTests);
        Assert.Contains(view.SubViews.OfType<Terminal.Gui.Views.Label>(), label => label.Visible && label.Text == "Repository: owner/repo — Clear shows all");
        Assert.Equal(0, loads);
    }

    [Fact]
    public void ExactCrossRepositoryHandoffClearsBrowseRestrictionButRejectedHandoffDoesNot()
    {
        using var fixture = new Fixture();
        var one = fixture.Skill(fixture.Root, "one");
        var other = fixture.Skill(fixture.Root, "other", "other/repo");
        var snapshot = fixture.Snapshot(one, other);
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No load"), (_, _, _) => throw new InvalidOperationException("No mutation"), () => { });
        Assert.True(view.RevealInstall(new(snapshot, overview, "", []) { BrowseOnly = true, BrowseRepository = "https://github.com/owner/repo" }));
        var indicator = view.SubViews.OfType<Label>().Single(label => label.Text.StartsWith("Repository:", StringComparison.Ordinal));
        var exact = InstalledInstallHandoffResolver.Resolve(new(InstallNavigationEvidence.SourceAnchor("other/repo", "skills/source")!,
            [fixture.Root], "user", "context", true, ["universal"]), snapshot, overview, TestContext.Current.CancellationToken);
        Assert.False(view.RevealInstall(exact with { GroupKey = "missing" }));
        Assert.True(indicator.Visible);
        Assert.Contains("owner/repo", indicator.Text);
        Assert.True(view.RevealInstall(exact));
        Assert.Same(other, view.GetSelectedSkill());
        Assert.False(indicator.Visible);
        Assert.Empty(indicator.Text);
        Assert.True(view.DetailOpenForTests);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "skillview-handoff-" + Guid.NewGuid().ToString("N"));
        internal Logger Logger { get; } = new(LogLevel.Debug);
        internal ProcessRunner Runner { get; }
        internal SkillSourceIdentity Anchor => InstallNavigationEvidence.SourceAnchor("owner/repo", "skills/source/SKILL.md")!;
        internal Fixture(bool overrides = false)
        {
            Directory.CreateDirectory(Root);
            var environment = new Dictionary<string, string> { ["HOME"] = Root, ["USERPROFILE"] = Root, ["PATH"] = "/usr/bin:/bin" };
            if (overrides)
            {
                environment["CLAUDE_CONFIG_DIR"] = Path.Combine(Root, "claude-override");
                environment["PI_CODING_AGENT_DIR"] = Path.Combine(Root, "pi-override");
            }
            Runner = new ProcessRunner(Logger).WithIsolatedContext(Root, environment);
            Assert.SkipUnless(SecureRemovalBackend.TryObserveIdentity(Root, out _, out _), "Native read-only identity observation is required.");
        }
        internal InstalledSkill Skill(string root, string leaf, string repo = "owner/repo", string agent = "universal")
        {
            var path = Path.Combine(root, leaf);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\n---\nInert fixture.");
            return Observe(new()
            {
                Name = "demo",
                ResolvedPath = path,
                ScanRoot = root,
                Scope = Scope.User,
                Agents = [new(agent, path, false, Scope: Scope.User)],
                FrontMatter = new() { Name = "demo", Upstream = "https://github.com/" + repo, GithubPath = "skills/source" },
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                Ignored = false,
                IsSymlinked = false,
                InstalledAt = null,
            });
        }
        internal InstalledSkill Observe(InstalledSkill skill) => skill with
        { ObservedSubject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, TestContext.Current.CancellationToken) };
        internal InventorySnapshot Snapshot(params InstalledSkill[] skills)
        {
            var roots = skills.Select(skill => skill.ScanRoot).Distinct().Select(root => new ScanRoot(root, Scope.User, "universal")).ToImmutableArray();
            return new()
            {
                Skills = skills.ToImmutableArray(),
                ConfiguredRoots = roots,
                ScannedRoots = roots,
                Providers = roots.Select(root => new ProviderObservation("gh", true, [root.Path], DateTimeOffset.UtcNow)).ToImmutableArray(),
                UsedGhSkillList = true,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }
        internal async Task<(InstallNavigationEvidence Evidence, IReadOnlyList<IReadOnlyList<string>> Folders)> AgentFolders(params string[] agents)
        {
            var options = new GhSkillInstallService.Options(Agents: agents, Scope: "user", WorkingDirectory: Root) { NavigationSource = Anchor };
            var roots = await GhPlacementCatalog.DestinationsAsync(options, Runner, TestContext.Current.CancellationToken);
            var evidence = InstallNavigationEvidence.Capture(options, roots, Runner)!;
            var folders = await SkillViewWorkflowCoordinator.CaptureInstallAgentFoldersAsync(evidence, Runner, TestContext.Current.CancellationToken);
            return (evidence, folders);
        }
        internal InstalledInstallHandoff ResolveAlternatives(InventorySnapshot snapshot, InstallNavigationEvidence evidence, IReadOnlyList<IReadOnlyList<string>> folders) =>
            InstalledInstallHandoffResolver.Resolve(evidence, snapshot, InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken, folders);
        internal InstalledInstallHandoff Resolve(InventorySnapshot snapshot, string[]? roots = null, bool exact = true) =>
            InstalledInstallHandoffResolver.Resolve(new(Anchor, roots ?? [Root], "user", "context", exact, ["universal"]), snapshot,
                InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        public void Dispose() => Directory.Delete(Root, true);
    }
}

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledRepositoryBrowseRenderingTests
{
    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, false)]
    [InlineData(120, 36, true)]
    public async Task BrowseRestrictionStaysVisibleThroughReloadExplicitRefreshAndFilteredEmptyThenKeyboardClear(int width, int height, bool newSnapshot)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        var services = TuiServices.Build(new Logger(), logDirectory: Path.GetTempPath(), workingDirectory: Path.GetTempPath());
        var shell = new SkillViewApp(services, new(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []));
        using var window = shell.BuildUiForTests();
        var one = InstalledOverviewTests.Copy(Path.Combine(Path.GetTempPath(), "demo-a"), "demo-a", "owner/repo") with
        { Agents = [new("universal", Path.Combine(Path.GetTempPath(), "demo-a"), false, Scope: Scope.User)] };
        var other = InstalledOverviewTests.Copy(Path.Combine(Path.GetTempPath(), "demo-b"), "demo-b", "other/repo");
        var snapshot = InventorySnapshot.Empty with { Skills = [one, other] };
        var nextSnapshot = snapshot;
        var loads = 0; var actions = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(nextSnapshot); }, (_, _, _) => { actions++; return Task.CompletedTask; }, () => { },
            onReviewUpdate: _ => actions++)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        shell.SetInstalledTabForTests(view);
        shell.ForceActiveTabForTests(SkillViewTab.Installed);
        var overview = InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken);
        Assert.True(view.RevealInstall(new(snapshot, overview, "", []) { BrowseOnly = true, BrowseRepository = "https://github.com/owner/repo" }));
        var session = app.Begin(window);
        try
        {
            Draw();
            var indicator = view.SubViews.OfType<Label>().Single(label => label.Text.StartsWith("Repository:", StringComparison.Ordinal));
            var clear = view.SubViews.OfType<Button>().Single(button => button.Text == "Clear");
            var table = view.SubViews.OfType<TableView>().Single();
            var detail = view.SubViews.OfType<Markdown>().Single();
            AssertRestriction();
            Assert.All(view.RowsForTests, row => Assert.Equal("demo-a", row.Group?.Name));
            Assert.Null(view.GetSelectedSkill());
            view.SetSelectedRowForTests(0); view.NewKeyDownEvent(new Key(Terminal.Gui.Drivers.KeyCode.CursorRight));
            view.SetSelectedRowForTests(1);
            Assert.Same(one, view.GetSelectedSkill());
            var selected = view.SelectedKeyForTests;
            view.SelectFilter(0, view.FilterChoices(0).Single(choice => choice.Key == InstalledFilters.Key("agent", "universal")).Key);
            view.SetFilterTextForTests("demo-a"); view.FocusList();
            view.NewKeyDownEvent(new Key('G')); view.NewKeyDownEvent(new Key('s'));
            nextSnapshot = newSnapshot ? snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(1) } : snapshot;
            await view.LoadAsync(); Draw();
            Assert.Equal(1, loads);
            AssertRestriction();
            Assert.Equal(selected, view.SelectedKeyForTests);
            Assert.Same(one, view.GetSelectedSkill());
            Assert.Equal("demo-a", view.GetFilterText());
            Assert.Contains("Agent: `universal`", view.DetailTextForTests);
            shell.SetStatusForTests("Unrelated workflow notice", TuiHelpers.NotificationLevel.Info);
            view.RefreshAvailability(); Draw(); AssertRestriction();

            // Exercise the shell's actual explicit Refresh key: invalidate and
            // invoke the same Installed loader, with a now-missing repository.
            nextSnapshot = snapshot with { Skills = [other], CapturedAt = snapshot.CapturedAt.AddSeconds(2) };
            view.FocusList();
            app.Keyboard.RaiseKeyDownEvent(new Key('r')); Draw();
            Assert.Equal(2, loads);
            Assert.Same(nextSnapshot, view.CurrentSnapshot);
            AssertRestriction();
            Assert.Empty(view.RowsForTests);
            Assert.Null(view.GetSelectedSkill());
            Assert.Contains("No matching installed skills", view.DetailTextForTests);
            Assert.DoesNotContain("No installed skills found", view.DetailTextForTests);
            Assert.Contains("https://github.com/owner/repo", view.DetailTextForTests);
            Assert.False(view.SubViews.OfType<Button>().Single(button => button.Text == "Review update (u)").Enabled);
            Assert.False(view.TryStartSelectedRemoveForTests());

            // Existing keyboard Clear resets independent facets/search/scope
            // and the repository restriction in the same visible transition.
            view.FocusFilterForTests();
            for (var i = 0; i < 5; i++) Assert.True(view.HandleLocalNavigation(Key.Tab));
            Assert.True(clear.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(Key.Enter); Draw();
            Assert.False(indicator.Visible); Assert.Empty(indicator.Text);
            Assert.Equal(3, table.Frame.Y); Assert.Equal(3, detail.Frame.Y);
            Assert.Empty(view.GetFilterText());
            Assert.DoesNotContain("Repository:", view.DetailTextForTests);
            Assert.Contains(view.RowsForTests, row => row.Group?.Name == "demo-b");
            Assert.Contains(view.SubViews.OfType<Label>(), label => label.Text == "P:any G:any s:S");
            Assert.Contains(view.SubViews.OfType<Button>(), button => button.Text == "Agent:Any");
            Assert.Null(view.GetSelectedSkill());
            Assert.Equal(0, actions); Assert.Equal(2, loads);

            void AssertRestriction()
            {
                Assert.True(indicator.Visible);
                Assert.Equal("Repository: owner/repo — Clear shows all", indicator.Text);
                Assert.True(clear.Visible); Assert.True(clear.Enabled);
                Assert.True(view.ViewportToScreen().Contains(indicator.FrameToScreen()));
                Assert.True(view.ViewportToScreen().Contains(clear.FrameToScreen()));
                Assert.Equal(4, table.Frame.Y); Assert.Equal(4, detail.Frame.Y);
                Assert.True(indicator.Frame.Bottom <= table.Frame.Top);
                Assert.InRange(indicator.Frame.Right, 1, view.Viewport.Width);
                Assert.Equal(1, indicator.Frame.Height);
            }
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(78)]
    [InlineData(118)]
    public void RepositoryIndicatorIsLiteralBoundedAndKeepsClearGuidance(int columns)
    {
        var text = InstalledTabView.RepositoryFilterText("https://github.com/owner_under/" + new string('x', 300) + "\u001b[31m\nrepo", columns);
        Assert.StartsWith("Repository: ", text);
        Assert.EndsWith(" — Clear shows all", text);
        Assert.InRange(text.Length, 1, columns);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\n', text);
        Assert.Empty(InstalledTabView.RepositoryFilterText(null, columns));
    }
}
