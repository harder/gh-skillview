using System.Collections.Immutable;
using Terminal.Gui.Views;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LoadingRetainsNonTextFocusAndLetsGlobalShortcutsBubble(bool refresh)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(120, 36);
        var pending = new TaskCompletionSource<InventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = new SkillView.Ui.Tabs.InstalledTabView(action => { action(); return Task.CompletedTask; },
            (_, _) => { }, token => pending.Task.WaitAsync(token), (_, _, _) => Task.CompletedTask,
            () => { }, () => { })
        { Visible = true, Width = Dim.Fill(), Height = Dim.Fill() };
        using var window = new Terminal.Gui.Views.Window();
        window.Add(view);
        if (refresh) view.LoadSeeded(InventorySnapshot.Empty);
        var session = app.Begin(window);
        Task? load = null;
        try
        {
            view.FocusList();
            load = view.LoadAsync();
            view.FocusList(); // Same focus request as cold tab activation.
            var loading = Assert.IsType<TabLoadingView>(view.SubViews.Single(child => child is TabLoadingView));
            Assert.True(loading.HasFocus);
            Assert.False(view.FilterHasFocusForTests);
            var received = new List<int>();
            window.KeyDown += (_, key) => received.Add(key.AsRune.Value);
            foreach (var key in new[] { '?', 'd', '3', 'q' })
                app.Keyboard.RaiseKeyDownEvent(new Key(key));
            Assert.Equal(new int[] { '?', 'd', '3', 'q' }, received);
            Assert.Equal(string.Empty, view.GetFilterText());
            Assert.False(view.FilterHasFocusForTests);
        }
        finally
        {
            view.CancelPendingWork();
            app.End(session!);
        }
        return load ?? Task.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    public void ArrowNavigation_PastBothEndsAndAfterFiltering_RemainsSafe(int count)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(120, 36);
        var skillview = new SkillViewApp(TuiServices.Build(new Logger(LogLevel.Debug)), new AppOptions(
            InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []));
        using var window = skillview.BuildUiForTests();
        var view = skillview.InstalledTabForTests!;
        var snapshot = InventorySnapshot.Empty with
        {
            Skills = Enumerable.Range(0, count).Select(i => new InstalledSkill
            {
                Name = $"skill-{i:D3}",
                ResolvedPath = $"/skills/skill-{i:D3}",
                ScanRoot = "/skills",
                Scope = Scope.User,
                Agents = [],
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                IsSymlinked = false,
                InstalledAt = null,
                Ignored = false,
            }).ToImmutableArray(),
        };
        view.LoadSeeded(snapshot);
        skillview.ForceActiveTabForTests(SkillViewTab.Installed);
        var session = app.Begin(window);
        try
        {
            view.FocusList();
            Press(KeyCode.CursorDown, count + 10);
            Assert.Equal(count == 0 ? null : $"skill-{count - 1:D3}", view.GetSelectedSkill()?.Name);
            Assert.True(view.GetSelectedSkill() is null ? view.SubViews.OfType<Markdown>().Single().HasFocus : view.TableHasFocusForTests);
            Press(KeyCode.N | KeyCode.CtrlMask, 10);
            Assert.True(view.GetSelectedSkill() is null ? view.SubViews.OfType<Markdown>().Single().HasFocus : view.TableHasFocusForTests);
            Press(KeyCode.CursorUp, count + 10);
            Assert.Equal(count == 0 ? null : "skill-000", view.GetSelectedSkill()?.Name);
            Assert.True(view.GetSelectedSkill() is null ? view.SubViews.OfType<Markdown>().Single().HasFocus : view.TableHasFocusForTests);
            Press(KeyCode.P | KeyCode.CtrlMask, 10);
            Assert.True(view.GetSelectedSkill() is null ? view.SubViews.OfType<Markdown>().Single().HasFocus : view.TableHasFocusForTests);
            Press(KeyCode.PageDown, 10);
            Assert.Equal(count == 0 ? null : $"skill-{count - 1:D3}", view.GetSelectedSkill()?.Name);
            Press(KeyCode.PageUp, 10);
            Assert.Equal(count == 0 ? null : "skill-000", view.GetSelectedSkill()?.Name);
            Press(KeyCode.End | KeyCode.CtrlMask);
            app.Driver.SetScreenSize(90, 26);
            window.Layout();
            Press(KeyCode.CursorDown, 10);
            view.SetFilterTextForTests("no matches");
            Press(KeyCode.CursorDown, 10);
            Press(KeyCode.CursorUp, 10);
            Assert.Null(view.GetSelectedSkill());
            Assert.True(view.GetSelectedSkill() is null ? view.SubViews.OfType<Markdown>().Single().HasFocus : view.TableHasFocusForTests);
            view.SetFilterTextForTests("skill-000");
            Press(KeyCode.CursorDown, 10);
            Assert.Equal(count == 0 ? null : "skill-000", view.GetSelectedSkill()?.Name);
            Press(KeyCode.Tab);
            Assert.False(view.TableHasFocusForTests);
        }
        finally
        {
            app.End(session!);
        }

        void Press(KeyCode key, int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                app.Keyboard.RaiseKeyDownEvent(new Key(key));
                window.Draw(new DrawContext());
            }
        }
    }
}
