using SkillView.Inventory.Models;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class MaintenanceQueueKeyboardTests
{
    [Fact]
    public async Task ExactDrillIn_ComparisonKeyboardStagesOnlySelectedAddressAndRequiresRetentionBeforeApply()
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(100, 26);
        var selected = MaintenanceQueueTests.Skill("/skills/selected", "https://github.com/selected/repo");
        var peer = MaintenanceQueueTests.Skill("/skills/peer", "https://github.com/peer/repo");
        var calls = new List<string>();
        var review = new ReplacementResult(new('a', 32), "planned", true, false, false,
            selected.ResolvedPath, "/state/original", "/state/candidate", "/state/displaced", new('b', 64), new('c', 64),
            ["SKILL.md"], "Review exact saved candidate", new(selected.InstallationId, selected.Name, selected.Scope,
                selected.Agents, selected.FrontMatter, []), "/state/plan.json", 448, 448)
        {
            Impact = OperationImpact.Payload(OperationSubject.From(selected), "update-revision"),
            Comparison = new(new('d', 64), new('e', 64), [new("SKILL.md",
                new("SKILL.md", "file", new('1', 64), new('1', 64), 10, 384, null),
                new("SKILL.md", "file", new('2', 64), new('2', 64), 20, 384, null))])
        };
        UpdateResult Result(ReplacementResult saved, bool applied = false) => new()
        {
            DryRun = false,
            Succeeded = true,
            ExitCode = 0,
            StdOut = "",
            StdErr = "",
            ErrorMessage = null,
            CommandLine = [],
            Entries = [],
            SavedPlan = saved,
            MutationAttempted = applied
        };
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(),
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [selected, peer] }),
            (_, options, _) =>
            {
                Assert.Equal(selected.InstallationId, Assert.Single(options.Targets!).InstallationId);
                Assert.False(options.All); calls.Add("compare-plan"); return Task.FromResult(Result(review));
            }, () => "/gh", new Logger(), () => { }, () => { }, applyRunner: (saved, _, _) =>
            {
                Assert.Same(review, saved); calls.Add("apply");
                return Task.FromResult(Result(saved with { Status = "installed", Message = "Verified saved disk locations; session unknown" }, true));
            })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        await view.LoadForSubjectAsync(OperationSubject.From(selected));
        var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single();
            table.SetFocus(); Assert.True(table.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key('c'));
            Assert.Equal(new[] { "compare-plan" }, calls);
            Assert.Contains("Installed → saved candidate comparison", view.PreviewTextForTests);
            Assert.Contains(new string('1', 64), view.PreviewTextForTests);
            Assert.Contains(new string('2', 64), view.PreviewTextForTests);
            Assert.False(view.ApplyButtonForTests.Enabled);
            await view.ApplyReviewForTestsAsync();
            Assert.Single(calls);
            view.RetainBoxForTests.SetFocus(); Assert.True(view.RetainBoxForTests.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Space));
            Assert.True(view.ApplyButtonForTests.Enabled);
            view.ApplyButtonForTests.SetFocus(); Assert.True(view.ApplyButtonForTests.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(Key.Enter);
            Assert.Equal(new[] { "compare-plan", "apply" }, calls);
            Assert.Contains("installed", view.PreviewTextForTests);
            Assert.Contains("Active session", view.PreviewTextForTests);
            Assert.Contains("unknown", view.PreviewTextForTests);
            Assert.False(view.ApplyButtonForTests.Enabled);
        }
        finally { app.End(session!); }
    }

    [Fact]
    public async Task FocusedQueue_RefreshesAndRoutesExactRowThroughApplicationKeyboard()
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(100, 26);
        var first = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var second = MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo");
        var captures = 0;
        MaintenanceQueueEntry? activated = null;
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => { captures++; return Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }); },
            static () => throw new InvalidOperationException("Untargeted updates"),
            static () => throw new InvalidOperationException("Untargeted cleanup"),
            static () => { }, static () => { }, onActivateEntry: entry => activated = entry,
            runTask: (work, _) => work().GetAwaiter().GetResult())
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        await view.LoadAsync();
        var session = app.Begin(window);
        try
        {
            var table = view.SubViews.OfType<TableView>().Single();
            table.SetFocus();
            Assert.True(table.HasFocus);
            Press(new Key('r'));
            Assert.Equal(2, captures);
            Assert.Null(activated);
            Assert.True(table.HasFocus);
            Press(new Key(KeyCode.CursorDown));
            Press(Key.Enter);
            Assert.NotNull(activated);
            Assert.Equal("NameCollision", activated.Issue);
            Assert.Equal(second.InstallationId, activated.Subject.InstallationId);
            Assert.Contains(second.ResolvedPath, activated.AffectedPaths);
        }
        finally { app.End(session!); }

        void Press(Key key)
        {
            app.Keyboard.RaiseKeyDownEvent(key);
            window.Draw(new DrawContext());
        }
    }
}
