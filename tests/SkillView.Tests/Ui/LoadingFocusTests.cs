using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class LoadingFocusTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void DiscoverPreservesBrowseFocusAndBlocksHiddenRowActions(bool fromDetail, bool targetAvailable)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(120, 36);
        using var view = new DiscoverTabView("actions", "preview") { Width = Dim.Fill(), Height = Dim.Fill() };
        using var window = new Window();
        window.Add(view);
        var session = app.Begin(window);
        try
        {
            var previous = fromDetail ? (View)view.DetailPane.PreviewPane : view.ResultsTable;
            previous.SetFocus();
            Assert.True(previous.HasFocus);
            view.SetLoading(true);
            var loading = Assert.IsType<TabLoadingView>(view.SubViews.Single(child => child is TabLoadingView));
            Assert.True(loading.HasFocus);
            Assert.False(view.QueryField.HasFocus);
            foreach (var key in new[] { new Key('i'), new Key('o'), new Key(KeyCode.Enter) })
            {
                app.Keyboard.RaiseKeyDownEvent(key);
                Assert.True(key.Handled);
            }
            var received = new List<int>();
            window.KeyDown += (_, key) => received.Add(key.AsRune.Value);
            app.Keyboard.RaiseKeyDownEvent(new Key('q'));
            app.Keyboard.RaiseKeyDownEvent(new Key('f'));
            Assert.Contains('q', received);
            Assert.Contains('f', received);
            Assert.Equal(string.Empty, view.QueryField.Text);
            // Switching the detail mode while loading can retire the old pane.
            if (!targetAvailable) previous.Visible = false;
            view.SetLoading(false); // Same restoration used by failed searches.
            Assert.True((targetAvailable ? previous : view.ResultsTable).HasFocus);
            Assert.False(view.QueryField.HasFocus);
        }
        finally { app.End(session!); }
    }

    [Fact]
    public void SearchFromQueryFieldKeepsIntentionalTextFocus()
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(120, 36);
        using var view = new DiscoverTabView("actions", "preview") { Width = Dim.Fill(), Height = Dim.Fill() };
        using var window = new Window();
        window.Add(view);
        var session = app.Begin(window);
        try
        {
            view.QueryField.SetFocus();
            Assert.True(view.QueryField.HasFocus);
            view.SetLoading(true);
            Assert.True(view.QueryField.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key('i'));
            Assert.Equal("i", view.QueryField.Text);
            view.SetLoading(false);
            Assert.True(view.QueryField.HasFocus);
        }
        finally { app.End(session!); }
    }

    [Fact]
    public Task ChangesRetainsFocusAndAllowsRefreshWhileLoadingWithoutActivatingHiddenRows()
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(120, 36);
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task>();
        var refreshes = 0;
        var activations = 0;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            token => pending.Task.WaitAsync(token), () => { }, onActivateEntry: _ => activations++,
            runTask: (work, _) => tasks.Add(work()), onRefresh: () => refreshes++)
        { Visible = true, Width = Dim.Fill(), Height = Dim.Fill() };
        using var window = new Window();
        window.Add(view);
        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        var session = app.Begin(window);
        try
        {
            table.SetFocus();
            Assert.True(table.HasFocus);
            tasks.Add(view.LoadAsync());
            var loading = Assert.IsType<TabLoadingView>(view.SubViews.Single(child => child is TabLoadingView));
            Assert.True(loading.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
            Assert.Equal(0, activations);
            app.Keyboard.RaiseKeyDownEvent(new Key('r'));
            Assert.Equal(1, refreshes);
            Assert.True(loading.HasFocus);
            view.CancelPendingLoad();
            Assert.True(table.HasFocus);
        }
        finally
        {
            view.CancelPendingLoad();
            app.End(session!);
        }
        return Task.WhenAll(tasks);
    }
}
