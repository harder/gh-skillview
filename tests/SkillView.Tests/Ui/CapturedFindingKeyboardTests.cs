using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class CapturedFindingKeyboardTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task RegistryOnlyCannotActivateAnInstallationOrAcknowledgeADifference(int width, int height)
    {
        using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var calls = 0;
        var record = new SkillPackage("fixture/source", "github", null, null, null)
        { Provider = "gh-vercel-global", SchemaVersion = 3, Selector = "only-record", ContextRoot = "/fixtures/home", LockFilePath = "/fixtures/home/.agents/.skill-lock.json", EntryFingerprint = new('a', 64), RecordFingerprint = new('b', 64) };
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { OwnershipRecords = [record] }), () => { },
            onActivateEntry: _ => calls++, runTask: (work, _) => work().GetAwaiter().GetResult(),
            setIntentional: (_, _) => { calls++; return Task.FromResult("must not run"); })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); await view.LoadAsync();
        var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single(); table.SetFocus();
            Press(Key.Enter); Press(Key.Enter); Press(new Key('u')); Press(new Key('k'));
            Assert.Equal(0, calls);
            Assert.Contains("Not matched", view.SubViews.OfType<Markdown>().Single().Text.ToString());
            Assert.False(view.SubViews.OfType<Button>().Single(button => button.Text.ToString().Contains("both", StringComparison.Ordinal)).Enabled);
            if (width < 110) { Assert.False(table.Visible); Press(Key.Esc); Assert.True(table.Visible); Assert.Equal(0, table.GetSelectedRow()); }
        }
        finally { app.End(session!); }
        void Press(Key key) { app.Keyboard.RaiseKeyDownEvent(key); window.Draw(new DrawContext()); }
    }

    [Fact]
    public async Task IntentionalDifferencesDoNotHideExplicitUpdatesAndProtectedProblems()
    {
        var first = CapturedFindingTests.Copy("/fixtures/one/demo", "v1");
        var second = CapturedFindingTests.Copy("/fixtures/two/demo", "v2");
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        var family = MaintenanceQueueBuilder.Build(snapshot, TestContext.Current.CancellationToken).Findings.Single(f => f.Difference is not null);
        snapshot = snapshot with { IntentionalDifferences = new([new DifferenceRecord(1, 1, "set", family.Difference!)], null) };
        var broken = CapturedFindingTests.Copy("/fixtures/broken", "v1") with { Name = "broken", Validity = ValidityState.InvalidMetadata };
        snapshot = snapshot with { Skills = snapshot.Skills.Add(broken), ConfiguredRootsComplete = false };
        var availability = new UpdateAvailabilitySession();
        var request = availability.Begin(snapshot, 1, "fixture");
        Assert.True(availability.Publish(request, 1, "fixture", first, new(UpdateAvailabilityStatus.Available, "Explicit dry-run", DateTimeOffset.UtcNow)));
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { }, availability: availability);
        await view.LoadAsync();
        Assert.Contains(view.RowsForTests, row => row.Kind == "Intent");
        Assert.Contains(view.RowsForTests, row => row.Kind == "Problem");
        Assert.Contains(view.RowsForTests, row => row.Kind == "Updates" && row.Entry.Subject.InstallationId == first.InstallationId);
        Assert.Contains("Scan incomplete", view.StatusTextForTests);
    }

    [Fact]
    public async Task LeavingCancelsPendingAcknowledgementAndRejectsItsLateUiPublication()
    {
        var first = CapturedFindingTests.Copy("/fixtures/one/demo", "v1"); var second = CapturedFindingTests.Copy("/fixtures/two/demo", "v2");
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new List<Task>(); CancellationToken selectedToken = default;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { },
            runTask: (task, _) => work.Add(task()), setIntentional: (_, request) => { selectedToken = request.Token; entered.SetResult(); return release.Task; });
        await view.LoadAsync();
        view.SubViews.OfType<TableView>().Single().NewKeyDownEvent(new Key('k'));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        view.CancelPendingLoad(); view.Visible = false;
        Assert.True(selectedToken.IsCancellationRequested);
        release.SetResult("Late acknowledgement must not publish"); await Task.WhenAll(work);
        Assert.DoesNotContain("Late acknowledgement", view.SubViews.OfType<Markdown>().Single().Text.ToString());
        Assert.Single(view.RowsForTests);
    }
}
