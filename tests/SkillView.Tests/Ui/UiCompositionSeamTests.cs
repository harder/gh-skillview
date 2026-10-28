using SkillView.Inventory.Models;
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
public sealed class UiCompositionSeamTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public async Task ComposedIssuesToolbarFitsAndAdvertisedPreferenceKeysReachOnlyTheSelectedFamily(int width, int height)
    {
        var first = CapturedFindingTests.Copy("/fixtures/one/demo", "v1");
        var second = CapturedFindingTests.Copy("/fixtures/two/demo", "v2");
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        string? expectedFindingId = null;
        var kept = 0;
        var choices = 0;
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(snapshot), () => { },
            runTask: (work, _) => work().GetAwaiter().GetResult(), availability: new UpdateAvailabilitySession(),
            setIntentional: (finding, lease) =>
            {
                Assert.True(lease.IsCurrent);
                Assert.Equal(expectedFindingId, finding.Id);
                kept++;
                return Task.FromResult("Observed difference acknowledged by fixture");
            },
            resetIntentional: lease => { Assert.True(lease.IsCurrent); choices++; return Task.CompletedTask; })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        await view.LoadAsync();
        expectedFindingId = Assert.Single(view.RowsForTests).Finding!.Id;
        var session = app.Begin(window);
        try
        {
            window.Draw(new DrawContext());
            var labels = new[] { ActionLabels.CheckUpdates, ActionLabels.ReviewIssue, ActionLabels.KeepBoth, ActionLabels.Choices };
            var controls = labels.Select(label => view.SubViews.OfType<Button>().Single(button => button.Text == label.ButtonText)).ToArray();
            foreach (var control in controls)
            {
                Assert.True(control.Visible);
                Assert.InRange(control.Frame.X, 0, view.Viewport.Width - 1);
                Assert.InRange(control.Frame.Right, 1, view.Viewport.Width);
            }
            for (var index = 1; index < controls.Length; index++)
                Assert.True(controls[index - 1].Frame.Right < controls[index].Frame.X);
            var table = view.SubViews.OfType<TableView>().Single();
            table.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key('k'));
            Assert.Equal(1, kept);
            Assert.Equal(0, choices);
            table.SetFocus();
            app.Keyboard.RaiseKeyDownEvent(new Key('K'));
            Assert.Equal(1, kept);
            Assert.Equal(1, choices);
            var help = HelpOverlay.BuildMarkdown(ActionScreen.Issues);
            Assert.Contains("**k**", help);
            Assert.Contains("**K**", help);
            Assert.Contains("**F5**", help);
            Assert.DoesNotContain("**i**", help);
        }
        finally { app.End(session!); }
    }
}
