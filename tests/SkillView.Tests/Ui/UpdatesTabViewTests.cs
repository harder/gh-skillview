using System.Collections.Immutable;
using System.Threading.Tasks;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class UpdatesTabViewTests
{
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
            snapshotLoader: _ => ++loadCount == 1 ? first.Task : second.Task,
            onRemove: static (_, _) => { },
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
            onRemove: static (_, _) => { },
            onLeaveTab: static () => { },
            onGoToSearch: static () => { });

        var firstLoad = view.LoadAsync();
        await firstStarted.Task;
        var replacementLoad = view.LoadAsync();

        await Task.WhenAll(firstLoad, replacementLoad);

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
            snapshotLoader: _ => Task.FromResult(SnapshotWithSkill("loaded")),
            onRemove: static (_, _) => { },
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

    private static UpdatesTabView CreateUpdatesTab(
        Func<Task<InventorySnapshot>> snapshotLoader,
        Func<string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>>? updateRunner = null)
    {
        var logger = new Logger(LogLevel.Debug);
        return new UpdatesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: _ => snapshotLoader(),
            updateRunner: updateRunner ?? ((_, _, _) => throw new NotSupportedException()),
            ghPathProvider: static () => "/usr/bin/gh",
            logger: logger,
            onLeaveTab: static () => { },
            onUpdateApplied: static () => { });
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
