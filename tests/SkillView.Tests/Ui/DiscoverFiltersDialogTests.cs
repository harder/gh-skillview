using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DiscoverFiltersDialogTests
{
    [Fact]
    public void EditableEscape_LeavesFieldWithoutClosingDialog()
    {
        var key = new Key(KeyCode.Esc);
        var leftField = false;
        string? hint = null;

        var handled = DiscoverFiltersDialog.HandleEditableEscape(
            key,
            () => leftField = true,
            value => hint = value);

        Assert.True(handled);
        Assert.True(key.Handled);
        Assert.True(leftField);
        Assert.Contains("Esc again", hint);
    }

    [Fact]
    public void NonEscape_RemainsAvailableToEditor()
    {
        var key = new Key('x');

        var handled = DiscoverFiltersDialog.HandleEditableEscape(
            key,
            () => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        Assert.False(handled);
        Assert.False(key.Handled);
    }
}
