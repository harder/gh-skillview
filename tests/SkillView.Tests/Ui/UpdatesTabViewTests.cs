using System.Collections.Immutable;
using System.Threading.Tasks;
using SkillView.Gh;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui.Tabs;
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
            snapshotLoader: () => ++loadCount == 1 ? first.Task : second.Task,
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
            snapshotLoader: () => Task.FromResult(SnapshotWithSkill("loaded")),
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
        Func<Task<InventorySnapshot>> snapshotLoader)
    {
        var logger = new Logger(LogLevel.Debug);
        return new UpdatesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: snapshotLoader,
            updateServiceFactory: static () => throw new NotSupportedException(),
            ghPathProvider: static () => "/usr/bin/gh",
            logger: logger,
            onLeaveTab: static () => { },
            onUpdateApplied: static () => { });
    }

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
