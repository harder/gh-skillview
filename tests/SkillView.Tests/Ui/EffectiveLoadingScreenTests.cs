using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class EffectiveLoadingScreenTests
{
    [Fact]
    public async Task CancelingAnActiveAnalysisDrainsItsWorkerBeforeTheModalReturns()
    {
        var started = 0; var canceled = 0; var cancelRequested = false; var timedOut = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(120, 40);
            Dialog? modal = null;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                if (Volatile.Read(ref started) == 0)
                {
                    modal.SubViews.OfType<Button>().Single(button => button.Text == "_Analyze").SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                }
                else if (!cancelRequested)
                {
                    cancelRequested = true;
                    app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Esc));
                }
                return true;
            });
            var selected = new InstalledSkill
            {
                Name = "demo",
                ResolvedPath = "/fixture/demo",
                ScanRoot = "/fixture",
                Scope = Scope.User,
                Agents = [],
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                Ignored = false,
                IsSymlinked = false,
                InstalledAt = null
            };
            EffectiveLoadingScreen.Show(app, new Logger(), selected, System.Environment.CurrentDirectory, async (_, token) =>
            {
                Interlocked.Increment(ref started);
                try { return await new TaskCompletionSource<EffectiveSkillAnalysis>().Task.WaitAsync(token); }
                finally { if (token.IsCancellationRequested) Interlocked.Increment(ref canceled); }
            }, dialog => modal = dialog);
            Assert.Equal(1, Volatile.Read(ref canceled));
            Assert.False(app.Initialized && app.TopRunnableView is not null);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.Equal(1, started); Assert.True(cancelRequested); Assert.False(timedOut);
    }
}
