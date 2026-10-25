using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class CopiesScreenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPinFlagAndDetailRemainExplicitForUnreadableOrMalformedMetadata(bool malformed)
    {
        var skill = CopiesPresentationTests.Copy("/unknown") with
        {
            Validity = malformed ? ValidityState.Valid : ValidityState.UnparsableFrontMatter,
            Package = malformed ? new("test/repo", "github", null, null, null) { PinnedRef = " " } : null
        };
        var checkedRow = false;
        var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                Assert.Equal("?", dialog.SubViews.OfType<TableView>().Single().Table![0, 3]);
                Assert.Contains("pin state unknown", dialog.SubViews.OfType<Editor>().Single().Text.ToString());
                checkedRow = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                return false;
            });
            Assert.Null(CopiesScreen.Show(app, new Logger(), new([skill], 0, 0, false),
                (_, _, _) => throw new InvalidOperationException("No implicit comparison"),
                TestContext.Current.CancellationToken, value => dialog = value));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.True(checkedRow);
        Assert.False(timedOut);
    }

    [Theory]
    [InlineData(80, 24, "Update / version...", "Update")]
    [InlineData(120, 36, "Cleanup review...", "Cleanup")]
    public async Task RealKeyboardSelectsComparesAndHandsOffOnlyTheChosenPhysicalCopy(int width, int height, string actionLabel, string action)
    {
        var first = CopiesPresentationTests.Copy("/first"); var second = CopiesPresentationTests.Copy("/second", reference: "refs/tags/v2");
        var calls = 0; var timedOut = false; var compared = false; CopiesChoice? choice = null;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(width, height);
            Dialog? dialog = null;
            var requested = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var table = dialog.SubViews.OfType<TableView>().Single();
                if (!requested)
                {
                    table.SetFocus(); Assert.True(table.HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
                    Assert.Equal(1, table.GetSelectedRow());
                    foreach (var control in dialog.SubViews)
                    {
                        Assert.InRange(control.Frame.Left, 0, dialog.Viewport.Width - 1); Assert.InRange(control.Frame.Right, 1, dialog.Viewport.Width);
                        Assert.InRange(control.Frame.Top, 0, dialog.Viewport.Height - 1); Assert.InRange(control.Frame.Bottom, 1, dialog.Viewport.Height);
                    }
                    var buttons = dialog.SubViews.OfType<Button>().ToArray();
                    foreach (var button in buttons)
                        Assert.DoesNotContain(buttons, other => !ReferenceEquals(button, other) && button.Frame.IntersectsWith(other.Frame));
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(dialog.SubViews.OfType<Editor>().Single().HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(buttons.Single(button => button.Text == "Use as reference").HasFocus);
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Tab));
                    Assert.True(buttons.Single(button => button.Text == "Compare").HasFocus);
                    requested = true;
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                    return true;
                }
                var text = dialog.SubViews.OfType<Editor>().Single().Text.ToString();
                if (!text.Contains("Differing files: 1", StringComparison.Ordinal)) return true;
                Assert.Contains("Different recorded revisions", text); Assert.Contains("Changed: SKILL.md", text);
                Assert.True(table.HasFocus);
                compared = true;
                var actionButton = dialog.SubViews.OfType<Button>().Single(button => button.Text == actionLabel);
                actionButton.SetFocus(); Assert.True(actionButton.HasFocus); app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); return false;
            });
            var group = new CopiesGroup([first, second], 0, 0, false)
            { CleanupCandidates = ImmutableDictionary<string, SkillView.Inventory.CleanupClassifier.Candidate>.Empty.Add(second.InstallationId, CopiesPresentationTests.Collision(second)) };
            choice = CopiesScreen.Show(app, new Logger(), group, (left, right, _) =>
            { calls++; Assert.Same(first, left); Assert.Same(second, right); return Task.FromResult(CopiesPresentationTests.Compared(left, right)); },
                TestContext.Current.CancellationToken, readyForTests: value => dialog = value);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(compared); Assert.Equal(1, calls);
        Assert.NotNull(choice); Assert.Equal(action, choice.Action.ToString()); Assert.Equal(second.InstallationId, choice.Subject.InstallationId); Assert.Equal(second.ResolvedPath, choice.Subject.Path);
    }

    [Fact]
    public async Task ManagedAndUnrelatedRowsExplainUnavailableActionsWithoutClosing()
    {
        var first = CopiesPresentationTests.Copy("/first");
        var managed = CopiesPresentationTests.Copy("/managed", "renamed") with
        { OwnershipClaims = [new("owner", "plugin", null, null, null) { InstallationMode = "managed-read-only", ReadOnly = true }] };
        var checkedRow = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var table = dialog.SubViews.OfType<TableView>().Single(); table.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.CursorDown));
                Assert.Equal(1, table.GetSelectedRow());
                Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Update / version...").Enabled);
                Assert.False(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Cleanup review...").Enabled);
                var text = dialog.SubViews.OfType<Editor>().Single().Text.ToString();
                Assert.Contains("Managed copy: use its owning tool", text);
                Assert.Contains("No unambiguous matching cleanup issue", text);
                checkedRow = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
            });
            Assert.Null(CopiesScreen.Show(app, new Logger(), new([first, managed], 0, 0, false),
                (_, _, _) => throw new InvalidOperationException("No implicit comparison."), TestContext.Current.CancellationToken, value => dialog = value));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(checkedRow);
    }

    [Fact]
    public async Task ComparisonFailureRestoresFocusAndSanitizesTheDiagnostic()
    {
        var first = CopiesPresentationTests.Copy("/first"); var second = CopiesPresentationTests.Copy("/second");
        var requested = false; var checkedFailure = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                var table = dialog.SubViews.OfType<TableView>().Single();
                if (!requested)
                {
                    table.SetSelectedRow(1); dialog.SubViews.OfType<Button>().Single(button => button.Text == "Compare").SetFocus();
                    requested = true; app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); return true;
                }
                var status = dialog.SubViews.OfType<Label>().Select(label => label.Text.ToString()).FirstOrDefault(text => text.StartsWith("Comparison unavailable:", StringComparison.Ordinal));
                if (status is null) return true;
                Assert.DoesNotContain('\u001b', status); Assert.DoesNotContain('\n', status);
                Assert.True(table.Enabled); Assert.True(table.HasFocus);
                Assert.True(dialog.SubViews.OfType<Button>().Single(button => button.Text == "Compare").Enabled);
                checkedFailure = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false;
            });
            Assert.Null(CopiesScreen.Show(app, new Logger(), new([first, second], 0, 0, false),
                (_, _, _) => throw new IOException("untrusted\u001b[31m\npath"), TestContext.Current.CancellationToken, value => dialog = value));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(checkedFailure);
    }

    [Fact]
    public async Task EscapeCancelsAnActiveComparisonAndWaitsForItBeforeClosing()
    {
        var first = CopiesPresentationTests.Copy("/first"); var second = CopiesPresentationTests.Copy("/second");
        var started = false; var canceled = false; var stopped = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi"); app.Driver!.SetScreenSize(80, 24);
            Dialog? dialog = null;
            var requested = false;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (dialog is null) return true;
                if (started) { app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc)); return false; }
                if (requested) return true;
                var table = dialog.SubViews.OfType<TableView>().Single(); table.SetSelectedRow(1);
                dialog.SubViews.OfType<Button>().Single(button => button.Text == "Compare").SetFocus();
                requested = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter)); return true;
            });
            Assert.Null(CopiesScreen.Show(app, new Logger(), new([first, second], 0, 0, false), async (_, _, token) =>
            {
                started = true;
                try { await Task.Delay(System.Threading.Timeout.Infinite, token); throw new InvalidOperationException("No uncanceled completion"); }
                catch (OperationCanceledException) { canceled = true; throw; }
                finally { stopped = true; }
            }, TestContext.Current.CancellationToken, value => dialog = value));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut); Assert.True(started); Assert.True(canceled); Assert.True(stopped);
    }
}
