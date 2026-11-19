using SkillView.Ui;
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
public sealed class InstalledFocusAuditTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    [InlineData(160, 48)]
    public void TabReachesVisibleDetailsAndComparisonControlsWithoutLosingSelection(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        var (request, _) = InlineCopyWorkflowTests.Request();
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(request.Snapshot), (_, _, _) => Task.CompletedTask, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(request.Snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout();
            view.SetSelectedRowForTests(0);
            view.FocusList();
            if (view.RowsForTests[0].Copy is null) Press(new Key(KeyCode.CursorRight));
            var exact = view.RowsForTests.ToList().FindIndex(row => row.Copy is not null);
            Assert.True(exact >= 0);
            view.SetSelectedRowForTests(exact);
            var selected = view.SelectedKeyForTests;
            Press(Key.Enter);
            var detail = view.SubViews.OfType<Markdown>().Single();
            Assert.True(detail.HasFocus);
            Press(Key.Tab.WithShift);
            Assert.False(detail.HasFocus);
            Press(Key.Tab);
            Assert.True(detail.HasFocus);
            Reach(detail, Key.Tab.WithShift);
            Assert.Equal(selected, view.SelectedKeyForTests);
            var more = view.SubViews.OfType<Button>().Single(button => button.Text == "More actions");
            more.SetFocus();
            Press(Key.Enter);
            Assert.True(app.Popovers!.GetActivePopover()?.Visible);
            Press(Key.Enter); // Compare copies is the first contextual action.
            var copies = view.SubViews.OfType<InlineCopiesView>().Single();
            Assert.True(copies.Open);
            var other = copies.Request!.Choices.First(copy => !ReferenceEquals(copy, copies.Request.Target));
            copies.SetReference(copies.Request, other);
            var compare = copies.SubViews.OfType<Button>().Single(button => button.Text == "Compare");
            var back = copies.SubViews.OfType<Button>().Single(button => button.Text == "Back (Esc)");
            Assert.True(compare.HasFocus);
            Press(Key.Tab);
            Assert.True(back.HasFocus);
            Press(Key.Tab.WithShift);
            Assert.True(compare.HasFocus);
            Reach(copies.SubViews.OfType<Markdown>().Single(), Key.Tab.WithShift);
            Press(Key.Esc);
            Assert.False(copies.Open);
            Assert.Equal(selected, view.SelectedKeyForTests);
        }
        finally { app.End(session!); }

        void Reach(View target, Key direction)
        {
            Press(direction);
            for (var count = 0; count < 24 && !target.HasFocus; count++) Press(direction);
            Assert.True(target.HasFocus, "Visible read-only details must be keyboard reachable.");
        }
        void Press(Key key)
        {
            app.Keyboard.RaiseKeyDownEvent(new Key(key.KeyCode));
            window.Layout();
            window.Draw(new DrawContext());
        }
    }
}
