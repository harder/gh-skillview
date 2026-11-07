using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class LocationsDialogTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ActualEnterAddsFromBothFieldsPreservesDraftAndFocusedSaveWorks(int width, int height)
    {
        var root = Directory.CreateTempSubdirectory("skillview-location-keys-");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root.FullName, "selected")).FullName;
            var profile = Directory.CreateDirectory(Path.Combine(root.FullName, "profile")).FullName;
            var cli = Directory.CreateDirectory(Path.Combine(root.FullName, "cli-root")).FullName;
            var services = Services(root.FullName);
            var options = new AppOptions(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [cli], null, []);
            var applied = 0; var completed = false; var timeout = false;
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
                var workflows = Coordinator(services, options, app);
                Dialog? dialog = null; var stage = 0;
                app.AddTimeout(TimeSpan.FromSeconds(20), () => { timeout = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (dialog is null) return true;
                    var fields = dialog.SubViews.OfType<TextField>().ToArray();
                    var list = dialog.SubViews.OfType<ListView>().Single();
                    var add = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Add");
                    var save = dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Save & refresh");
                    if (!add.Enabled) return true;
                    if (stage == 0)
                    {
                        fields[0].Text = first; fields[0].SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                    }
                    else if (stage == 1 && list.Source?.Count == 1)
                    {
                        list.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); Assert.True(dialog.IsRunning);
                        var kind = dialog.SubViews.OfType<OptionSelector>().Single(); kind.Value = 2; kind.FocusedItem = 2;
                        fields[0].Text = profile; fields[1].Text = "codex:work"; fields[1].SetFocus();
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                    }
                    else if (stage == 2 && list.Source?.Count == 2)
                    {
                        fields[0].Text = Path.Combine(root.FullName, "missing"); fields[0].SetFocus();
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                    }
                    else if (stage == 3 && dialog.SubViews.OfType<Label>().Any(label => label.Text.Contains("action failed", StringComparison.Ordinal)))
                    {
                        Assert.Equal(2, list.Source!.Count); fields[2].SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                        Assert.True(dialog.IsRunning); Assert.True(save.Enabled);
                        save.SetFocus(); app.Keyboard.RaiseKeyDownEvent(Key.Enter); stage++;
                    }
                    return true;
                });
                LocationsDialog.Show(app, services, workflows, null, TestContext.Current.CancellationToken, () => applied++, shown => dialog = shown);
                completed = stage == 4;
                var saved = workflows.SavedLocations;
                Assert.Equal([cli], workflows.CurrentDiscoveryOptions.ScanRoots);
                Assert.DoesNotContain(saved.Locations, item => item.Path == cli);
                Assert.Contains(saved.Locations, item => item.Kind == "workspace" && item.Path == PathIdentity.Normalize(first));
                Assert.Contains(saved.Locations, item => item.Kind == "profile" && item.Name == "work");
            }, TestContext.Current.CancellationToken);
            Assert.False(timeout); Assert.True(completed); Assert.Equal(1, applied);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task EscapeBeforeQueuedAddCompletionKeepsDraftUnpublishedAndLateCallbackInert(int width, int height)
    {
        var root = Directory.CreateTempSubdirectory("skillview-location-cancel-");
        try
        {
            var selected = Directory.CreateDirectory(Path.Combine(root.FullName, "selected")).FullName;
            var services = Services(root.FullName); var applied = 0; var submitted = false; var timeout = false;
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
                var options = new AppOptions(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
                var workflows = Coordinator(services, options, app); Dialog? dialog = null;
                app.AddTimeout(TimeSpan.FromSeconds(15), () => { timeout = true; app.RequestStop(); return false; });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (dialog is null || submitted || !dialog.SubViews.OfType<Button>().Single(button => button.Text == "_Add").Enabled) return true;
                    submitted = true;
                    var path = dialog.SubViews.OfType<TextField>().First(); path.Text = selected; path.SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.Enter); app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    return false;
                });
                LocationsDialog.Show(app, services, workflows, null, TestContext.Current.CancellationToken, () => applied++, shown => dialog = shown);
                // Give queued stale completions an actual later application loop.
                using var next = new Window(); app.StopAfterFirstIteration = true; app.Run(next);
                Assert.Empty(workflows.CurrentDiscoveryOptions.WorkspaceDirectories);
                Assert.False(Directory.Exists(workflows.LocationsPath));
            }, TestContext.Current.CancellationToken);
            Assert.False(timeout); Assert.True(submitted); Assert.Equal(0, applied);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task RecoveryErrorIsLiteralScrollableAndEnterInTextKeepsCancelDefaultSafe(int width, int height)
    {
        var root = Directory.CreateTempSubdirectory("skillview-location-recovery-keys-");
        try
        {
            var services = Services(root.FullName); var checkedKeys = false; var timeout = false;
            await Task.Run(() =>
            {
                using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
                var options = new AppOptions(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
                var workflows = Coordinator(services, options, app);
                workflows.InitializeLocationsAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                var details = string.Join('\n', Enumerable.Range(0, 150).Select(i => "Literal line " + i))
                    + "\n--scan-root active for this session; saved values apply when launched without it\nLAST LITERAL PATH " + Path.Combine(root.FullName, "backup-01234567890123456789012345678901");
                app.AddTimeout(TimeSpan.FromSeconds(15), () => { timeout = true; app.RequestStop(); return false; });
                var result = LocationsRecoveryDialog.Show(app, services, workflows, null, TestContext.Current.CancellationToken, details, dialog =>
                {
                    app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
                    {
                        var text = dialog.SubViews.OfType<Editor>().Single();
                        if (!text.Text.Contains("Nothing was removed.", StringComparison.Ordinal)) return true;
                        var cancel = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel");
                        Assert.True(cancel.IsDefault); Assert.True(cancel.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Tab); Assert.True(text.HasFocus);
                        Assert.Contains(details, text.Text); Assert.True(text.ReadOnly); Assert.True(text.WordWrap);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.End | KeyCode.CtrlMask)); Assert.Equal(text.Text.Length, text.CaretOffset);
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter); Assert.True(dialog.IsRunning);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Home | KeyCode.CtrlMask)); Assert.Equal(0, text.CaretOffset);
                        app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab | KeyCode.ShiftMask)); Assert.True(cancel.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Tab); Assert.True(text.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Tab); Assert.True(cancel.HasFocus);
                        app.Keyboard.RaiseKeyDownEvent(Key.Enter); checkedKeys = true; return false;
                    });
                });
                Assert.Null(result); Assert.False(Directory.Exists(workflows.LocationsPath));
            }, TestContext.Current.CancellationToken);
            Assert.True(checkedKeys); Assert.False(timeout);
        }
        finally { root.Delete(true); }
    }

    private static TuiServices Services(string root)
    {
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        return TuiServices.Build(new Logger(), processEnvironment: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(root, "state"),
            ["CODEX_HOME"] = Path.Combine(home, ".codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi"),
            ["GIT_DIR"] = Path.Combine(work, ".git"),
            ["GIT_WORK_TREE"] = work,
            ["GIT_COMMON_DIR"] = Path.Combine(work, ".git"),
            ["GIT_CONFIG_COUNT"] = "0"
        }, workingDirectory: work);
    }
    private static SkillViewWorkflowCoordinator Coordinator(TuiServices services, AppOptions options, IApplication app)
    {
        var report = new EnvironmentReport
        {
            GhPath = null,
            GhVersionRaw = null,
            GhVersion = null,
            GhMeetsMinimum = false,
            Auth = GhAuthStatus.Unknown,
            GhSkillAvailable = false,
            LogDirectory = null
        };
        return new(services, options, () => app, () => null, () => report, _ => Task.FromResult(report), _ => { }, () => { }, _ => { }, (_, _) => { },
            action => app.Invoke(action), (action, _) => { app.Invoke(action); return Task.FromResult(true); }, (_, _) => false, () => { }, () => { });
    }
}
