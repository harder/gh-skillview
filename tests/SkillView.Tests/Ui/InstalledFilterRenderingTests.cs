using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
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
public sealed class InstalledFilterRenderingTests
{
    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(120, 36, false)]
    [InlineData(80, 24, true)]
    [InlineData(120, 36, true)]
    public void EmptyInventoryShowsHonestCoverageAndKeyboardSearchWithCopyActionsGated(int width, int height, bool partial)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var searches = 0; var actions = 0; var loads = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(InventorySnapshot.Empty); }, (_, _, _) => { actions++; return Task.CompletedTask; }, () => searches++,
            onPackageCheck: (_, _, _) => actions++, onLocalReimport: _ => actions++, onLocalSourceSwitch: _ => actions++,
            availability: new UpdateAvailabilitySession(), onCheckUpdates: () => actions++, onReviewUpdate: _ => actions++)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        var snapshot = InventorySnapshot.Empty with
        {
            ConfiguredRoots = [.. Enumerable.Range(0, 79).Select(i => new ScanRoot("/locations/" + i, Scope.User, "codex"))],
            ConfiguredRootsComplete = !partial,
            Diagnostics = ScanDiagnostics.Empty with { InaccessiblePaths = partial ? ["/locations/unknown"] : [] }
        };
        window.Add(view); view.LoadSeeded(snapshot);
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value == 'C') actions++; };
        var session = app.Begin(window);
        try
        {
            Draw(); view.FocusList();
            Assert.Contains("No installed skills found", view.DetailTextForTests);
            Assert.Contains("0 locations scanned; 79 locations configured", view.DetailTextForTests);
            Assert.Equal(partial, view.DetailTextForTests.Contains("other copies may exist", StringComparison.Ordinal));
            Assert.DoesNotContain("Selected copy is missing", view.DetailTextForTests);
            Assert.DoesNotContain("No matching installed", view.DetailTextForTests);
            var detail = view.SubViews.OfType<Markdown>().Single();
            Assert.Equal(0, detail.Frame.X); Assert.True(detail.Frame.Width >= width - 4);
            var buttons = view.SubViews.OfType<Button>().ToArray();
            var search = buttons.Single(b => b.Text == "Search skills (1)");
            var coverage = buttons.Single(b => b.Text == "Coverage");
            Assert.True(search.Visible); Assert.True(search.Enabled);
            Assert.True(search.Frame.Bottom <= view.Frame.Height); Assert.True(search.Frame.Right <= view.Frame.Width);
            foreach (var label in new[] { "Details", "Review update (u)", "More actions" })
            { var button = buttons.Single(b => b.Text == label); Assert.False(button.Visible); Assert.False(button.Enabled); }
            Assert.False(buttons.Single(b => b.Text == "Check all updates (C)").Visible);
            foreach (var key in new[] { 'K', 'u', 'C', 'R', 'T' }) app.Keyboard.RaiseKeyDownEvent(new Key(key));
            Assert.False(view.TryStartSelectedRemoveForTests()); Assert.Null(view.GetSelectedSkill()); Assert.Equal(0, actions);
            coverage.SetFocus(); Assert.True(view.HandleLocalNavigation(Key.Tab)); Assert.True(search.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(Key.Enter); Assert.Equal(1, searches);
            view.FocusList(); app.Keyboard.RaiseKeyDownEvent(new Key('1')); Assert.Equal(2, searches);
            view.FocusFilterForTests(); app.Keyboard.RaiseKeyDownEvent(new Key('1'));
            Assert.Equal("1", view.GetFilterText()); Assert.Equal(2, searches);
            view.ToggleCoverage(); Draw(); Assert.Contains("Captured coverage", view.DetailTextForTests);
            Assert.Null(view.GetSelectedSkill()); view.NewKeyDownEvent(Key.Esc); Draw();
            Assert.Contains("No installed skills found", view.DetailTextForTests);
            Assert.Equal(0, loads); Assert.Equal(0, actions);
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void FilteredAndMissingSelectionRemainDistinctFromNoObservedCopiesAndClearIsExplicit(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with { Agents = [new("codex", "/skills/demo", false)] };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => throw new InvalidOperationException("No scan allowed"), (_, _, _) => Task.CompletedTask, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            Draw(); view.SetSelectedRowForTests(1);
            var key = view.SelectedKeyForTests;
            view.SelectFilter(0, Assert.Single(view.FilterChoices(0)).Key);
            view.SetFilterTextForTests("no-matches"); Draw();
            Assert.Contains("No matching installed skills", view.DetailTextForTests);
            Assert.Contains("Selected copy is missing, filtered out or hidden", view.DetailTextForTests);
            Assert.DoesNotContain("No installed skills found", view.DetailTextForTests);
            Assert.Equal(key, view.SelectedKeyForTests); Assert.Null(view.GetSelectedSkill());
            Assert.False(view.SubViews.OfType<Button>().Single(b => b.Text == "Search skills (1)").Visible);
            view.LoadSeeded(InventorySnapshot.Empty); Draw();
            Assert.Contains("No installed skills found", view.DetailTextForTests);
            Assert.Contains("unavailable; 0 matches", view.DetailTextForTests);
            Assert.Equal("no-matches", view.GetFilterText()); Assert.Equal(key, view.SelectedKeyForTests);
            view.LoadSeeded(snapshot); Draw(); Assert.Empty(view.RowsForTests);
            var clear = view.SubViews.OfType<Button>().Single(b => b.Text == "Clear");
            clear.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); Draw();
            Assert.Equal("", view.GetFilterText()); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(skill, view.GetSelectedSkill());
            // Existing ambiguity remains an inspect-only state, not empty.
            view.LoadSeeded(snapshot with { Skills = [skill, skill] }); Draw();
            Assert.Contains("Ambiguous installation ID", view.DetailTextForTests);
            Assert.DoesNotContain("No installed skills found", view.DetailTextForTests); Assert.Null(view.GetSelectedSkill());
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void VisibleControlsKeepListBudgetAndFilterSelectionCoverageFocusAndExactActionsAcrossRefresh(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skills = Enumerable.Range(0, 40).Select(i => InstalledOverviewTests.Copy("/skills/skill-" + i.ToString("D2"), "skill-" + i.ToString("D2")) with
        { Agents = [new("claude", "/claude/" + i, false, Scope.Project, "/work/Alpha"), new("codex", "/codex/" + i, true, Scope.User, "/home/alex")] }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills, Diagnostics = new() { InaccessiblePaths = ["/work/Gamma"] } };
        var availability = new UpdateAvailabilitySession();
        InstalledSkill? reviewed = null;
        var loads = 0; var checks = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => { loads++; return Task.FromResult(snapshot); }, (_, _, _) => Task.CompletedTask, () => { },
            availability: availability, onReviewUpdate: s => reviewed = s, onCheckUpdates: () => checks++)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            Draw();
            var buttons = view.SubViews.OfType<Button>().ToArray();
            var facets = new[] { "Agent:", "Project/location:", "Source/owner:", "Issue:" }.Select(title => buttons.Single(b => b.Text.ToString().StartsWith(title, StringComparison.Ordinal))).ToArray();
            for (var i = 0; i < 4; i++)
            {
                Assert.True(facets[i].Visible); Assert.True(facets[i].CanFocus);
                Assert.InRange(facets[i].Frame.Right, 1, view.Frame.Width);
                if (i > 0) Assert.True(facets[i - 1].Frame.Right <= facets[i].Frame.X);
            }
            var table = view.SubViews.OfType<TableView>().Single();
            Assert.True(table.Frame.Height >= height - 8); // three header rows, two footer rows, window borders
            view.FocusFilterForTests();
            foreach (var facet in facets)
            { Assert.True(view.HandleLocalNavigation(Key.Tab)); Assert.True(facet.HasFocus); }
            var agent = view.FilterChoices(0).Single(c => c.Label == "claude");
            view.SelectFilter(0, agent.Key);
            view.SelectFilter(1, view.FilterChoices(1).Single(c => c.Label.Contains("Alpha", StringComparison.Ordinal)).Key);
            view.FocusList(); view.SetSelectedRowForTests(41); table.RowOffset = 30; Draw();
            var key = view.SelectedKeyForTests;
            var top = view.RowsForTests[table.RowOffset].Key;
            view.NewKeyDownEvent(Key.Enter); Draw();
            var detail = view.SubViews.OfType<Markdown>().Single();
            detail.ScrollVertical(2); Draw(); var offset = detail.Viewport.Y;
            view.RefreshAvailability(); Draw();
            Assert.True(detail.HasFocus); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key); Assert.Equal(offset, detail.Viewport.Y);
            view.LoadSeeded(snapshot with { Skills = [.. skills.Reverse()] }); Draw();
            Assert.Equal(key, view.SelectedKeyForTests); Assert.True(detail.HasFocus);
            view.ReviewSelectedUpdate(); Assert.Equal(skills[20].ResolvedPath, reviewed?.ResolvedPath);
            view.ToggleCoverage(); Draw();
            Assert.Contains("Captured coverage", view.DetailTextForTests);
            Assert.Contains("Could not be scanned", view.DetailTextForTests);
            Assert.Contains("Issues not loaded", view.DetailTextForTests);
            Assert.Null(view.GetSelectedSkill());
            foreach (var size in new[] { (80, 24), (120, 36) })
            { app.Driver.SetScreenSize(size.Item1, size.Item2); Draw(); Assert.Contains("Captured coverage", view.DetailTextForTests); Assert.Equal(key, view.SelectedKeyForTests); }
            view.NewKeyDownEvent(Key.Esc); Draw();
            Assert.False(view.DetailOpenForTests); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(top, view.RowsForTests[table.RowOffset].Key);
            view.ReviewSelectedUpdate(); Assert.Equal(skills[20].ResolvedPath, reviewed?.ResolvedPath);
            // The selected Agent disappears; its choice is retained and cannot
            // quietly broaden to Codex or retarget a sibling operation.
            view.LoadSeeded(snapshot with { Skills = [.. skills.Select(s => s with { Agents = [s.Agents[1]] })] }); Draw();
            Assert.Empty(view.RowsForTests); Assert.Null(view.GetSelectedSkill());
            Assert.Contains("unavailable; 0 matches", view.DetailTextForTests);
            Assert.Equal(key, view.SelectedKeyForTests);
            reviewed = null; view.ReviewSelectedUpdate(); Assert.Null(reviewed);
            view.LoadSeeded(snapshot); Draw(); Assert.Equal(key, view.SelectedKeyForTests);
            Assert.Equal(skills[20].ResolvedPath, view.GetSelectedSkill()?.ResolvedPath);
            view.FocusList(); view.NewKeyDownEvent(new Key('C')); Assert.Equal(1, checks);
            view.FocusFilterForTests(); view.SendFilterKeyForTests(new Key('C')); Assert.Equal(1, checks);
            Assert.Equal(0, loads); // local filtering/drawing never calls the capture loader
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Fact]
    public void ScopeCycleAloneIncludesPhysicalUserAndCustomCopiesWithOnlyProjectConsumers()
    {
        var user = InstalledOverviewTests.Copy("/physical/user", "user") with
        { Agents = [new("claude", "/Beta/user", true, Scope.Project, "/work/Beta")] };
        var custom = user with { Name = "custom", ResolvedPath = "/physical/custom", Scope = Scope.Custom };
        var snapshot = InventorySnapshot.Empty with { Skills = [user, custom] };
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { });
        view.LoadSeeded(snapshot);
        view.FocusList();
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["user"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["custom", "user"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('G')); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
        view.SelectFilter(0, Assert.Single(view.FilterChoices(0)).Key);
        Assert.Empty(view.RowsForTests); // Custom payload scope cannot fill a project-consumer match.
        view.SelectFilter(0, null); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
        view.SelectFilter(1, Assert.Single(view.FilterChoices(1)).Key); Assert.Empty(view.RowsForTests);
        view.SelectFilter(1, null); Assert.Equal(["custom"], view.VisibleSkillNamesForTests);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void EachRefreshUsesOneIssuesReferenceAndGenerationForAllRowsChoicesCountsAndStatus(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var snapshot = InventorySnapshot.Empty with
        { Skills = [.. Enumerable.Range(0, 6).Select(i => InstalledOverviewTests.Copy("/skills/" + i, "skill-" + i))] };
        var evidence = InstalledIssueEvidence.Capture(snapshot, 1, new(snapshot.CapturedAt, false, [], []));
        var changing = false; var issueReads = 0; var generationReads = 0;
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { }, availability: new(),
            capturedIssues: () => !changing || ++issueReads == 1 ? evidence : null,
            catalogGeneration: () => !changing || ++generationReads == 1 ? 1 : 2)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot); view.SelectFilter(3, "none-observed");
        var session = app.Begin(window);
        try
        {
            changing = true;
            view.SetFilterTextForTests("skill-"); Draw(); // Full refresh, including nested availability rendering.

            Assert.Equal(1, issueReads); Assert.Equal(1, generationReads);
            Assert.Equal(6, view.VisibleSkillNamesForTests.Count);
            Assert.Contains("6 observed copies", view.FooterTextForTests);
            Assert.DoesNotContain("Issues not loaded", view.DetailTextForTests);
            Assert.Contains(view.FilterChoices(3), c => c.Key == "none-observed");
            view.ToggleCoverage(); Draw();
            Assert.DoesNotContain("Issues not loaded", view.DetailTextForTests);
            Assert.Equal(1, issueReads); Assert.Equal(1, generationReads);
            // The next refresh observes invalidation as one coherent unknown view.
            view.RefreshAvailability(); Draw();
            Assert.Equal(2, issueReads); Assert.Equal(2, generationReads);
            Assert.Empty(view.RowsForTests);
            Assert.Contains("0 of 6 observed copies", view.FooterTextForTests);
            Assert.DoesNotContain(view.FilterChoices(3), c => c.Key == "none-observed");
            Assert.Contains("Issues not loaded", view.DetailTextForTests);
            Assert.Contains("unavailable; 0 matches", view.DetailTextForTests);
            var issueButton = view.SubViews.OfType<Button>().Single(b => b.Text.ToString().StartsWith("Issue:", StringComparison.Ordinal));
            Assert.Contains("!", issueButton.Text.ToString());
            Assert.Equal(2, issueReads); Assert.Equal(2, generationReads);
        }
        finally { app.End(session!); }
        void Draw() { window.Layout(); window.Draw(new DrawContext()); }
    }

    [Fact]
    public async Task ClosingSelectorAfterLifetimeCancellationDoesNotRestoreFocusToItsButton()
    {
        await Task.Run(() =>
        {
            using var app = Application.Create().Init(); app.Driver!.SetScreenSize(80, 24);
            using var window = new Window();
            using var cancellation = new CancellationTokenSource();
            using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException(),
                (_, _, _) => Task.CompletedTask, () => { }, lifetimeToken: cancellation.Token)
            { Width = Dim.Fill(), Height = Dim.Fill() };
            window.Add(view); view.LoadSeeded(InstalledOverviewTests.Fixture());
            var session = app.Begin(window);
            var canceledInSelector = false; var focusedAfterCancellation = false; var timedOut = false;
            try
            {
                window.Layout();
                var button = view.SubViews.OfType<Button>().Single(b => b.Text.ToString().StartsWith("Agent:", StringComparison.Ordinal));
                view.FocusList();
                button.HasFocusChanged += (_, _) => { if (cancellation.IsCancellationRequested && button.HasFocus) focusedAfterCancellation = true; };
                app.AddTimeout(TimeSpan.FromSeconds(5), () => { timedOut = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (app.TopRunnableView is not Dialog) return true;
                    canceledInSelector = true; cancellation.Cancel(); app.RequestStop(); return false;
                });
                button.InvokeCommand(Command.Accept);
                Assert.True(canceledInSelector); Assert.False(timedOut); Assert.False(focusedAfterCancellation);
            }
            finally { app.End(session!); }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task PackageOverlayIsOneExactCopyProblemAndReviewsStayInInstalled(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        var target = InstalledOverviewTests.Copy("/nonexistent/package-a/demo", "demo");
        var sibling = target with { Name = "sibling", ResolvedPath = "/nonexistent/package-a/other" };
        var snapshot = InventorySnapshot.Empty with { Skills = [target, sibling] };
        var report = PackageCheckFactTests.Report() with
        {
            Findings = [
            new("reference", "reference-missing", "SKILL.md", 56, "references/maintenance.md", "m", "r"),
            new("reference", "reference-missing", "SKILL.md", 60, "references/other.md", "m", "r"),
            new("metadata", "format", "SKILL.md", 1, null, "m", "r")],
            FindingCount = 3
        };
        var fact = new PackageCheckFact(new(snapshot, target, 1, "context", target.InstallationId), report, DateTimeOffset.UtcNow);
        PackageCheckFact? current = fact; PackageCheckFact? activated = null;
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { },
            catalogGeneration: () => 1, packageCheck: () => current, onPackageCheck: f => activated = f);
        await issues.LoadAsync();
        var overlay = Assert.Single(issues.RowsForTests, r => r.PackageCheck is not null);
        Assert.Equal("Problem", overlay.Kind);
        Assert.Same(fact, overlay.PackageCheck);
        Assert.Empty(issues.CapturedIssues!.Findings);
        Assert.Contains("1 Problems", issues.GetQueueLabel());
        var key = overlay.Entry.Id;
        issues.NewKeyDownEvent(Key.Enter); Assert.Same(fact, activated);
        using var installed = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No new inventory"),
            (_, _, _) => Task.CompletedTask, () => { }, capturedIssues: () => issues.CapturedIssues, catalogGeneration: () => 1, packageCheck: () => current);
        installed.LoadSeeded(snapshot);
        installed.SelectFilter(3, "any-finding");
        Assert.Single(installed.RowsForTests, r => r.Copy is not null);
        installed.SetSelectedRowForTests(1); installed.NewKeyDownEvent(Key.Enter);
        Assert.Contains("line 56", installed.DetailTextForTests);
        Assert.Contains("references/maintenance.md", installed.DetailTextForTests);
        Assert.Contains("1 Review", installed.DetailTextForTests);
        current = fact with { Report = report with { Findings = [report.Findings[2]], FindingCount = 1 } };
        await issues.LoadAsync();
        Assert.DoesNotContain(issues.RowsForTests, r => r.PackageCheck is not null);
        installed.RefreshAvailability();
        Assert.Single(installed.RowsForTests, r => r.Copy is not null); // Review-only remains a captured finding.
        current = fact;
        await issues.LoadAsync(); Assert.Equal(key, Assert.Single(issues.RowsForTests, r => r.PackageCheck is not null).Entry.Id);
        current = null;
        await issues.LoadAsync(); Assert.DoesNotContain(issues.RowsForTests, r => r.PackageCheck is not null);
    }

    [Fact]
    public async Task AgedDisplayedInventoryAllowsBoundKButNextTabReloadClearsItsPackageOverlay()
    {
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(120, 36);
            var root = Directory.CreateTempSubdirectory("skillview-package-lifetime-");
            try
            {
                var token = TestContext.Current.CancellationToken;
                File.WriteAllText(Path.Combine(root.FullName, "SKILL.md"), "---\nname: demo\ndescription: inert fixture\n---\n[missing](references/missing.md)\n");
                var target = InstalledOverviewTests.Copy(root.FullName, "demo");
                var initialPayload = PayloadSnapshot.CaptureEvidence(root.FullName, token);
                var now = DateTimeOffset.UtcNow;
                target = target with
                {
                    ObservedSubject = OperationSubject.From(target) with
                    { NativeObject = NativeObjectObservation.From(initialPayload.RootIdentity!.Value, now, "inventory-observation") }
                };
                var snapshot = InventorySnapshot.Empty with { CapturedAt = now, Skills = [target] };
                var replacement = snapshot with { CapturedAt = now.AddMinutes(3) };
                const long generation = 4;
                var displayCaptures = 0; var payloadCaptures = 0; var timedOut = false;
                var cache = new DisplayInventoryCache(() => generation, _ =>
                {
                    displayCaptures++;
                    return Task.FromResult(replacement);
                }, (work, operation) => { _ = work(token); return true; }, () => now);
                cache.PublishFresh(snapshot, generation);
                PackageCheckFact? lastFact = null;
                PackageCheckFact? CurrentFact() => lastFact is not null && cache.CacheIsCurrent(lastFact.Request.Snapshot, generation) ? lastFact : null;
                using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, ct => cache.GetAsync(ct), () => { },
                    catalogGeneration: () => generation, packageCheck: CurrentFact);
                issues.LoadAsync().GetAwaiter().GetResult();
                Assert.Same(snapshot, issues.CapturedIssues!.Snapshot);
                InstalledTabView? installed = null;
                using var installedOwner = installed = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, ct => cache.GetAsync(ct),
                    (_, _, _) => Task.CompletedTask, () => { }, catalogGeneration: () => generation, packageCheck: CurrentFact, onPackageCheck: Check)
                { Width = Dim.Fill(), Height = Dim.Fill(), Visible = true };
                using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
                window.Add(installed);
                var session = app.Begin(window);
                try
                {
                    installed.LoadSeeded(snapshot);
                    installed.SetSelectedRowForTests(1);
                    installed.FocusList();
                    Assert.True(installed.TableHasFocusForTests);
                    Assert.Same(target, installed.GetSelectedSkill());
                    now += TimeSpan.FromMinutes(3);
                    Assert.True(cache.CacheIsCurrent(snapshot, generation));
                    installed.NewKeyDownEvent(new Key('K'));
                    Assert.False(timedOut);
                    var fact = Assert.IsType<PackageCheckFact>(lastFact);
                    Assert.Same(snapshot, fact.Request.Snapshot);
                    Assert.Equal(1, fact.Problems);
                    Assert.Contains("Check results are cleared when the skill list reloads.", fact.Detail);
                    Assert.Contains("Opening Installed or Issues reloads lists at least 1 minute old.", fact.Detail);
                    installed.NewKeyDownEvent(Key.Enter);
                    Assert.Contains(PackageCheckFact.InventoryLifetimeNotice, installed.DetailTextForTests);
                    issues.RefreshAvailability();
                    Assert.Same(fact, Assert.Single(issues.RowsForTests, r => r.PackageCheck is not null).PackageCheck);
                    Assert.Equal(0, displayCaptures);
                    Assert.Equal(1, payloadCaptures);

                    // Entering Issues calls GetAsync. The old capture's reuse window
                    // expired before K, so even this just-completed fact belongs to S only.
                    issues.LoadAsync().GetAwaiter().GetResult();
                    Assert.Same(replacement, issues.CapturedIssues!.Snapshot);
                    Assert.False(cache.CacheIsCurrent(snapshot, generation));
                    Assert.Null(CurrentFact());
                    Assert.DoesNotContain(issues.RowsForTests, r => r.PackageCheck is not null);
                    Assert.False(fact.Matches(replacement, generation, target));
                    Assert.Same(fact, lastFact); // Retained memory was not rebound to S2.
                    installed.LoadAsync().GetAwaiter().GetResult();
                    Assert.Same(replacement, installed.CurrentSnapshot);
                    installed.SetSelectedRowForTests(1); installed.NewKeyDownEvent(Key.Enter);
                    Assert.Contains("Package not checked for this inventory capture.", installed.DetailTextForTests);
                    Assert.Contains(PackageCheckFact.InventoryLifetimeNotice, installed.DetailTextForTests);
                    Assert.Equal(1, displayCaptures);
                    Assert.Equal(1, payloadCaptures); // No automatic payload check/hash on either tab load.

                }
                finally { app.End(session!); }

                void Check(InventorySnapshot displayed, string id, InstalledSkill selected)
                {
                    Assert.Same(snapshot, displayed);
                    Dialog? modal = null;
                    var request = new PackageCheckRequest(displayed, selected, generation, "captured-child-context", id);
                    app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
                    app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                    {
                        if (lastFact is null || modal?.SubViews.OfType<Button>().Single(b => b.Text == "_Check again").Enabled != true) return true;
                        app.Keyboard.RaiseKeyDownEvent(Key.Esc); return false;
                    });
                    PackageCheckScreen.ShowBound(app, new Logger(), selected, ct => request.CaptureAsync(ct, (path, sink, captureToken) =>
                    {
                        payloadCaptures++;
                        return PayloadSnapshot.CaptureWithEntrySink(path, sink, captureToken);
                    }), token, dialog => modal = dialog, publish: result =>
                    {
                        if (result.Fact is not { } completed || !cache.CacheIsCurrent(displayed, generation)
                            || !ReferenceEquals(installed?.CurrentSnapshot, displayed)) return false;
                        lastFact = completed;
                        return true;
                    });
                    installed?.RefreshAvailability();
                }
            }
            finally { root.Delete(true); }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MetadataReviewAddsNoProblemToExistingMalformedQueueRow()
    {
        var target = InstalledOverviewTests.Copy("/nonexistent/package-a/malformed", "demo") with { Validity = ValidityState.InvalidMetadata };
        var snapshot = InventorySnapshot.Empty with { Skills = [target] };
        var report = PackageCheckFactTests.Report() with { Findings = [new("metadata", "required", "SKILL.md", 1, null, "m", "r")], FindingCount = 1 };
        var fact = new PackageCheckFact(new(snapshot, target, 1, "context", target.InstallationId), report, DateTimeOffset.UtcNow);
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { },
            catalogGeneration: () => 1, packageCheck: () => fact);
        await issues.LoadAsync();
        Assert.DoesNotContain(issues.RowsForTests, r => r.PackageCheck is not null);
        Assert.Single(issues.RowsForTests, r => r.Entry.Issue == "Malformed");
        Assert.Contains("1 Problems", issues.GetQueueLabel());
    }

    [Fact]
    public async Task IssuesHandoffReusesExistingResultAndRejectsCatalogGenerationChanges()
    {
        var snapshot = InventorySnapshot.Empty;
        long generation = 1;
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, _ => Task.FromResult(snapshot), () => { }, catalogGeneration: () => generation);
        Assert.Null(issues.CapturedIssues);
        await issues.LoadAsync();
        var captured = issues.CapturedIssues;
        Assert.NotNull(captured); Assert.True(captured.IsCurrent(snapshot, generation));
        await issues.LoadAsync(); Assert.Same(captured, issues.CapturedIssues);
        generation++;
        Assert.False(captured.IsCurrent(snapshot, generation));
        await issues.LoadAsync(); Assert.NotSame(captured, issues.CapturedIssues);
        Assert.True(issues.CapturedIssues!.IsCurrent(snapshot, generation));
        using var installed = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException("No capture allowed"),
            (_, _, _) => Task.CompletedTask, () => { }, capturedIssues: () => issues.CapturedIssues, catalogGeneration: () => generation);
        installed.LoadSeeded(snapshot);
        Assert.Contains(installed.FilterChoices(3), c => c.Key == "none-observed");
        generation++;
        installed.LoadSeeded(snapshot); // A new view refresh captures the invalidation.
        Assert.DoesNotContain(installed.FilterChoices(3), c => c.Key == "none-observed");
    }
    [Fact]
    public async Task InvalidatedOrCanceledIssuesLoadCannotPublishFindingsAndDoesNotStrandLoading()
    {
        long generation = 1;
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var issues = new ChangesTabView(a => { a(); return Task.CompletedTask; }, token => pending.Task.WaitAsync(token),
            () => { }, catalogGeneration: () => generation);
        var loading = issues.LoadAsync();
        generation++;
        pending.SetResult(InventorySnapshot.Empty);
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(issues.CapturedIssues);
        Assert.Contains("Inventory changed", issues.StatusTextForTests);
        Assert.All(issues.SubViews.OfType<SpinnerView>(), spinner => Assert.False(spinner.Visible));
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        loading = issues.LoadAsync();
        issues.CancelPendingLoad();
        pending.SetResult(InventorySnapshot.Empty);
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(issues.CapturedIssues);
    }

    [Fact]
    public void RefreshedChoiceLabelFollowsItsStableKeyWhenSanitizationCollisionDisappears()
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo") with
        { Agents = [new("claude\u001b[31m", "/a", false), new("claude", "/b", false)] };
        using var view = new InstalledTabView(a => { a(); return Task.CompletedTask; }, (_, _) => { }, _ => throw new InvalidOperationException(),
            (_, _, _) => Task.CompletedTask, () => { });
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [skill] });
        view.SelectFilter(0, InstalledFilters.Key("agent", skill.Agents[0].AgentId));
        Assert.Contains("claude #", view.DetailTextForTests);
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [skill with { Agents = [skill.Agents[0]] }] });
        Assert.Contains("Agent: `claude`", view.DetailTextForTests);
        Assert.DoesNotContain("claude #", view.DetailTextForTests);
        Assert.Equal(2, view.RowsForTests.Count);
    }

}
