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
    [Fact]
    public void DescribeCapabilityState_DisablesUnsupportedFlags()
    {
        var state = UpdatesTabView.DescribeCapabilityState(CapabilityProfile.Empty);

        Assert.False(state.SupportsAll);
        Assert.Equal("_all (not supported)", state.AllLabel);
        Assert.False(state.SupportsForce);
        Assert.False(state.SupportsUnpin);
        Assert.False(state.SupportsYes);
        Assert.Equal("yes (needs gh --yes)", state.YesLabel);
        Assert.False(state.YesDefaultChecked);
        Assert.False(state.SupportsDryRun);
    }

    [Fact]
    public void DescribeCapabilityState_EnablesSupportedFlags()
    {
        var caps = new CapabilityProfile
        {
            SkillSubcommandPresent = true,
            ListSubcommandPresent = false,
            SearchFlags = ImmutableHashSet<string>.Empty,
            InstallFlags = ImmutableHashSet<string>.Empty,
            UpdateFlags = ImmutableHashSet.Create("--all", "--force", "--unpin", "--yes", "--dry-run"),
            ListFlags = ImmutableHashSet<string>.Empty,
            PreviewFlags = ImmutableHashSet<string>.Empty,
        };

        var state = UpdatesTabView.DescribeCapabilityState(caps);

        Assert.True(state.SupportsAll);
        Assert.Equal("_all", state.AllLabel);
        Assert.True(state.SupportsForce);
        Assert.True(state.SupportsUnpin);
        Assert.True(state.SupportsYes);
        Assert.Equal("_yes", state.YesLabel);
        Assert.True(state.YesDefaultChecked);
        Assert.True(state.SupportsDryRun);
    }

    [Fact]
    public void RefreshCapabilities_UpdatesLiveControls_WhenProbeDataArrivesLater()
    {
        var caps = CapabilityProfile.Empty;
        var view = CreateUpdatesTab(() => caps, () => Task.FromResult(InventorySnapshot.Empty));

        Assert.False(view.AllBoxForTests.Enabled);
        Assert.False(view.DryRunButtonForTests.Enabled);

        caps = SupportedUpdateCapabilities();
        view.RefreshCapabilities();

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
            () => SupportedUpdateCapabilities(),
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

    private static UpdatesTabView CreateUpdatesTab(
        Func<CapabilityProfile> capabilitiesProvider,
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
            capabilitiesProvider: capabilitiesProvider,
            logger: logger,
            onLeaveTab: static () => { },
            onUpdateApplied: static () => { });
    }

    private static CapabilityProfile SupportedUpdateCapabilities() => new()
    {
        SkillSubcommandPresent = true,
        ListSubcommandPresent = false,
        SearchFlags = ImmutableHashSet<string>.Empty,
        InstallFlags = ImmutableHashSet<string>.Empty,
        UpdateFlags = ImmutableHashSet.Create("--all", "--force", "--unpin", "--yes", "--dry-run"),
        ListFlags = ImmutableHashSet<string>.Empty,
        PreviewFlags = ImmutableHashSet<string>.Empty,
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
