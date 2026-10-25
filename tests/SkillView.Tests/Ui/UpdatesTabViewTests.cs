using System.Collections.Immutable;
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
    public async Task ExactOptionsResolveOneCopyAndPreparationCannotWidenToItsSameNamedPeer()
    {
        var first = SnapshotWithSkill("demo").Skills[0];
        var second = first with { ResolvedPath = "/another/demo", Scope = Scope.Project };
        GhSkillUpdateService.Options? captured = null;
        using var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            (_, options, _) => { captured = options; return Task.FromResult(SuccessfulResult()); });
        await view.LoadForSubjectAsync(OperationSubject.From(second));
        Assert.Null(captured);
        Assert.Equal(second.InstallationId, view.SelectedInstallationForTests);
        Assert.Empty(view.SubViews.OfType<TableView>());
        await view.ReviewUpdateAsync();
        Assert.NotNull(captured);
        Assert.False(captured.All); Assert.False(captured.DryRun);
        Assert.Equal(second.InstallationId, Assert.Single(captured.Targets!).InstallationId);
    }

    [Fact]
    public async Task UnseededRefreshDoesNotOpenAnInventoryBrowserOrCaptureInventory()
    {
        using var view = CreateUpdatesTab(() => throw new InvalidOperationException("An exact subject is required"));
        await view.LoadAsync();
        await view.ReviewUpdateAsync();
        Assert.Null(view.SelectedInstallationForTests);
        Assert.Empty(view.SubViews.OfType<TableView>());
    }

    [Fact]
    public async Task SelectedPreviewRetainsExactEvidenceWithoutInstalledShortcuts()
    {
        var home = Path.GetFullPath("updates-child-home");
        var skill = MaintenanceQueueTests.Skill(Path.Combine(home, "skills", "demo"), "https://github.com/owner/repo") with
        { OwnershipRunner = new SkillView.Subprocess.ProcessRunner(new Logger(), environment: new Dictionary<string, string> { ["HOME"] = home, ["USERPROFILE"] = home }) };
        skill = skill with { ObservedSubject = OperationSubject.From(skill) };
        using var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [skill] }));
        await view.LoadForSubjectAsync(OperationSubject.From(skill));
        view.ToggleDetailsForTests();
        var detail = view.PreviewTextForTests;
        Assert.Contains("**Path:** `" + skill.ResolvedPath + "`", detail);
        Assert.Contains("Installation address ID", detail);
        Assert.DoesNotContain("**E** Analyze", detail); Assert.DoesNotContain("**K** Check", detail);
        Assert.DoesNotContain("**F2** Technical", detail);
    }

    [Fact]
    public async Task MissingOrFailedExactCaptureCannotPrepareUpdateOrUnpinAPreviousCopy()
    {
        var first = SnapshotWithSkill("first").Skills[0];
        var missing = first with { ResolvedPath = "/missing/first" };
        var capture = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0; var prepares = 0; var unpins = 0;
        using var view = CreateUpdatesTab(() => ++loads == 1 ? Task.FromResult(InventorySnapshot.Empty with { Skills = [first] }) : capture.Task,
            (_, _, _) => { prepares++; return Task.FromResult(SuccessfulResult()); },
            (_, _, _) => { unpins++; return Task.FromResult(SuccessfulResult()); });
        await view.LoadForSubjectAsync(OperationSubject.From(first));
        var pending = view.LoadForSubjectAsync(OperationSubject.From(missing));
        Assert.Null(view.SelectedInstallationForTests);
        await view.ReviewUpdateAsync(); await view.PlanUnpinForTestsAsync();
        capture.SetException(new IOException("Selected inventory unavailable")); await pending;
        await view.ReviewUpdateAsync(); await view.PlanUnpinForTestsAsync();
        Assert.Contains("inventory load failed", view.StatusTextForTests);
        Assert.Equal(0, prepares); Assert.Equal(0, unpins);
    }

    [Fact]
    public async Task RefreshRetainsExactSubjectAndRefusesItsDisappearanceWithoutSelectingAPeer()
    {
        var selected = SnapshotWithSkill("demo").Skills[0];
        var peer = selected with { ResolvedPath = "/peer/demo" };
        var snapshot = InventorySnapshot.Empty with { Skills = [selected, peer] };
        using var view = CreateUpdatesTab(() => Task.FromResult(snapshot));
        await view.LoadForSubjectAsync(OperationSubject.From(selected));
        Assert.Equal(selected.InstallationId, view.SelectedInstallationForTests);
        snapshot = snapshot with { Skills = [peer] };
        await view.LoadAsync();
        Assert.Null(view.SelectedInstallationForTests);
        Assert.Contains("missing or ambiguous", view.StatusTextForTests);
        Assert.False(view.ReviewButtonForTests.Enabled);
    }

    [Fact]
    public async Task SupersededExactLoadCannotOverwriteANewerSnapshot()
    {
        var selected = SnapshotWithSkill("demo").Skills[0];
        var first = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        using var view = CreateUpdatesTab(() => ++loads == 1 ? first.Task : second.Task);
        var oldLoad = view.LoadForSubjectAsync(OperationSubject.From(selected));
        var newLoad = view.LoadForSubjectAsync(OperationSubject.From(selected));
        second.SetResult(InventorySnapshot.Empty with { Skills = [selected with { Name = "newer" }] }); await newLoad;
        first.SetResult(InventorySnapshot.Empty with { Skills = [selected with { Name = "older" }] }); await oldLoad;
        Assert.Equal(["newer"], view.LoadedSkillNamesForTests);
    }

    [Fact]
    public async Task CancelPendingWorkCancelsExactPreparationAndStaleCompletionCannotRestoreApproval()
    {
        var selected = SnapshotWithSkill("demo").Skills[0];
        var completion = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        using var view = CreateUpdatesTab(() => Task.FromResult(InventorySnapshot.Empty with { Skills = [selected] }),
            (_, _, token) => { operationToken = token; return completion.Task; });
        await view.LoadForSubjectAsync(OperationSubject.From(selected));
        var work = view.ReviewUpdateAsync();
        Assert.True(view.BusyForTests); Assert.False(view.ReviewButtonForTests.Enabled);
        view.CancelPendingWork(); await view.LoadAsync(); var status = view.StatusTextForTests;
        completion.SetResult(SuccessfulResult()); await work;
        Assert.True(operationToken.IsCancellationRequested);
        Assert.Equal(status, view.StatusTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled); Assert.False(view.BusyForTests);
        Assert.True(view.ReviewButtonForTests.Enabled);
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

    private static UpdateResult SuccessfulResult() => new()
    {
        DryRun = false,
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
