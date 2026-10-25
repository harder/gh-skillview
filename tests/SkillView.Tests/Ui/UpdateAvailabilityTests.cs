using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class UpdateAvailabilityTests
{
    [Theory]
    [InlineData("", "Unknown")]
    [InlineData("new upstream wording", "Unknown")]
    [InlineData("All skills are up to date.", "NoUpdate")]
    [InlineData("demo: up-to-date", "NoUpdate")]
    [InlineData("demo: failed but up-to-date", "Unknown")]
    [InlineData("✗ All skills are up to date.", "Unknown")]
    [InlineData("! demo (owner/repo) abc > def [main]", "Unknown")]
    [InlineData("would update demo v1 to v2 but failed", "Unknown")]
    [InlineData("Skipping demo: HTTP 429\nAll skills are up to date.", "Unknown")]
    [InlineData("warning: HTTP 403\nAll skills are up to date.", "Unknown")]
    [InlineData("demo (owner/repo) abc > def [main]", "Available")]
    [InlineData("  • demo (owner/repo)  > a209a7ed [v2.103.2]\n\n1 update(s) available:\n", "Available")]
    [InlineData("demo (owner/repo) abc > def [main]\n2 update(s) available:", "Unknown")]
    [InlineData("other (owner/repo) abc > def [main]", "Unknown")]
    [InlineData("demo (wrong/repo) abc > def [main]", "Unknown")]
    [InlineData("demo (owner/repo) abc > def [main]\nAll skills are up to date.", "Unknown")]
    [InlineData("demo is pinned to v1 (skipped)", "Unknown")]
    [InlineData("Skill demo not found in owner/repo\nAll skills are up to date.", "Unknown")]
    public void OnlyUnambiguousExactOutputEstablishesAvailability(string output, string expected)
    {
        var result = Result(output);
        var observation = SkillUpdateCoordinator.ClassifyAvailability(result, "demo", "https://github.com/owner/repo");
        Assert.Equal(expected, observation.Status.ToString());
        Assert.Contains(output, observation.Diagnostic);
        Assert.NotNull(observation.CheckedAt);
    }

    [Fact]
    public void FailedCanceledAndTruncatedOutputCannotEstablishCurrency()
    {
        var current = Result("All skills are up to date.");
        Assert.Equal(UpdateAvailabilityStatus.Failed, Classify(current with { Succeeded = false, ExitCode = 1 }).Status);
        Assert.Equal(UpdateAvailabilityStatus.Canceled, Classify(current with { Cancelled = true }).Status);
        Assert.Equal(UpdateAvailabilityStatus.Unknown, Classify(current with { OutputTruncated = true }).Status);
        Assert.Contains("integrity", Classify(current).Detail);
    }

    [Fact]
    public void ExactCopiesEvidenceContextGenerationsAndCancellationBoundPublication()
    {
        var first = MaintenanceQueueTests.Skill("/first/demo", "https://github.com/owner/repo");
        var second = first with { ResolvedPath = "/second/demo" };
        var state = new UpdateAvailabilitySession();
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        var request = state.Begin(snapshot, 1, "home-a;gh-version-a");
        Assert.True(state.Publish(request, 1, "home-a;gh-version-a", first, Classify(Result("demo (owner/repo) abc > def [main]"))));
        Assert.Equal(UpdateAvailabilityStatus.Available, state.Get(first).Status);
        Assert.Equal(UpdateAvailabilityStatus.Checking, state.Get(second).Status);
        Assert.False(state.Publish(request, 1, "home-b;gh-version-a", second, Classify(Result("All skills are up to date."))));
        Assert.False(state.Publish(request, 2, "home-a;gh-version-a", second, Classify(Result("All skills are up to date."))));
        state.Cancel();
        Assert.Equal(UpdateAvailabilityStatus.Available, state.Get(first).Status);
        Assert.Equal(UpdateAvailabilityStatus.Canceled, state.Get(second).Status);
        Assert.False(state.Publish(request, 1, "home-a;gh-version-a", second, Classify(Result("All skills are up to date."))));
        request = state.Begin(snapshot, 2, "home-b;gh-version-b");
        state.Publish(request, 2, "home-b;gh-version-b", first, Classify(Result("All skills are up to date.")));
        var changed = first with { FrontMatter = first.FrontMatter with { GithubRef = "new-ref" } };
        Assert.Equal(UpdateAvailabilityStatus.Stale, state.Get(changed).Status);
        Assert.Contains("No upstream update", state.Get(changed).Reason);
        state.Invalidate();
        Assert.False(state.IsCurrent(request));
        Assert.Equal(UpdateAvailabilityStatus.Stale, state.Get(second).Status);
        Assert.False(state.Publish(request, 2, "home-b;gh-version-b", first, Classify(Result("All skills are up to date."))));
    }

    [Fact]
    public async Task MainViewsShareVisibleResultsWithoutReloadingOrChangingSelectionAndReviewExactCopy()
    {
        var first = MaintenanceQueueTests.Skill("/first/demo", "https://github.com/owner/repo");
        var second = first with { ResolvedPath = "/second/demo" };
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        var state = new UpdateAvailabilitySession();
        var checkCalls = 0;
        InstalledSkill? reviewed = null;
        MaintenanceQueueEntry? finding = null;
        using var installed = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("Availability must not reload inventory"), (_, _, _) => Task.CompletedTask,
            () => { }, () => { }, availability: state, onCheckUpdates: () => checkCalls++, onReviewUpdate: skill => reviewed = skill);
        installed.LoadSeeded(snapshot);
        var table = installed.SubViews.OfType<TableView>().Single();
        table.SetSelectedRow(1);
        var tableSource = table.Table;
        using var issues = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(snapshot), () => { }, onActivateEntry: entry => finding = entry,
            availability: state, onCheckUpdates: () => checkCalls++);
        await issues.LoadAsync();
        var request = state.Begin(snapshot, 1, "ctx");
        installed.RefreshAvailability();
        Assert.True(table.Visible);
        installed.FocusList();
        installed.NewKeyDownEvent(new Key('C'));
        Assert.Equal(1, checkCalls);
        state.Publish(request, 1, "ctx", second, Classify(Result("demo (owner/repo) abc > def [main]")));
        installed.RefreshAvailability(); issues.RefreshAvailability();
        Assert.Same(tableSource, table.Table);
        Assert.Equal(second.InstallationId, installed.GetSelectedSkill()!.InstallationId);
        Assert.Contains("Update available", installed.DetailTextForTests);
        installed.NewKeyDownEvent(Key.Enter);
        Assert.Equal(second.InstallationId, reviewed!.InstallationId);
        var issueTable = issues.SubViews.OfType<TableView>().Single();
        var updateRow = Enumerable.Range(0, issueTable.Table!.Rows).Single(i => (string)issueTable.Table[i, 0] == "Updates");
        issueTable.SetSelectedRow(updateRow);
        issueTable.NewKeyDownEvent(Key.Enter);
        Assert.Equal(second.InstallationId, finding!.Subject.InstallationId);
        Assert.Equal("upstream-update", finding.Issue);
        Assert.Contains("Checking updates", issues.StatusTextForTests);
        state.Invalidate();
        installed.RefreshAvailability(); issues.RefreshAvailability();
        Assert.Contains("Stale", installed.DetailTextForTests);
        Assert.DoesNotContain(Enumerable.Range(0, issueTable.Table.Rows), i => (string)issueTable.Table[i, 0] == "Updates");
    }

    [Fact]
    public async Task ChecksVisitEveryCopyPast128WithAtMostFourWorkersAndImmediateCancellation()
    {
        var skills = Enumerable.Range(0, 137).Select(i => MaintenanceQueueTests.Skill("/copies/" + i, "https://github.com/owner/repo")).ToArray();
        var visited = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var active = 0;
        var maximum = 0;
        await SkillViewWorkflowCoordinator.CheckCopiesAsync(skills, async (skill, token) =>
        {
            var running = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); } while (previous < running && Interlocked.CompareExchange(ref maximum, running, previous) != previous);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Assert.True(visited.TryAdd(skill.InstallationId, true));
            Interlocked.Decrement(ref active);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(137, visited.Count);
        Assert.InRange(maximum, 1, 4);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = SkillViewWorkflowCoordinator.CheckCopiesAsync(skills, async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 4) entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(4, started);
    }

    [Fact]
    public async Task ShutdownAndRetiredRequestsSuppressQueuedResultDelivery()
    {
        var skill = MaintenanceQueueTests.Skill("/copies/demo", "https://github.com/owner/repo");
        var state = new UpdateAvailabilitySession();
        var request = state.Begin(InventorySnapshot.Empty with { Skills = [skill] }, 1, "context");
        Action? queued = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dispatch = SkillViewApp.AwaitDispatchAsync(action => queued = action,
            () => state.Publish(request, 1, "context", skill, Classify(Result("All skills are up to date."))), lifetime.Token);
        lifetime.Cancel();
        queued!();
        Assert.False(await dispatch);
        Assert.Equal(UpdateAvailabilityStatus.Checking, state.Get(skill).Status);
        state.Invalidate();
        queued!();
        Assert.Equal(UpdateAvailabilityStatus.Stale, state.Get(skill).Status);
    }

    private static UpdateAvailability Classify(UpdateResult result) => SkillUpdateCoordinator.ClassifyAvailability(result, "demo", "https://github.com/owner/repo");
    private static UpdateResult Result(string output) => new()
    {
        DryRun = true, Succeeded = true, ExitCode = 0, StdOut = output, StdErr = "", ErrorMessage = null,
        CommandLine = [], Entries = GhSkillUpdateService.ParseEntries(output, true)
    };
}
