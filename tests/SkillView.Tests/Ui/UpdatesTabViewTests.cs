using System.Collections.Immutable;
using System.Threading.Tasks;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class UpdatesTabViewTests
{
    [Fact]
    public async Task QueueDrillIn_SelectsOnlyExactPhysicalInstallAndDoesNotStartAnUpdate()
    {
        var first = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var second = MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo");
        var calls = 0;
        GhSkillUpdateService.Options? checkedOptions = null;
        var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            (_, options, _) => { calls++; checkedOptions = options; return Task.FromResult(SuccessfulDryRun()); });
        view.AllBoxForTests.Value = CheckState.Checked;

        await view.LoadForSubjectAsync(OperationSubject.From(second));

        Assert.Equal(0, calls);
        Assert.Equal(CheckState.UnChecked, view.AllBoxForTests.Value);
        Assert.Single(view.LoadedSkillNamesForTests);
        Assert.Contains("availability unknown", view.StatusTextForTests);
        await view.RunForTestsAsync(dryRun: true, batchOnly: false);
        Assert.Equal(second.InstallationId, Assert.Single(checkedOptions!.Targets!).InstallationId);
    }

    [Fact]
    public async Task QueueDrillIn_MissingTargetCannotUpdateSameNamedSibling()
    {
        var missing = MaintenanceQueueTests.Skill("/skills/missing", "https://github.com/one/repo");
        var sibling = MaintenanceQueueTests.Skill("/skills/sibling", "https://github.com/two/repo");
        var calls = 0;
        var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [sibling] }),
            (_, _, _) => { calls++; return Task.FromResult(SuccessfulDryRun()); });
        await view.LoadForSubjectAsync(OperationSubject.From(missing));
        Assert.Empty(view.LoadedSkillNamesForTests);
        Assert.Contains("missing or ambiguous", view.StatusTextForTests);
        await view.RunForTestsAsync(dryRun: true, batchOnly: false);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task QueueDrillIn_FailedCaptureClearsPreviousTargetsAndCannotRunUpdateOrUnpin()
    {
        var unrelated = MaintenanceQueueTests.Skill("/skills/unrelated", "https://github.com/other/repo");
        var selected = MaintenanceQueueTests.Skill("/skills/selected", "https://github.com/selected/repo");
        var capture = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var updates = 0;
        var unpins = 0;
        using var view = CreateUpdatesTab(
            () => ++loads == 1 ? Task.FromResult(InventorySnapshot.Empty with { Skills = [unrelated] }) : capture.Task,
            (_, _, _) => { updates++; return Task.FromResult(SuccessfulDryRun()); },
            (_, _, _) => { unpins++; return Task.FromResult(SuccessfulDryRun()); });
        await view.LoadAsync();
        view.MarkRowForTests(0);
        view.SelectRowForTests(0);
        view.AllBoxForTests.Value = CheckState.Checked;
        view.UnpinBoxForTests.Value = CheckState.Checked;

        var load = view.LoadForSubjectAsync(OperationSubject.From(selected));
        Assert.Empty(view.LoadedSkillNamesForTests);
        Assert.Equal(CheckState.UnChecked, view.AllBoxForTests.Value);
        Assert.Equal(CheckState.UnChecked, view.UnpinBoxForTests.Value);
        capture.SetException(new IOException("Selected inventory unavailable"));
        await load;
        Assert.Contains("inventory load failed", view.StatusTextForTests);
        Assert.Empty(view.LoadedSkillNamesForTests);
        await view.RunForTestsAsync(dryRun: true, batchOnly: false);
        await view.RunForTestsAsync(dryRun: false, batchOnly: false);
        await view.RunForTestsAsync(dryRun: false, batchOnly: true);
        view.UpdateCurrentRowForTests();
        await view.PlanUnpinForTestsAsync();
        Assert.Equal(0, updates);
        Assert.Equal(0, unpins);
    }

    [Fact]
    public async Task QueueDrillIn_RefreshRetainsExactSelectionAndRefusesItsDisappearance()
    {
        var selected = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var sibling = selected with { ResolvedPath = "/skills/two" };
        var snapshot = InventorySnapshot.Empty with { Skills = [selected, sibling] };
        var view = CreateUpdatesTab(() => Task.FromResult(snapshot));
        view.ActivateFromChanges(static () => { }, OperationSubject.From(selected));
        Assert.Single(view.LoadedSkillNamesForTests);
        snapshot = snapshot with { Skills = [sibling] };
        await view.LoadAsync();
        Assert.Empty(view.LoadedSkillNamesForTests);
        Assert.Contains("missing or ambiguous", view.StatusTextForTests);
    }

    // gh ≥ 2.94 is required, so every update flag is always available; the
    // Updates tab no longer has per-capability UI state to test.

    [Fact]
    public void UpdateControls_AreEnabledByDefault()
    {
        var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty));
        Assert.True(view.AllBoxForTests.Enabled);
        Assert.Equal("_all", view.AllBoxForTests.Text.ToString());
        Assert.True(view.DryRunButtonForTests.Enabled);
    }

    [Fact]
    public async Task LoadAsync_IgnoresStaleEarlierSnapshot()
    {
        var first = new TaskCompletionSource<InventorySnapshot>();
        var second = new TaskCompletionSource<InventorySnapshot>();
        var loadCount = 0;
        var view = CreateUpdatesTab(
            () => ++loadCount == 1 ? first.Task : second.Task);

        var initialLoad = view.LoadAsync();
        var replacementLoad = view.LoadAsync();

        second.SetResult(SnapshotWithSkill("newer"));
        await replacementLoad;

        first.SetResult(SnapshotWithSkill("older"));
        await initialLoad;

        Assert.Equal(["newer"], view.LoadedSkillNamesForTests);
    }

    [Fact]
    public async Task CancelPendingWork_CancelsActiveUpdateAndRestoresControls()
    {
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken updateToken = default;
        var view = CreateUpdatesTab(
            () => Task.FromResult(SnapshotWithSkill("installed")),
            async (_, _, token) =>
            {
                updateToken = token;
                updateStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The canceled delay should not complete normally.");
            });
        await view.LoadAsync();
        view.AllBoxForTests.Value = CheckState.Checked;

        var update = view.RunForTestsAsync(dryRun: true, batchOnly: false);
        await updateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(view.BusyForTests);
        Assert.False(view.DryRunButtonForTests.Enabled);

        view.CancelPendingWork();
        await update.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(updateToken.IsCancellationRequested);
        Assert.False(view.BusyForTests);
        Assert.True(view.DryRunButtonForTests.Enabled);
    }

    [Fact]
    public async Task CanceledUpdateCompletion_DoesNotOverwriteReloadedState()
    {
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCompleted = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotName = "initial";
        var view = CreateUpdatesTab(
            () => Task.FromResult(SnapshotWithSkill(snapshotName)),
            (_, _, _) =>
            {
                updateStarted.SetResult();
                return updateCompleted.Task;
            });
        await view.LoadAsync();
        view.AllBoxForTests.Value = CheckState.Checked;

        var update = view.RunForTestsAsync(dryRun: true, batchOnly: false);
        await updateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        view.CancelPendingWork();

        snapshotName = "reloaded";
        await view.LoadAsync();
        var reloadedStatus = view.StatusTextForTests;
        updateCompleted.SetResult(SuccessfulDryRun());
        await update.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["reloaded"], view.LoadedSkillNamesForTests);
        Assert.Equal(reloadedStatus, view.StatusTextForTests);
        Assert.DoesNotContain("dry-run complete", view.StatusTextForTests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledTab_LoadAsync_IgnoresStaleEarlierSnapshot()
    {
        var first = new TaskCompletionSource<InventorySnapshot>();
        var second = new TaskCompletionSource<InventorySnapshot>();
        var loadCount = 0;
        var view = new InstalledTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            runTask: (operation, _) => operation().GetAwaiter().GetResult(),
            snapshotLoader: _ => ++loadCount == 1 ? first.Task : second.Task,
            onRemove: static (_, _, _) => Task.CompletedTask,
            onLeaveTab: static () => { },
            onGoToSearch: static () => { });

        var initialLoad = view.LoadAsync();
        var replacementLoad = view.LoadAsync();

        second.SetResult(SnapshotWithSkill("newer"));
        await replacementLoad;

        first.SetResult(SnapshotWithSkill("older"));
        await initialLoad;

        Assert.Equal(["newer"], view.VisibleSkillNamesForTests);
    }

    [Fact]
    public async Task InstalledTab_LoadAsync_CancelsSupersededInventoryScan()
    {
        var loadCount = 0;
        CancellationToken firstToken = default;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new InstalledTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            runTask: (operation, _) => operation().GetAwaiter().GetResult(),
            snapshotLoader: async token =>
            {
                if (Interlocked.Increment(ref loadCount) == 1)
                {
                    firstToken = token;
                    firstStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return SnapshotWithSkill("newer");
            },
            onRemove: static (_, _, _) => Task.CompletedTask,
            onLeaveTab: static () => { },
            onGoToSearch: static () => { });

        var firstLoad = view.LoadAsync();
        await firstStarted.Task;
        var indicator = Assert.IsType<TabLoadingView>(view.SubViews.Single(child => child is TabLoadingView));
        Assert.True(indicator.Visible);
        Assert.False(view.SubViews.Single(child => child is TableView).Visible);
        var replacementLoad = view.LoadAsync();

        await Task.WhenAll(firstLoad, replacementLoad);

        Assert.False(indicator.Visible);
        Assert.True(view.SubViews.Single(child => child is TableView).Visible);
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(["newer"], view.VisibleSkillNamesForTests);
    }

    [Fact]
    public async Task InstalledTab_LoadAsync_NotifiesStateChangeAfterPopulate()
    {
        var callbackCount = 0;
        IReadOnlyList<string>? visibleNamesAtCallback = null;
        InstalledSkill? selectedSkillAtCallback = null;
        InstalledTabView? view = null;
        view = new InstalledTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            runTask: (operation, _) => operation().GetAwaiter().GetResult(),
            snapshotLoader: _ => Task.FromResult(SnapshotWithSkill("loaded")),
            onRemove: static (_, _, _) => Task.CompletedTask,
            onLeaveTab: static () => { },
            onGoToSearch: static () => { },
            onStateChange: () =>
            {
                callbackCount++;
                visibleNamesAtCallback = view!.VisibleSkillNamesForTests;
                selectedSkillAtCallback = view.GetSelectedSkill();
            });

        await view.LoadAsync();

        Assert.True(callbackCount >= 1);
        Assert.Equal(["loaded"], visibleNamesAtCallback);
        Assert.NotNull(selectedSkillAtCallback);
        Assert.Equal("loaded", selectedSkillAtCallback!.Name);
    }

    [Fact]
    public async Task MarkedSelectionPreservesPhysicalIdentityEvenWhenAllBoxIsChecked()
    {
        var first = SnapshotWithSkill("demo").Skills[0];
        var second = first with { ResolvedPath = "/another/demo", Scope = Scope.Project };
        GhSkillUpdateService.Options? captured = null;
        using var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            (_, options, _) => { captured = options; return Task.FromResult(SuccessfulDryRun()); });
        await view.LoadAsync();
        view.AllBoxForTests.Value = CheckState.Checked;
        view.MarkRowForTests(1);
        await view.RunForTestsAsync(dryRun: true, batchOnly: true);
        Assert.NotNull(captured);
        Assert.False(captured.All);
        Assert.Equal(second.InstallationId, Assert.Single(captured.Targets!).InstallationId);
    }

    [Fact]
    public async Task CurrentRowShortcutPreservesItsPhysicalTarget()
    {
        var first = SnapshotWithSkill("demo").Skills[0];
        var second = first with { ResolvedPath = "/another/demo", Scope = Scope.Project };
        GhSkillUpdateService.Options? captured = null;
        using var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            (_, options, _) => { captured = options; return Task.FromResult(SuccessfulDryRun()); });
        await view.LoadAsync();
        view.AllBoxForTests.Value = CheckState.Checked;
        view.SelectRowForTests(1);
        view.UpdateCurrentRowForTests();
        Assert.NotNull(captured);
        Assert.False(captured.All);
        Assert.Equal(second.InstallationId, Assert.Single(captured.Targets!).InstallationId);
    }

    private static UpdatesTabView CreateUpdatesTab(
        Func<Task<InventorySnapshot>> snapshotLoader,
        Func<string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>>? updateRunner = null,
        Func<InstalledSkill, bool, CancellationToken, Task<UpdateResult>>? unpinRunner = null)
    {
        var logger = new Logger(LogLevel.Debug);
        return new UpdatesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            runTask: (operation, _) => operation().GetAwaiter().GetResult(),
            snapshotLoader: _ => snapshotLoader(),
            updateRunner: updateRunner ?? ((_, _, _) => throw new NotSupportedException()),
            ghPathProvider: static () => "/usr/bin/gh",
            logger: logger,
            onLeaveTab: static () => { },
            onUpdateApplied: static () => { },
            unpinRunner: unpinRunner);
    }

    private static UpdateResult SuccessfulDryRun() => new()
    {
        DryRun = true,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "would update initial",
        StdErr = string.Empty,
        ErrorMessage = null,
        CommandLine = ["skill", "update", "--dry-run", "--all"],
        Entries = ImmutableArray<UpdateEntry>.Empty,
    };

    private static InventorySnapshot SnapshotWithSkill(string name) => InventorySnapshot.Empty with
    {
        Skills = ImmutableArray.Create(new InstalledSkill
        {
            Name = name,
            ResolvedPath = $"/skills/{name}",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        }),
    };
}
