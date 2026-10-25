using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using SkillView.Maintenance;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesTabViewTests
{
    [Fact]
    public async Task SummaryUsesCapturedChildHomeAndTechnicalViewKeepsExactPath()
    {
        var home = Path.GetFullPath("changes-child-home");
        var skill = MaintenanceQueueTests.Skill(Path.Combine(home, "skills", "demo"), "https://github.com/owner/repo") with { Validity = ValidityState.InvalidMetadata };
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [skill] }),
            static () => { }, homeDirectory: home);
        await view.LoadAsync();
        var detail = Assert.IsType<Markdown>(view.SubViews.Single(child => child is Markdown));
        Assert.DoesNotContain(home, detail.Text.ToString());
        Assert.Contains("~", detail.Text.ToString());
        view.NewKeyDownEvent(new Key(KeyCode.F2));
        Assert.Contains(skill.ResolvedPath, detail.Text.ToString());
    }

    [Fact]
    public async Task SameFrozenSnapshotReusesQueueAndNewSnapshotReclassifies()
    {
        var root = Directory.CreateTempSubdirectory("skillview-queue-cache-");
        try
        {
            var snapshot = InventorySnapshot.Empty with { ScannedRoots = [new(root.FullName, Scope.Custom, null)] };
            using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
                _ => Task.FromResult(snapshot), static () => { });
            await view.LoadAsync();
            Assert.Equal(0, view.RowCountForTests);
            Directory.CreateDirectory(Path.Combine(root.FullName, "new-empty-child"));
            await view.LoadAsync();
            Assert.Equal(0, view.RowCountForTests);
            snapshot = snapshot with { CapturedAt = DateTimeOffset.UtcNow };
            await view.LoadAsync();
            Assert.True(view.RowCountForTests > 0);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Loading_ReplacesOldContentAndClearsOnCancellationAndCompletion()
    {
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLoad = true;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => { if (firstLoad) { firstLoad = false; return Task.FromResult(SnapshotWithMalformedSkill()); } return pending.Task; },
            static () => { });
        await view.LoadAsync();
        var loading = view.LoadAsync();
        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        var indicator = Assert.IsType<TabLoadingView>(view.SubViews.Single(child => child is TabLoadingView));
        Assert.True(indicator.Visible);
        Assert.False(table.Visible);
        Assert.Null(table.Table);
        Assert.Equal(0, view.RowCountForTests);
        view.CancelPendingLoad();
        Assert.False(indicator.Visible);
        pending.SetResult(SnapshotWithMalformedSkill());
        await loading;
        Assert.Equal(0, view.RowCountForTests);
        await view.LoadAsync();
        Assert.False(indicator.Visible);
        Assert.True(table.Visible);
        Assert.True(view.RowCountForTests > 0);
    }

    [Fact]
    public async Task Enter_RoutesExactSelectedIssueAndPhysicalInstallation()
    {
        var first = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var second = MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo");
        MaintenanceQueueEntry? activated = null;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            static () => { }, onActivateEntry: entry => activated = entry);
        await view.LoadAsync();
        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        table.SetSelection(0, 0, false); // One family, anchored at the first exact address.
        table.NewKeyDownEvent(Key.Enter);
        Assert.NotNull(activated);
        Assert.Equal("NameCollision", activated.Issue);
        Assert.Equal(first.InstallationId, activated.Subject.InstallationId);
        Assert.Equal(first.ResolvedPath, activated.Cleanup!.Path);
    }

    [Fact]
    public async Task CancelBeforeQueuedUiDispatch_DoesNotPublishRows()
    {
        Action? queued = null;
        var dispatchQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = new ChangesTabView(action => { queued = action; dispatchQueued.TrySetResult(); return dispatched.Task; },
            _ => Task.FromResult(SnapshotWithMalformedSkill()), static () => { });
        var loading = view.LoadAsync();
        await dispatchQueued.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        view.CancelPendingLoad();
        queued();
        dispatched.SetResult();
        await loading;
        Assert.Equal(0, view.RowCountForTests);
    }

    [Fact]
    public async Task HealthyInventoryShowsCleanLocalStateAndKeepsUpdatesAccessible()
    {
        var healthy = MaintenanceQueueTests.Skill("/skills/healthy", "https://github.com/one/repo");
        healthy = healthy with { ObservedSubject = OperationSubject.From(healthy) with { ExposureCoverageComplete = true, ExposureCoverage = "complete" } };
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [healthy] }),
            static () => { });
        await view.LoadAsync();
        Assert.Equal(0, view.RowCountForTests);
        var detail = view.SubViews.OfType<Markdown>().Single().Text.ToString();
        Assert.Contains("No local problems found. Updates not checked.", detail);
        Assert.Contains("**C:** Check updates", detail);
        Assert.DoesNotContain("omitted", detail);
        Assert.Contains("0 Problems", view.StatusTextForTests);
        Assert.Contains("Updates not checked", view.StatusTextForTests);
        Assert.Contains("0 Review", view.StatusTextForTests);
        Assert.Equal("0 Problems · Updates not checked · 0 Review", view.GetQueueLabel());
        Assert.DoesNotContain("0 Updates", view.GetQueueLabel());
        Assert.Contains("**Enter / u:**", detail);
    }

    [Fact]
    public async Task IncompleteConsumerCoverageStaysVisibleEvenWhenRoutineRowsAreHidden()
    {
        var skill = MaintenanceQueueTests.Skill("/skills/unobserved", "https://github.com/one/repo");
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [skill] }), static () => { });
        await view.LoadAsync();
        Assert.Equal(0, view.RowCountForTests);
        Assert.Contains("Scan incomplete", view.StatusTextForTests);
        view.NewKeyDownEvent(new Key(KeyCode.F2));
        Assert.Contains("Consumer discovery incomplete for 1", view.SubViews.OfType<Markdown>().Single().Text.ToString());
    }

    [Fact]
    public async Task IncompleteDiscoveryNeverClaimsACleanScanAndDetailsRemainAccessibleWithoutRows()
    {
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { ConfiguredRootsComplete = false }),
            static () => { });
        await view.LoadAsync();
        Assert.Contains("Scan incomplete", view.StatusTextForTests);
        var detail = view.SubViews.OfType<Markdown>().Single();
        Assert.Contains("checked locations", detail.Text.ToString());
        Assert.DoesNotContain("No local problems found. Updates not checked.", detail.Text.ToString());
        view.NewKeyDownEvent(new Key(KeyCode.F2));
        Assert.Contains("Configured-root discovery is incomplete", detail.Text.ToString());
    }

    [Fact]
    public async Task LoadAsync_ShowsCleanupCandidateDetailOnSelectionWithoutOpeningCleanup()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(SnapshotWithMalformedSkill()),
            onLeaveTab: static () => { });

        await view.LoadAsync();

        var detail = Assert.IsType<Markdown>(view.SubViews.Single(child => child is Markdown));
        var rendered = detail.Text.ToString();

        Assert.Contains("## Missing or invalid skill file", rendered);
        Assert.Contains("/skills/codex-primary-runtime", rendered);
        Assert.Contains("This directory has no SKILL.md file", rendered);
        Assert.DoesNotContain("validity=MissingSkillMd", rendered);
    }

    [Fact]
    public async Task LoadAsync_CountsProblemsWithoutInventingAvailableUpdates()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(SnapshotWithMalformedSkill()),
            onLeaveTab: static () => { });

        await view.LoadAsync();

        Assert.Contains("1 Problems", view.StatusTextForTests);
        Assert.Equal("1 Problems · Updates not checked · 0 Review", view.GetQueueLabel());
        Assert.DoesNotContain("0 Updates", view.GetQueueLabel());
        Assert.Contains("Updates not checked", view.StatusTextForTests);
        Assert.Contains("F2 Details", view.StatusTextForTests);
    }

    [Fact]
    public async Task PresentationLimit_RemainsVisibleWhenTogglingDetails()
    {
        var skills = Enumerable.Range(0, ChangesQueueBuilder.MaxRows + 1).Select(index =>
            MaintenanceQueueTests.Skill("/skills/" + index, "https://github.com/one/repo") with
            { Name = "skill-" + index, Validity = ValidityState.InvalidMetadata }).ToImmutableArray();
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = skills }),
            static () => { });
        await view.LoadAsync();
        Assert.Equal(ChangesQueueBuilder.MaxRows, view.RowCountForTests);
        Assert.Contains("1 omitted", view.StatusTextForTests);
        view.NewKeyDownEvent(new Key(KeyCode.F2));
        Assert.Contains("1 omitted", view.StatusTextForTests);
        Assert.Contains("1 additional findings", view.SubViews.OfType<Markdown>().Single().Text.ToString());
        view.NewKeyDownEvent(new Key(KeyCode.F2));
        Assert.Contains("1 omitted", view.StatusTextForTests);
        Assert.Contains("F2 Details", view.StatusTextForTests);
    }

    private static InventorySnapshot SnapshotWithMalformedSkill() => InventorySnapshot.Empty with
    {
        ScannedRoots = ImmutableArray.Create(new ScanRoot("/skills", Scope.User, "copilot")),
        Skills = ImmutableArray.Create(new InstalledSkill
        {
            Name = "codex-primary-runtime",
            ResolvedPath = "/skills/codex-primary-runtime",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray.Create(new AgentMembership("copilot", "/skills/codex-primary-runtime", false)),
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.MissingSkillMd,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            Package = new SkillPackage(
                Source: "TCGplayer/guild-ai-tools-and-notes",
                SourceType: "github",
                SourceUrl: "https://github.com/TCGplayer/guild-ai-tools-and-notes.git",
                InstalledAt: null,
                UpdatedAt: null),
        }),
    };
}
