using System.Collections.Concurrent;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class CompactInstallDialogTests
{
    [Theory]
    [InlineData(80, 24, false, -1, false)]
    [InlineData(120, 36, false, -1, false)]
    [InlineData(80, 24, true, -1, false)]
    [InlineData(120, 36, true, -1, false)]
    [InlineData(80, 24, false, 0, false)]
    [InlineData(120, 36, true, 1, false)]
    [InlineData(80, 24, false, 2, false)]
    [InlineData(80, 24, true, -1, true)]
    [InlineData(120, 36, false, -1, true)]
    public async Task VisibleInitialChoiceAndKeyboardSubmissionAgree(int width, int height, bool seeded, int alternate, bool all)
    {
        using var fixture = new Fixture(seeded);
        var interacted = false;
        var result = await RunModal(fixture, width, height, all, (app, dialog) =>
        {
            var selector = dialog.SubViews.OfType<OptionSelector>().Single();
            var buttons = dialog.SubViews.OfType<Button>().Where(b => b.Visible).ToArray();
            var install = buttons.Single(b => b.Text == "Install");
            Assert.True(install.HasFocus);
            Assert.Equal(seeded ? 0 : 1, selector.Value);
            Assert.Equal(selector.Value, selector.FocusedItem);
            Assert.Equal(CheckState.Checked, selector.SubViews.OfType<CheckBox>().ElementAt(selector.Value!.Value).Value);
            foreach (var control in dialog.SubViews.Where(v => v.Visible && v is Label or OptionSelector or TextField or Button))
                AssertFits(control, dialog);
            for (var i = 1; i < buttons.Length; i++)
                Assert.True(buttons[i - 1].Frame.Right < buttons[i].Frame.Left);
            Assert.All(buttons, b => Assert.Equal(1, b.Frame.Height));
            Assert.Equal(all ? 2 : 3, buttons.Length);

            // Exercise real traversal, then return to the primary action.
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
            Assert.True(buttons[1].HasFocus);
            install.SetFocus();
            if (alternate >= 0)
            {
                selector.SetFocus();
                var initial = selector.FocusedItem;
                for (var i = initial; i != alternate; i += i < alternate ? 1 : -1)
                    app.Keyboard.RaiseKeyDownEvent(new Key(i < alternate ? KeyCode.CursorRight : KeyCode.CursorLeft));
                app.Keyboard.RaiseKeyDownEvent(new Key(' '));
                Assert.Equal(alternate, selector.Value);
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.True(dialog.IsRunning);
                if (alternate == 2)
                {
                    Assert.False(install.Enabled);
                    var path = dialog.SubViews.OfType<TextField>().Single(field => !field.ReadOnly);
                    Assert.True(path.Visible);
                    path.Text = "  " + fixture.Custom + "  ";
                    Assert.True(install.Enabled);
                    Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.Text.Contains("overrides scope", StringComparison.Ordinal));
                }
            }
            interacted = true;
            install.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.True(interacted);
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        var args = result.Install!.CommandLine;
        var expected = alternate >= 0 ? alternate : seeded ? 0 : 1;
        if (expected == 2)
        {
            Assert.Contains(fixture.Custom, args);
            Assert.Contains("--dir", args);
            Assert.DoesNotContain("--scope", args);
            Assert.DoesNotContain("--agent", args);
        }
        else
        {
            Assert.Equal(expected == 0 ? "project" : "user", args[args.ToList().IndexOf("--scope") + 1]);
            Assert.Contains("universal", args);
        }
        Assert.Equal(all, args.Contains("--all"));
    }

    [Theory]
    [InlineData("Advanced…", false)]
    [InlineData("Cancel", false)]
    [InlineData("Cancel", true)]
    [InlineData("Esc", false)]
    public async Task SecondaryActionsDoNotLaunchInstall(string action, bool all)
    {
        using var fixture = new Fixture(false);
        var result = await RunModal(fixture, 80, 24, all, (app, dialog) =>
        {
            if (action != "Esc") dialog.SubViews.OfType<Button>().Single(b => b.Text == action).SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(action == "Esc" ? KeyCode.Esc : KeyCode.Enter));
        });
        Assert.Equal(action == "Advanced…" ? InstallConfirmModal.Outcome.EscalateToAdvanced : InstallConfirmModal.Outcome.Cancelled, result.Outcome);
        Assert.Null(result.Install);
        Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Message.Contains("/bin/echo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedAgentsRemainSelectedThroughSubmission()
    {
        using var fixture = new Fixture(false);
        var ghPath = Path.Combine(fixture.Custom, "listing-gh");
        Directory.CreateDirectory(fixture.Custom);
        File.WriteAllText(ghPath, "#!/bin/sh\nif [ \"$#\" = 4 ]; then printf 'demo\\tFixture\\n'; else /bin/echo \"$@\"; fi\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ghPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            var grid = dialog.SubViews.Single(v => v.SubViews.OfType<CheckBox>().Count() == InstallAgentCatalog.Entries.Length);
            foreach (var box in grid.SubViews.OfType<CheckBox>())
                box.Value = box.Text is "Claude" or "Codex" ? CheckState.Checked : CheckState.UnChecked;
            Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.Text.Contains("Selected agents", StringComparison.Ordinal));
            dialog.SubViews.OfType<Button>().Single(button => button.Text == "Install").SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        }, ghPath);
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        Assert.Contains("claude-code", result.Install!.CommandLine);
        Assert.Contains("codex", result.Install.CommandLine);
        Assert.DoesNotContain("universal", result.Install.CommandLine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectedGitProjectDrivesDefaultAndNativePlacementFromCapturedChildContext(bool aliasedAncestor)
    {
        using var fixture = new Fixture(true, aliasedAncestor);
        var project = fixture.Runner.EffectiveWorkingDirectory;
        Assert.True(SecureRemovalBackend.TryCanonicalizePath(project, out var nativeProject, out var error), error);
        if (aliasedAncestor) Assert.NotEqual(project, nativeProject);
        var init = await fixture.Runner.RunAsync("/usr/bin/git", ["init", "--quiet"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        var elsewhere = Path.Combine(fixture.Custom, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        fixture.Runner = fixture.Runner.WithContext(elsewhere, new Dictionary<string, string>
        { ["GIT_DIR"] = Path.Combine(project, ".git"), ["GIT_WORK_TREE"] = project });
        var gitRoot = await fixture.Runner.RunAsync("/usr/bin/git", ["rev-parse", "--show-toplevel"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(gitRoot.Succeeded);
        Assert.Equal(nativeProject, gitRoot.StdOut.TrimEnd('\r', '\n'));
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            Assert.Equal(0, dialog.SubViews.OfType<OptionSelector>().Single().Value);
            Assert.Equal(nativeProject, dialog.SubViews.OfType<TextField>().Single(field => field.ReadOnly).Text);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        Assert.Contains("project", result.Install!.CommandLine);
        var destinations = await GhPlacementCatalog.DestinationsAsync(
            new(Agents: ["universal"], Scope: "project", WorkingDirectory: elsewhere), fixture.Runner, TestContext.Current.CancellationToken);
        Assert.Equal([Path.Combine(nativeProject, ".agents", "skills")], destinations);
        Assert.Equal(elsewhere, fixture.Runner.EffectiveWorkingDirectory);
        Assert.Equal(Path.Combine(project, ".git"), fixture.Runner.GetEffectiveEnvironmentVariable("GIT_DIR"));
        Assert.Equal(project, fixture.Runner.GetEffectiveEnvironmentVariable("GIT_WORK_TREE"));
    }

    [Fact]
    public async Task ActiveInstallGuardsSecondaryActionsAndEscapeCancelsChild()
    {
        using var fixture = new Fixture(false);
        var stall = Path.Combine(fixture.Custom, "stall-gh");
        Directory.CreateDirectory(fixture.Custom);
        File.WriteAllText(stall, "#!/bin/sh\nexec /bin/sleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stall, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var checkedActive = false;
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (!fixture.Logger.Snapshot().Any(entry => entry.Message.Contains(stall, StringComparison.Ordinal))) return true;
                var buttons = dialog.SubViews.OfType<Button>().ToArray();
                Assert.All(buttons, button => Assert.False(button.Enabled));
                buttons.Single(button => button.Text == "Advanced…").InvokeCommand(Command.Accept);
                Assert.True(dialog.IsRunning);
                buttons.Single(button => button.Text == "Install").InvokeCommand(Command.Accept);
                Assert.Single(fixture.Logger.Snapshot(), entry => entry.Message.Contains(stall, StringComparison.Ordinal));
                dialog.SubViews.OfType<TextField>().Single(field => field.ReadOnly).SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                Assert.True(dialog.IsRunning);
                checkedActive = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
        }, stall);
        Assert.True(checkedActive);
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task PendingProjectProbeCannotSubmitAndEscapeCancelsIt()
    {
        using var fixture = new Fixture(false);
        Directory.CreateDirectory(fixture.Custom);
        var git = Path.Combine(fixture.Custom, "git");
        File.WriteAllText(git, "#!/bin/sh\nexec /bin/sleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fixture.Runner = fixture.Runner.WithContext(fixture.Runner.EffectiveWorkingDirectory,
            new Dictionary<string, string> { ["PATH"] = fixture.Custom });
        var checkedPending = false;
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (!fixture.Logger.Snapshot().Any(entry => entry.Message.Contains(git, StringComparison.Ordinal))) return true;
                Assert.False(dialog.SubViews.OfType<OptionSelector>().Single().Enabled);
                var install = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Install");
                Assert.False(install.Enabled);
                install.InvokeCommand(Command.Accept);
                Assert.True(dialog.IsRunning);
                Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Message.Contains("/bin/echo", StringComparison.Ordinal));
                checkedPending = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
        }, waitForPreparation: false);
        Assert.True(checkedPending);
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task NonGitSeedKeepsExistingUserDefault()
    {
        using var fixture = new Fixture(false);
        Directory.CreateDirectory(Path.Combine(fixture.Runner.EffectiveWorkingDirectory, ".agents", "skills"));
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            Assert.Equal(1, dialog.SubViews.OfType<OptionSelector>().Single().Value);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        Assert.Contains("user", result.Install!.CommandLine);
    }

    [Fact]
    public async Task FailedProjectLookupStillAllowsExplicitCustomPathAndFieldEnter()
    {
        using var fixture = new Fixture(false);
        Directory.CreateDirectory(fixture.Custom);
        var git = Path.Combine(fixture.Custom, "git");
        File.WriteAllText(git, "#!/bin/sh\nprintf 'relative-root\\n'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fixture.Runner = fixture.Runner.WithContext(fixture.Runner.EffectiveWorkingDirectory,
            new Dictionary<string, string> { ["PATH"] = fixture.Custom });
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            var scope = dialog.SubViews.OfType<OptionSelector>().Single();
            Assert.Equal(1, scope.Value);
            scope.Value = 2;
            var path = dialog.SubViews.OfType<TextField>().Single(field => !field.ReadOnly);
            path.Text = fixture.Custom;
            path.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        Assert.Contains("--dir", result.Install!.CommandLine);
        Assert.Contains(fixture.Custom, result.Install.CommandLine);
        Assert.DoesNotContain("--scope", result.Install.CommandLine);
        Assert.Equal([fixture.Custom], await GhPlacementCatalog.DestinationsAsync(
            new(Path: fixture.Custom), fixture.Runner, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task QueuedPreparationPreservesCancelFocusAndEnterCancels(int width, int height)
    {
        using var fixture = new Fixture(false);
        var result = await RunQueuedPreparation(fixture, width, height, (app, dialog, operation, completion) =>
        {
            // Status initially owns focus; Tab visits Base, then Cancel.
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
            var cancel = dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel");
            Assert.True(cancel.HasFocus);
            Assert.Equal(ModalOperationTracker.Ownership.AwaitingUiCompletion, operation.CurrentOwnership);
            completion();
            Assert.Equal(ModalOperationTracker.Ownership.None, operation.CurrentOwnership);
            Assert.True(cancel.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
        Assert.Null(result.Install);
        Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Message.Contains("/bin/echo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task QueuedPreparationPreservesBaseFieldFocus(int width, int height)
    {
        using var fixture = new Fixture(false);
        var result = await RunQueuedPreparation(fixture, width, height, (app, dialog, _, completion) =>
        {
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
            var path = dialog.SubViews.OfType<TextField>().Single(field => field.ReadOnly);
            Assert.True(path.HasFocus);
            completion();
            Assert.True(path.HasFocus);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
        Assert.Null(result.Install);
    }

    [Theory]
    [InlineData(80, 24, false, false)]
    [InlineData(120, 36, false, false)]
    [InlineData(80, 24, true, false)]
    [InlineData(120, 36, false, true)]
    public async Task CancellationAfterProbeWorkerCompletionClosesWithoutPublishingReady(
        int width, int height, bool cancelButton, bool failedLookup)
    {
        using var fixture = new Fixture(false);
        if (failedLookup)
        {
            Directory.CreateDirectory(fixture.Custom);
            var git = Path.Combine(fixture.Custom, "git");
            File.WriteAllText(git, "#!/bin/sh\nprintf 'relative-root\\n'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            fixture.Runner = fixture.Runner.WithContext(fixture.Runner.EffectiveWorkingDirectory,
                new Dictionary<string, string> { ["PATH"] = fixture.Custom });
        }
        var result = await RunQueuedPreparation(fixture, width, height, (app, dialog, operation, completion) =>
        {
            if (cancelButton) dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel").SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(cancelButton ? KeyCode.Enter : KeyCode.Esc));
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.True(dialog.IsRunning);
            Assert.Equal(ModalOperationTracker.Ownership.AwaitingUiCompletion, operation.CurrentOwnership);
            completion();
            Assert.False(dialog.SubViews.OfType<OptionSelector>().Single().Enabled);
            Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Install").Enabled);
            Assert.DoesNotContain(dialog.SubViews.OfType<Label>(), label => label.Text == " ready" || label.Text.Contains("User or Custom available", StringComparison.Ordinal));
            // Closing still owns the queued completion until normal modal teardown.
            Assert.Equal(ModalOperationTracker.Ownership.AwaitingUiCompletion, operation.CurrentOwnership);
        });
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
        Assert.Null(result.Install);
        Assert.DoesNotContain(fixture.Logger.Snapshot(), entry => entry.Message.Contains("/bin/echo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisplaySanitizesControlPathsButPlacementAndChildKeepRawBase(bool project, bool aliasedAncestor)
    {
        using var fixture = new Fixture(false, aliasedAncestor);
        var rawBase = Path.Combine(fixture.Custom, "base-\u001b[31mred\u001b[0m\u0007\t");
        Directory.CreateDirectory(rawBase);
        Assert.True(SecureRemovalBackend.TryCanonicalizePath(rawBase, out var nativeBase, out var error), error);
        if (aliasedAncestor) Assert.NotEqual(rawBase, nativeBase);
        // Git and getcwd report the physical project address; HOME remains the
        // exact lexical value supplied to the child, including ancestor aliases.
        var expectedBase = project ? nativeBase : rawBase;
        if (project)
        {
            Directory.CreateDirectory(Path.Combine(rawBase, ".agents", "skills"));
            fixture.Runner = fixture.Runner.WithContext(rawBase, new Dictionary<string, string>());
            var init = await fixture.Runner.RunAsync("/usr/bin/git", ["init", "--quiet"], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(init.Succeeded);
        }
        else fixture.Runner = fixture.Runner.WithContext(fixture.Runner.EffectiveWorkingDirectory,
            new Dictionary<string, string> { ["HOME"] = rawBase, ["USERPROFILE"] = rawBase });
        var child = Path.Combine(fixture.Custom, "echo-context");
        File.WriteAllText(child, project ? "#!/bin/sh\nprintf '%s' \"$PWD\"\n" : "#!/bin/sh\nprintf '%s' \"$HOME\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            Assert.Equal(project ? 0 : 1, dialog.SubViews.OfType<OptionSelector>().Single().Value);
            var displayed = dialog.SubViews.OfType<TextField>().Single(field => field.ReadOnly).Text;
            Assert.Equal(Path.Combine(Path.GetDirectoryName(expectedBase)!, "base-red\uFFFD"), displayed);
            Assert.Contains("red", displayed);
            Assert.DoesNotContain("[31m", displayed);
            Assert.DoesNotContain(displayed, char.IsControl);
            Assert.NotEqual(rawBase, displayed);
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        }, child);
        Assert.Equal(InstallConfirmModal.Outcome.Installed, result.Outcome);
        Assert.Equal(expectedBase, result.Install!.StdOut);
        Assert.Contains(project ? "project" : "user", result.Install.CommandLine);
        Assert.Equal(rawBase, project ? fixture.Runner.EffectiveWorkingDirectory : fixture.Runner.GetEffectiveEnvironmentVariable("HOME"));
        if (!project) Assert.Equal(rawBase, fixture.Runner.GetEffectiveEnvironmentVariable("USERPROFILE"));
        Assert.Equal([Path.Combine(expectedBase, ".agents", "skills")], await GhPlacementCatalog.DestinationsAsync(
            new(Agents: ["universal"], Scope: project ? "project" : "user", WorkingDirectory: fixture.Runner.EffectiveWorkingDirectory),
            fixture.Runner, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisplayBoundsLongHomeWithoutChangingRawContext()
    {
        using var fixture = new Fixture(false);
        var rawHome = Path.Combine(fixture.Custom, new string('x', InstallConfirmModal.MaxBasePathDisplayCharacters + 128));
        fixture.Runner = fixture.Runner.WithContext(fixture.Runner.EffectiveWorkingDirectory,
            new Dictionary<string, string> { ["HOME"] = rawHome, ["USERPROFILE"] = rawHome });
        var result = await RunModal(fixture, 80, 24, false, (app, dialog) =>
        {
            var displayed = dialog.SubViews.OfType<TextField>().Single(field => field.ReadOnly).Text;
            Assert.InRange(displayed.Length, 1, InstallConfirmModal.MaxBasePathDisplayCharacters);
            Assert.EndsWith("…", displayed);
            Assert.Equal(rawHome, fixture.Runner.GetEffectiveEnvironmentVariable("HOME"));
            dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cancel").SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
        });
        Assert.Equal(InstallConfirmModal.Outcome.Cancelled, result.Outcome);
        Assert.Null(result.Install);
    }

    private static async Task<InstallConfirmModal.Result> RunQueuedPreparation(Fixture fixture, int width, int height,
        Action<IApplication, Dialog, ModalOperationTracker, Action> interact)
    {
        var queued = new ConcurrentQueue<Action>();
        ModalOperationTracker? operation = null;
        var checkedCompletion = false;
        var result = await RunModal(fixture, width, height, false, (app, dialog) =>
        {
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (operation!.CurrentOwnership != ModalOperationTracker.Ownership.AwaitingUiCompletion || !queued.TryDequeue(out var completion)) return true;
                Assert.False(dialog.SubViews.OfType<OptionSelector>().Single().Enabled);
                Assert.Contains(dialog.SubViews.OfType<Label>(), label => label.HasFocus && label.Text.Contains("checking project scope", StringComparison.Ordinal));
                checkedCompletion = true;
                interact(app, dialog, operation, completion);
                return false;
            });
        }, waitForPreparation: false, invokeForTests: queued.Enqueue, readyForTests: (_, owner) => operation = owner);
        Assert.True(checkedCompletion);
        return result;
    }

    private static async Task<InstallConfirmModal.Result> RunModal(Fixture fixture, int width, int height, bool all,
        Action<IApplication, Dialog> interact, string ghPath = "/bin/echo", bool waitForPreparation = true,
        Action<Action>? invokeForTests = null, Action<Dialog, ModalOperationTracker>? readyForTests = null)
    {
        return await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            var timedOut = false;
            var timeoutDetails = "";
            app.AddTimeout(TimeSpan.FromSeconds(8), () =>
            {
                timedOut = true;
                timeoutDetails = string.Join("\n", dialog!.SubViews.OfType<Label>().Select(label => label.Text))
                    + "\n" + string.Join("\n", fixture.Logger.Snapshot().Select(entry => entry.Message));
                app.RequestStop();
                return false;
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(20), () =>
            {
                if (dialog is null || (waitForPreparation && !dialog.SubViews.OfType<OptionSelector>().Single().Enabled)) return true;
                interact(app, dialog);
                return false;
            });
            var result = new InstallConfirmModal(app, new GhSkillInstallService(fixture.Runner, fixture.Logger), fixture.Logger,
                ghPath, new InstallRequest("fixture/tools", "demo"), all).Show((value, operation) =>
                {
                    dialog = value;
                    readyForTests?.Invoke(value, operation);
                }, invokeForTests);
            Assert.False(timedOut, timeoutDetails);
            return result;
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
    }

    private static void AssertFits(View control, View parent)
    {
        Assert.InRange(control.Frame.Left, 0, parent.Viewport.Width - 1);
        Assert.InRange(control.Frame.Right, 1, parent.Viewport.Width);
        Assert.InRange(control.Frame.Top, 0, parent.Viewport.Height - 1);
        Assert.InRange(control.Frame.Bottom, 1, parent.Viewport.Height);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "skillview-compact-" + Guid.NewGuid().ToString("N"));
        private readonly string _contextRoot;
        internal string Custom => Path.Combine(_contextRoot, "custom");
        internal Logger Logger { get; } = new(LogLevel.Debug);
        internal ProcessRunner Runner { get; set; }

        internal Fixture(bool seeded, bool aliasedAncestor = false)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix echo as an inert gh child; built PTY checks cover actual gh separately.");
            _contextRoot = _root;
            if (aliasedAncestor)
            {
                var physical = Path.Combine(_root, "physical");
                Directory.CreateDirectory(physical);
                _contextRoot = Path.Combine(_root, "alias");
                Directory.CreateSymbolicLink(_contextRoot, physical);
            }
            var work = Path.Combine(_contextRoot, "work");
            var home = Path.Combine(_contextRoot, "home");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(home);
            if (seeded) Directory.CreateDirectory(Path.Combine(work, ".agents", "skills"));
            Runner = new ProcessRunner(Logger).WithIsolatedContext(work, new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["PATH"] = "/usr/bin:/bin",
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
                ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
                ["XDG_DATA_HOME"] = Path.Combine(_root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(_root, "cache"),
                ["CODEX_HOME"] = Path.Combine(home, ".codex"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
                ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
                ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi", "agent"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "/dev/null",
                ["GIT_CONFIG_COUNT"] = "0"
            });
            if (seeded)
            {
                var init = Runner.RunAsync("/usr/bin/git", ["init", "--quiet"], cancellationToken: TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult();
                Assert.True(init.Succeeded);
            }
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
