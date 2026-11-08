using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InlineAdapterTests
{
    private static ChangesQueueRow Leaf(bool cleanup = true, bool reviewOnly = false)
    {
        var skill = CapturedFindingTests.Copy("/fixture/exact/demo", "refs/tags/v1");
        var subject = OperationSubject.From(skill);
        var entry = new MaintenanceQueueEntry("exact-entry", cleanup ? "EmptyDirectory" : "recorded-observation", "demo", subject,
            [subject.Path], "fixture", "Frozen observation", "review", false, false)
        { Cleanup = cleanup ? new(CleanupClassifier.CandidateKind.EmptyDirectory, subject.Path, "Fixture", null, reviewOnly) : null };
        var finding = new CapturedFinding(entry.Id, entry.Issue, "demo", FindingClass.Problem, "fixture", "Exact fixture", new FindingCopies([subject]),
            DateTimeOffset.UtcNow, false, [cleanup ? FindingNavigation.ReviewCleanup : FindingNavigation.ReviewUpdate])
        { Entries = [entry] };
        return new("Problem", "Exact fixture", [entry]) { Finding = finding, LocationLabels = [subject.Path] };
    }

    private static ChangesQueueRow Family()
    {
        var selected = CapturedFindingTests.Copy("/fixture/exact/demo", "refs/tags/v1");
        var peer = CapturedFindingTests.Copy("/fixture/peer/demo", "refs/tags/v2");
        var snapshot = InventorySnapshot.Empty with { Skills = [selected, peer] };
        var queue = MaintenanceQueueBuilder.Build(snapshot, TestContext.Current.CancellationToken);
        var family = Assert.Single(ChangesQueueBuilder.Build(queue, TestContext.Current.CancellationToken).Rows, row => row.IsFamily);
        Assert.Equal(2, family.Entries.Count);
        Assert.Equal(family.Entries.Count, family.LocationLabels.Count);
        Assert.NotNull(family.Finding!.Difference);
        return family;
    }

    private static IEnumerable<ChangesQueueRow> Rejected()
    {
        var row = Leaf(); var finding = row.Finding!; var entry = row.Entries[0];
        yield return row with { Entries = [] };
        yield return row with { Entries = [entry, entry] };
        yield return row with { Finding = finding with { Entries = [] } };
        yield return row with { Finding = finding with { Entries = [entry, entry] } };
        yield return row with { Finding = finding with { Navigation = [FindingNavigation.ReviewCleanup, FindingNavigation.ReviewUpdate] } };
        yield return row with { Finding = finding with { Navigation = [FindingNavigation.InspectRegistry] } };
        yield return row with { Finding = finding with { Navigation = [FindingNavigation.InspectCopies] } };
        yield return row with { Finding = finding with { Navigation = [] } };
        yield return row with { Finding = finding with { Subject = new FindingCopies([entry.Subject, entry.Subject]) } };
        yield return row with { Finding = finding with { Subject = new FindingCopies([entry.Subject with { InstallationId = "another" }]) } };
        yield return row with { Finding = finding with { Subject = new FindingCopies([entry.Subject with { Path = "/fixture/exact/DEMO" }]) } };
        yield return row with { Finding = finding with { Id = entry.Id.ToUpperInvariant() } };
        yield return row with { Finding = finding with { Entries = [entry with { Id = "different" }] } };
        yield return row with { Entries = [entry with { Cleanup = null }] };
        yield return row with { Finding = finding with { Navigation = [FindingNavigation.ReviewUpdate] } };
        yield return row with { Finding = finding with { Difference = new("family", "evidence") } };
        yield return row with { Finding = finding with { Intentional = true } };
        foreach (var classification in new[] { FindingClass.Information, FindingClass.Coverage, FindingClass.Update })
            yield return row with { Finding = finding with { Classification = classification } };
        var record = new SkillPackage("fixture/source", "github", null, null, null);
        yield return row with { Entries = [], Finding = finding with { Subject = new FindingRegistry(record), Navigation = [FindingNavigation.InspectRegistry] } };
        yield return row with { Entries = [], Finding = finding with { Subject = new FindingRegistry(record), Navigation = [FindingNavigation.InspectRegistry], Classification = FindingClass.Coverage } };
        yield return row with { Finding = finding with { Navigation = [FindingNavigation.InspectCopies], Intentional = true } };
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task TypedSingletonRoutesAndGuidedEligibilityMatchLegacyWithoutNewAuthority(int width, int height)
    {
        foreach (var row in new[] { Leaf(), Leaf(reviewOnly: true), Leaf(cleanup: false) })
        {
            var entry = row.Entries[0];
            Assert.Same(entry, row.ExactEntry);
            Assert.Equal("finding:" + entry.Id, row.Key);
            var queue = new MaintenanceQueue(DateTimeOffset.UtcNow, false, [], [entry]);
            var legacy = ChangesQueueBuilder.Build(queue, TestContext.Current.CancellationToken).Rows;
            var typed = ChangesQueueBuilder.Build(queue with { Findings = [row.Finding!] }, TestContext.Current.CancellationToken).Rows;
            Assert.Equal(legacy.Select(item => item.ExactEntry!.Id).Order(), typed.Select(item => item.ExactEntry!.Id).Order());
            foreach (var key in new[] { Key.Enter, new Key('u'), new Key('c') })
            {
                if (entry.Cleanup is null && key.AsRune.Value == 'c') continue;
                await ViewFixture.Run(width, height, row, async fixture =>
                {
                    await fixture.PressAsync(key);
                    if (width == 80 && key.KeyCode == KeyCode.Enter)
                    {
                        Assert.Empty(fixture.Actions);
                        Assert.Empty(fixture.Guided);
                        Assert.False(fixture.Sequence.HasActiveSequence);
                        var table = fixture.View.SubViews.OfType<TableView>().Single();
                        var detail = fixture.View.SubViews.OfType<Markdown>().Single();
                        Assert.False(table.Visible);
                        Assert.True(detail.Visible);
                        Assert.True(detail.HasFocus);
                        Assert.Contains(entry.Subject.Path, detail.Text);
                        await fixture.PressAsync(Key.Enter);
                    }
                    var received = Assert.Single(fixture.Actions);
                    Assert.Same(entry, received);
                    Assert.Equal(entry.Subject.InstallationId, received.Subject.InstallationId);
                    Assert.Equal(entry.Subject.Path, received.Subject.Path);
                });
            }
            await ViewFixture.Run(width, height, row, async guided =>
            {
                var start = new Key('g'); await guided.PressAsync(start);
                var eligible = entry.Cleanup is { ReviewOnly: false };
                Assert.Equal(eligible, start.Handled);
                Assert.Equal(eligible, guided.Sequence.HasActiveSequence);
                if (eligible)
                {
                    await guided.PressAsync(new Key('n'));
                    Assert.Same(entry, Assert.Single(guided.Guided));
                }
                else Assert.Empty(guided.Guided);
            });
        }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task MalformedTypedRowsCannotRouteCleanupUpdateOrGuidedWork(int width, int height)
    {
        foreach (var row in Rejected())
        {
            Assert.Null(row.ExactEntry);
            foreach (var key in new[] { new Key('c'), new Key('u'), new Key('g') })
            {
                await ViewFixture.Run(width, height, row, async fixture =>
                {
                    await fixture.PressAsync(key);
                    Assert.Empty(fixture.Actions);
                    Assert.Empty(fixture.Guided);
                    Assert.False(fixture.Sequence.HasActiveSequence);
                    if (key.AsRune.Value == 'g') Assert.False(key.Handled);
                });
            }
        }
    }

    [Fact]
    public void FindingBindingRestrictsObservedChoicesAndRejectsChangedDifferenceOrMissingMember()
    {
        var first = CapturedFindingTests.Copy("/fixture/first/demo", "v1");
        var second = CapturedFindingTests.Copy("/fixture/second/demo", "v2");
        var third = CapturedFindingTests.Copy("/fixture/third/demo", "v3") with { FrontMatter = second.FrontMatter with { Upstream = "other/source" } };
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second, third] };
        var finding = MaintenanceQueueBuilder.Build(snapshot, TestContext.Current.CancellationToken).Findings.Single(item => item.Kind == "recorded-revisions");
        var binding = FindingBinding.Capture(finding)!;
        Assert.True(binding.IsCurrent(snapshot, TestContext.Current.CancellationToken));
        var request = InlineCopyRequest.Capture(snapshot, 1, "context", 1, first, new object(), binding)!;
        Assert.Equal(binding.Allowed.Order(), request.Choices.Select(copy => (copy.InstallationId, copy.ResolvedPath)).Order());
        Assert.False((binding with { Difference = new("changed", "changed") }).IsCurrent(snapshot, TestContext.Current.CancellationToken));
        Assert.False(binding.IsCurrent(snapshot with { Skills = [first, third] }, TestContext.Current.CancellationToken));
        Assert.Null(InlineCopyRequest.Capture(snapshot, 1, "context", 1, third, new object(), binding));
        Assert.Contains(third, InlineCopyRequest.Capture(snapshot, 1, "context", 1, first, new object())!.Choices);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task FamiliesKeepIndependentChildKeysAndBackReturnsToTheExactParent(int width, int height)
    {
        await UiHost.Run(width, height, async host =>
        {
            var first = CapturedFindingTests.Copy("/fixture/one/demo", "v1");
            var second = CapturedFindingTests.Copy("/fixture/two/demo", "v2");
            var third = CapturedFindingTests.Copy("/fixture/three/demo", "v3") with { FrontMatter = second.FrontMatter with { Upstream = "other/source" } };
            var snapshot = InventorySnapshot.Empty with { Skills = [first, second, third] };
            var guided = new GuidedRepairSequence();
            var window = host.Window;
            var view = new ChangesTabView(host.DispatchAsync, _ => Task.FromResult(snapshot), () => { },
                lifetimeToken: host.Token, guided: guided, runTask: host.RunTask, validateFinding: (_, _) => Task.FromResult(true))
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); await host.WaitAsync(view.LoadAsync(), "load Issues");
            var table = view.SubViews.OfType<TableView>().Single();
            var families = view.RowsForTests.Where(row => row.IsFamily).ToArray(); Assert.Equal(2, families.Length);
            foreach (var family in families)
            {
                Select(family.Key); Press(Key.Enter);
                var children = view.RowsForTests.Where(row => row.ParentKey == family.Key).ToArray();
                Assert.Equal(family.Finding!.Entries.Select(entry => (entry.Subject.InstallationId, entry.Subject.Path)),
                    children.Select(child => (child.ExactEntry!.Subject.InstallationId, child.ExactEntry.Subject.Path)));
            }
            var shared = view.RowsForTests.Where(row => row.IsExactChild && row.ExactEntry!.Subject.InstallationId == first.InstallationId).ToArray();
            Assert.Equal(2, shared.Length); Assert.NotEqual(shared[0].Key, shared[1].Key);
            var selected = shared[1]; Select(selected.Key); Press(Key.Enter);
            await host.SettleAsync("publish exact child comparison");
            var copies = view.SubViews.OfType<InlineCopiesView>().Single(); Assert.True(copies.Open);
            Assert.Equal(selected.Finding!.Id, copies.Request!.Finding!.Id);
            Assert.Equal(selected.Finding.Entries.Select(entry => (entry.Subject.InstallationId, entry.Subject.Path)).Order(),
                copies.Request.Choices.Select(copy => (copy.InstallationId, copy.ResolvedPath)).Order());
            Press(Key.Esc); Assert.Equal(selected.Key, view.RowsForTests[table.GetSelectedRow()].Key);
            view.RefreshAvailability(); Assert.Equal(selected.Key, view.RowsForTests[table.GetSelectedRow()].Key);
            await host.WaitAsync(view.LoadAsync(), "load Issues"); Assert.Equal(selected.Key, view.RowsForTests[table.GetSelectedRow()].Key);
            Select(families[0].Key); Press(new Key(KeyCode.CursorLeft));
            Assert.DoesNotContain(view.RowsForTests, row => row.ParentKey == families[0].Key);
            Assert.Contains(view.RowsForTests, row => row.ParentKey == families[1].Key);
            var start = new Key('g'); Press(start); Assert.False(start.Handled); Assert.False(guided.HasActiveSequence);
            void Select(string key) { table.SetSelectedRow(view.RowsForTests.ToList().FindIndex(row => row.Key == key)); table.SetFocus(); }
            void Press(Key key) { host.Press(key); }
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task RegistryOnlyRowSurvivesF2AvailabilityReloadAndReturnWithoutAnOperation(int width, int height)
    {
        await UiHost.Run(width, height, async host =>
        {
            var record = new SkillPackage("fixture/source", "github", null, null, null)
            { Provider = "gh-vercel-global", SchemaVersion = 3, Selector = "registry-only", ContextRoot = "/fixture/home", LockFilePath = "/fixture/home/.agents/.skill-lock.json", EntryFingerprint = new('a', 64), RecordFingerprint = new('b', 64) };
            var snapshot = InventorySnapshot.Empty with { OwnershipRecords = [record] };
            var operations = 0; var sequence = new GuidedRepairSequence();
            var window = host.Window;
            var view = new ChangesTabView(host.DispatchAsync, _ => Task.FromResult(snapshot), () => { },
                lifetimeToken: host.Token, guided: sequence, onActivateEntry: _ => operations++, setIntentional: (_, _) => { operations++; return Task.FromResult("unreachable"); },
                availability: new UpdateAvailabilitySession())
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); await host.WaitAsync(view.LoadAsync(), "load Issues");
            var table = view.SubViews.OfType<TableView>().Single(); var row = Assert.Single(view.RowsForTests);
            Assert.Empty(row.Entries); Assert.Null(row.ExactEntry); Assert.StartsWith("finding:registry:", row.Key);
            table.SetFocus(); Press(Key.Enter); Press(Key.F2); view.RefreshAvailability();
            await host.WaitAsync(view.LoadAsync(), "load Issues"); Assert.Equal(row.Key, view.RowsForTests[table.GetSelectedRow()].Key);
            Press(new Key('u')); Press(new Key('k')); var start = new Key('g'); Press(start);
            Assert.False(start.Handled); Assert.False(sequence.HasActiveSequence); Assert.Equal(0, operations);
            Assert.Contains("gh registry observation", view.SubViews.OfType<Markdown>().Single().Text);
            Press(Key.Esc); Assert.True(table.Visible); Assert.Equal(row.Key, view.RowsForTests[table.GetSelectedRow()].Key);
            void Press(Key key) { host.Press(key); }
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task GuidedKeysAreContextualAndPreferenceKeysCannotCancelAnOpenGuidedRequest(int width, int height)
    {
        await UiHost.Run(width, height, async host =>
        {
            var row = Leaf(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); host.OwnGate(release);
            CancellationToken guideToken = default; var preferences = 0; var actions = 0; var works = new List<Task>();
            var sequence = new GuidedRepairSequence();
            var availability = new UpdateAvailabilitySession();
            var window = host.Window;
            var placeholder = CapturedFindingTests.Copy("/fixture/broken", "v1") with { Validity = ValidityState.InvalidMetadata };
            var view = new ChangesTabView(host.DispatchAsync,
                _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [placeholder] }), () => { }, lifetimeToken: host.Token, guided: sequence,
                onActivateEntry: _ => actions++, setIntentional: (_, _) => { preferences++; return Task.FromResult("unreachable"); },
                resetIntentional: _ => { preferences++; return Task.CompletedTask; }, availability: availability,
                runTask: (work, label) => works.Add(host.StartWork(work, label)), activateGuided: async (_, request) =>
                { guideToken = request.Token; host.Stage("guided request awaiting held release"); await release.Task.WaitAsync(guideToken); })
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); await host.WaitAsync(view.LoadAsync(), "load Issues"); var rows = Assert.IsType<ChangesQueueRow[]>(view.RowsForTests); Assert.Single(rows); rows[0] = row;

            var table = view.SubViews.OfType<TableView>().Single(); table.SetFocus();
            foreach (var value in new[] { 'n', 's', 'x' }) { var inactive = new Key(value); Press(inactive); Assert.False(inactive.Handled); }
            var start = new Key('g'); Press(start); Assert.True(start.Handled); Assert.True(sequence.HasActiveSequence);
            var again = new Key('g'); Press(again); Assert.False(again.Handled);
            foreach (var text in new[] { ActionLabels.Guided.ButtonText, ActionLabels.ReviewNext.ButtonText, ActionLabels.SkipGuided.ButtonText, ActionLabels.StopGuided.ButtonText })
            {
                var button = view.SubViews.OfType<Button>().Single(control => control.Text == text);
                Assert.True(button.Visible); Assert.InRange(button.Frame.Right, 1, view.Viewport.Width);
            }
            Press(new Key('n')); Assert.True(guideToken.CanBeCanceled); Assert.NotNull(sequence.Active);
            var active = sequence.Active;
            var family = Family();
            Assert.Contains(family.Entries, member => member.Subject.InstallationId == active!.Entry.Subject.InstallationId
                && member.Subject.Path == active.Entry.Subject.Path);
            rows[0] = family; view.RefreshGuided();
            Assert.Same(active, sequence.Active);
            foreach (var value in new[] { 'k', 'K', 'c' })
            {
                Press(new Key(value)); Assert.False(guideToken.IsCancellationRequested); Assert.Equal(0, preferences); Assert.Equal(0, actions);
                Assert.Contains("Finish or Stop the guided review first", view.StatusTextForTests);
            }
            var blocked = new Key('g'); Press(blocked); Assert.False(blocked.Handled);
            Press(new Key('x')); Assert.True(guideToken.IsCancellationRequested);
            AssertStoppedView();
            release.TrySetResult();
            await host.WaitAsync(Task.WhenAll(works), "settle selected workers");
            await host.SettleAsync("settle nested publication");
            Assert.False(sequence.HasActiveSequence); Assert.Null(sequence.Active);
            var outcome = Assert.Single(sequence.Outcomes);
            Assert.Equal(row.ExactEntry!.Id, outcome.Id);
            Assert.Equal(row.ExactEntry.Subject.Path, outcome.Path);
            Assert.StartsWith("Skipped", outcome.Status); Assert.Null(outcome.SavedOperationId);
            AssertStoppedView();
            view.RefreshAvailability();
            AssertStoppedView();
            Assert.Same(outcome, Assert.Single(sequence.Outcomes));
            foreach (var value in new[] { 'n', 's', 'x' }) { var inactive = new Key(value); Press(inactive); Assert.False(inactive.Handled); }
            Assert.Equal(0, preferences); Assert.Equal(0, actions);
            void AssertStoppedView()
            {
                host.Stage("assert stopped visible summary and retained outcomes");
                foreach (var text in new[] { ActionLabels.ReviewNext.ButtonText, ActionLabels.SkipGuided.ButtonText, ActionLabels.StopGuided.ButtonText })
                    Assert.False(view.SubViews.OfType<Button>().Single(control => control.Text == text).Visible);
                var summary = Assert.Single(view.SubViews.OfType<Label>(), label => label.Text.StartsWith("Guided reviews:", StringComparison.Ordinal));
                Assert.True(summary.Visible);
                Assert.Contains("1 outcomes; 1 skipped; 0 remaining; stopped", summary.Text.ToString());
                Assert.DoesNotContain("Review next / Skip / Stop", summary.Text.ToString());
                Assert.InRange(summary.Frame.Right, 1, view.Viewport.Width);
            }
            void Press(Key key) { host.Press(key); }
        });
    }

    [Fact]
    public void HelpUsesExistingContextsAndOnlyAdvertisesDiffWhenVisible()
    {
        var issues = HelpOverlay.BuildMarkdown(ActionScreen.Issues);
        foreach (var key in new[] { "**Enter**", "**u / U**", "**k**", "**K**", "**g**", "**n**", "**s**", "**x**", "**F5**", "**F4**" }) Assert.Contains(key, issues);
        Assert.DoesNotContain("**i**", issues); Assert.DoesNotContain("Skip (k)", issues);
        Assert.DoesNotContain("**D / d**", HelpOverlay.BuildMarkdown(ActionScreen.UpdateReview));
        Assert.Contains("**D / d**", HelpOverlay.BuildMarkdown(ActionScreen.UpdateReview, sourceDiffVisible: true));
        Assert.Contains("separate confirmation", issues);
        var enter = Assert.Single(issues.Split('\n'), line => line.StartsWith("- **Enter** — ", StringComparison.Ordinal));
        Assert.Contains("Open: expand a family, open an exact child's comparison", enter);
        Assert.Contains("inspect registry guidance", enter);
        Assert.Contains("open an exact item's review (narrow layouts show details first); never applies a change", enter);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task KeepBothNeverSkipsAndSkipActsOnlyDuringTheWaitingSequence(int width, int height)
    {
        await UiHost.Run(width, height, async host =>
        {
            var sequence = new GuidedRepairSequence(); var writes = 0; var works = new List<Task>();
            var window = host.Window;
            var placeholder = CapturedFindingTests.Copy("/fixture/placeholder", "v1") with { Validity = ValidityState.InvalidMetadata };
            var view = new ChangesTabView(host.DispatchAsync,
                _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [placeholder] }), () => { }, lifetimeToken: host.Token, guided: sequence,
                runTask: (work, label) => works.Add(host.StartWork(work, label)), setIntentional: (finding, lease) =>
                { host.Observe(() => { Assert.True(lease.IsCurrent); Assert.NotNull(finding.Difference); }); writes++; return Task.FromResult("Fake preference receipt"); })
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); await host.WaitAsync(view.LoadAsync(), "load Issues"); var rows = Assert.IsType<ChangesQueueRow[]>(view.RowsForTests); Assert.Single(rows);
            var leaf = Leaf(); rows[0] = leaf;
            var table = view.SubViews.OfType<TableView>().Single(); table.SetFocus();
            Press(new Key('g')); Assert.True(sequence.HasActiveSequence);
            rows[0] = Family();
            Press(new Key('k'));
            await host.WaitAsync(Task.WhenAll(works), "settle selected workers");
            await host.SettleAsync("settle nested publication");
            Assert.Equal(1, writes); Assert.Empty(sequence.Outcomes);
            Assert.True(sequence.HasActiveSequence); Assert.Null(sequence.Active);
            table.SetFocus(); var skip = new Key('s'); Press(skip); Assert.True(skip.Handled);
            Assert.StartsWith("Skipped", Assert.Single(sequence.Outcomes).Status); Assert.False(sequence.HasActiveSequence);
            var inactive = new Key('s'); Press(inactive); Assert.False(inactive.Handled); Assert.Single(sequence.Outcomes);
            void Press(Key key) { host.Press(key); }
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task StartIsUnhandledWhileAnotherExactSelectionOwnsWork(int width, int height)
    {
        await UiHost.Run(width, height, async host =>
        {
            var sequence = new GuidedRepairSequence(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); host.OwnGate(release);
            var works = new List<Task>(); CancellationToken selectedToken = default;
            var window = host.Window;
            var placeholder = CapturedFindingTests.Copy("/fixture/placeholder", "v1") with { Validity = ValidityState.InvalidMetadata };
            var view = new ChangesTabView(host.DispatchAsync,
                _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [placeholder] }), () => { }, lifetimeToken: host.Token, guided: sequence,
                runTask: (work, label) => works.Add(host.StartWork(work, label)), activateEntryAsync: async (_, request) =>
                { selectedToken = request.Token; host.Stage("selected request awaiting held release"); await release.Task.WaitAsync(selectedToken); })
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); await host.WaitAsync(view.LoadAsync(), "load Issues"); var rows = Assert.IsType<ChangesQueueRow[]>(view.RowsForTests); Assert.Single(rows); rows[0] = Leaf();

            var table = view.SubViews.OfType<TableView>().Single(); table.SetFocus();
            host.Press(new Key('u')); Assert.True(selectedToken.CanBeCanceled);
            var start = new Key('g'); host.Press(start); Assert.False(start.Handled); Assert.False(sequence.HasSequence);
            var stop = new Key('x'); host.Press(stop); Assert.False(stop.Handled); Assert.False(selectedToken.IsCancellationRequested);
            release.SetResult();
            await host.WaitAsync(Task.WhenAll(works), "settle selected workers");
            await host.SettleAsync("settle nested publication");
            start = new Key('g'); host.Press(start); Assert.True(start.Handled); Assert.True(sequence.HasActiveSequence);
        });
    }

    private sealed class ViewFixture(UiHost host)
    {
        internal Window Window => host.Window;
        internal ChangesTabView View = null!;
        internal GuidedRepairSequence Sequence = new();
        internal List<MaintenanceQueueEntry> Actions = [];
        internal List<MaintenanceQueueEntry> Guided = [];

        internal static Task Run(int width, int height, ChangesQueueRow row, Func<ViewFixture, Task> scenario,
            [CallerMemberName] string test = "") => UiHost.Run(width, height, async host =>
        {
            var fixture = new ViewFixture(host);
            // Reproduce a driver resize after Run begins on every platform.
            // The test window must retain its requested narrow/wide viewport.
            host.App.Driver!.SetScreenSize(width + 40, height + 8);
            fixture.Window.SetNeedsLayout(); fixture.Window.Layout();
            var skill = CapturedFindingTests.Copy("/fixture/placeholder", "v1") with { Validity = ValidityState.InvalidMetadata };
            fixture.View = new ChangesTabView(host.DispatchAsync,
                _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [skill] }), () => { },
                lifetimeToken: host.Token, onActivateEntry: entry => fixture.Actions.Add(entry), guided: fixture.Sequence,
                runTask: host.RunTask, activateGuided: (item, lease) =>
                {
                    host.Observe(() => { Assert.True(lease.IsCurrent); }); fixture.Guided.Add(item.Entry);
                    fixture.Sequence.Complete(item, "Fixture review completed"); return Task.CompletedTask;
                })
            { Width = Dim.Fill(), Height = Dim.Fill() };
            fixture.Window.Add(fixture.View);
            await host.WaitAsync(fixture.View.LoadAsync(), "load fixture Issues");
            fixture.View.LoadRowsForTests([row]);
            Assert.Same(row, Assert.Single(fixture.View.RowsForTests));
            // The child was added after Run began. Apply its layout before
            // sending Enter; a zero-width viewport takes the wide action path.
            fixture.Window.Layout();
            Assert.True(fixture.View.Viewport.Width > 0);
            Assert.Equal(width < 110, fixture.View.Viewport.Width < 110);
            fixture.View.SubViews.OfType<TableView>().Single().SetFocus();
            await scenario(fixture);
        }, test);

        internal async Task PressAsync(Key key)
        {
            host.Press(key);
            await host.SettleAsync("settle fixture key publication");
        }
    }

    // All controls and their assertions stay on this dedicated UI thread. The ANSI loop
    // remains running while fake operations, queued publications and teardown complete.
    private sealed class UiHost : IDisposable
    {
        internal IApplication App { get; }
        internal Window Window { get; }
        internal CancellationToken Token => _lifetime.Token;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _caller;
        private readonly List<Task> _workers = [];
        private readonly List<Task> _awaited = [];
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Exception> _failures = [];
        private readonly string _case;
        private readonly string _diagnosticPath = Path.Combine(Path.GetTempPath(), $"skillview-inline-adapter-{Guid.NewGuid():N}.log");
        private readonly int _thread = System.Environment.CurrentManagedThreadId;
        private string _stage = "starting";
        private bool _started;
        private bool _draining;
        private bool _settled;
        private bool _diagnosticWriteFailed;

        private UiHost(int width, int height, string test, CancellationToken caller)
        {
            _caller = caller;
            _case = $"{test} {width}x{height}";
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller);
            App = Application.Create();
            try
            {
                App.Init("ansi"); App.Driver!.SetScreenSize(width, height);
                // The ANSI driver may re-observe the physical Windows console
                // when Run starts. Keep this fixture's requested geometry even
                // when the host console is wider than its simulated screen.
                Window = new Window { Width = width, Height = height };
            }
            catch { App.Dispose(); _lifetime.Dispose(); throw; }
        }

        internal static Task Run(int width, int height, Func<UiHost, Task> scenario,
            [CallerMemberName] string test = "")
        {
            var caller = TestContext.Current.CancellationToken;
            return Task.Factory.StartNew(() =>
            {
                using var host = new UiHost(width, height, test, caller);
                host.RunLoop(scenario);
            }, caller, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void RunLoop(Func<UiHost, Task> scenario)
        {
            // Iteration starts the scenario only after Run has installed the owning
            // MainLoopSyncContext. No incomplete UI work is synchronously blocked here.
            App.Iteration += Start;
            App.AddTimeout(TimeSpan.FromSeconds(10), () =>
            {
                Record(new TimeoutException($"{_case}: watchdog at {_stage}"));
                Mark("watchdog cancellation: " + _stage); _lifetime.Cancel(); return false;
            });
            try
            {
                Console.WriteLine($"InlineAdapter {_case}: diagnostic log {_diagnosticPath}");
                Mark("run loop"); App.Run(Window);
                if (!_settled) Record(new InvalidOperationException($"{_case}: loop ended before owned work settled at {_stage}"));
            }
            finally { App.Iteration -= Start; }
            if (_failures.Count > 0) throw new AggregateException(_failures);
            _caller.ThrowIfCancellationRequested();

            void Start(object? sender, EventArgs args)
            {
                if (_started) return;
                _started = true;
                // ExecuteScenario catches failures; its final RequestStop follows drain.
                _ = ExecuteScenario();
            }

            async Task ExecuteScenario()
            {
                try { Token.ThrowIfCancellationRequested(); await scenario(this); }
                catch (Exception ex)
                {
                    Mark(_caller.IsCancellationRequested ? "caller canceled" : "scenario failed");
                    Record(ex);
                }
                finally
                {
                    _draining = true;
                    Mark("cancel and drain owned work");
                    _lifetime.Cancel();
                    foreach (var gate in _gates) gate.TrySetResult();
                    // Caller cancellation cannot bypass cleanup. The separate bound
                    // reports incomplete teardown as failure, never as a passing scenario.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await DrainAsync().WaitAsync(cleanup.Token); }
                    catch (Exception ex) { Record(ex); }
                    _settled = _workers.Concat(_awaited).All(task => task.IsCompleted);
                    if (!_settled) Record(new InvalidOperationException($"{_case}: owned work did not drain at {_stage}"));
                    Mark(_settled ? "owned work settled; stop loop" : "incomplete drain; stop loop with failure");
                    App.RequestStop();
                }
            }
        }

        internal void OwnGate(TaskCompletionSource gate) { CheckThread(); _gates.Add(gate); }
        internal void RunTask(Func<Task> work, string label) => StartWork(work, label);
        internal Task StartWork(Func<Task> work, string label)
        {
            CheckThread(); Mark("start worker: " + label);
            Task task;
            try { task = work(); }
            catch (Exception ex) { Record(ex); task = Task.FromException(ex); }
            _workers.Add(task); return task;
        }

        internal async Task WaitAsync(Task work, string stage)
        {
            CheckThread(); Mark("await: " + stage); _awaited.Add(work);
            await work.WaitAsync(Token);
            CheckThread(); Mark("completed: " + stage);
        }

        internal async Task SettleAsync(string stage)
        {
            CheckThread(); Mark("await: " + stage);
            // The drain must never join itself through the awaited-operation inventory.
            await DrainAsync().WaitAsync(Token);
            CheckThread(); Mark("completed: " + stage);
        }

        internal async Task DrainAsync()
        {
            CheckThread();
            var count = -1;
            while (count != _workers.Count + _awaited.Count)
            {
                count = _workers.Count + _awaited.Count;
                var batch = _workers.Concat(_awaited).ToArray();
                try { await Task.WhenAll(batch); }
                catch (Exception) when (_draining)
                {
                    // Await every owned task before inspecting faults. Only cancellation
                    // from the canceled harness lifetime is expected during teardown.
                    foreach (var task in batch.Where(task => task.IsFaulted)) Record(task.Exception!);
                    if (!Token.IsCancellationRequested && batch.Any(task => task.IsCanceled))
                        Record(new InvalidOperationException($"{_case}: unexpected canceled worker"));
                }
                CheckThread();
            }
        }

        internal async Task DispatchAsync(Action action)
        {
            Token.ThrowIfCancellationRequested();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = Token.Register(() => done.TrySetCanceled(Token));
            App.Invoke(() =>
            {
                CheckThread();
                if (Token.IsCancellationRequested) { done.TrySetCanceled(Token); return; }
                try { action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            await done.Task;
        }

        internal void Observe(Action assertion)
        {
            CheckThread();
            try { assertion(); }
            catch (Exception ex) { Record(ex); throw; }
        }

        internal void Press(Key key)
        {
            CheckThread(); Mark("key event: " + key.KeyCode);
            App.Keyboard.RaiseKeyDownEvent(key); Window.Draw(new DrawContext());
        }

        private void CheckThread()
        {
            if (System.Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException($"{_case}: UI work left its owning thread at {_stage}");
        }
        internal void Stage(string stage) => Mark(stage);
        private void Mark(string stage)
        {
            CheckThread(); _stage = stage;
            var marker = $"{DateTimeOffset.UtcNow:O} InlineAdapter {_case} thread={_thread}: {stage}";
            // Close each write so a nonsettled run retains its last scenario stage even
            // when the test runner has not yet published captured console output.
            if (!_diagnosticWriteFailed)
            {
                try { File.AppendAllText(_diagnosticPath, marker + System.Environment.NewLine); }
                catch (Exception ex) { _diagnosticWriteFailed = true; Record(ex); }
            }
            Console.WriteLine(marker);
        }
        private void Record(Exception error) { CheckThread(); _failures.Add(error); }
        public void Dispose()
        {
            CheckThread(); _lifetime.Cancel();
            foreach (var gate in _gates) gate.TrySetResult();
            try { Window.Dispose(); }
            finally { try { App.Dispose(); } finally { _lifetime.Dispose(); } }
        }
    }
}
