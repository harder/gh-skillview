using SkillView.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class RemovalWizardRoutingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalAcceptanceKeepsWizardAliveForOwnedRemoval(bool keyboard)
    {
        var timedOut = false;
        var accepted = 0;
        var completionObserved = false;
        await Task.Run(() =>
        {
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(120, 36);
            using var wizard = new RemoveScreen.RemovalWizard { Width = 80, Height = 24 };
            var choose = new WizardStep { Title = "Choose" };
            var review = new WizardStep { Title = "Review" };
            var confirm = new WizardStep { Title = "Confirm" };
            wizard.AddStep(choose);
            wizard.AddStep(review);
            wizard.AddStep(confirm);
            wizard.Accepting += (_, args) =>
            {
                args.Handled = true;
                Assert.Same(confirm, wizard.CurrentStep);
                accepted++;
            };
            var phase = 0;
            app.AddTimeout(TimeSpan.FromSeconds(8), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                Assert.True(wizard.IsRunning);
                if (phase == 3)
                {
                    Assert.Equal(1, accepted);
                    completionObserved = true;
                    app.RequestStop();
                    return false;
                }
                Assert.Same(new[] { choose, review, confirm }[phase], wizard.CurrentStep);
                Assert.Equal(0, accepted);
                wizard.NextFinishButton.SetFocus();
                if (keyboard) app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                else wizard.NextFinishButton.InvokeCommand(Command.Accept);
                phase++;
                return true;
            });
            app.Run(wizard);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
        Assert.False(timedOut);
        Assert.Equal(1, accepted);
        Assert.True(completionObserved);
    }
}
