using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SkillHealthFormatterTests
{
    [Theory]
    [InlineData(ValidityState.Valid, false, "Healthy")]
    [InlineData(ValidityState.MissingSkillMd, false, "Needs review")]
    [InlineData(ValidityState.Valid, true, "Symlink")]
    public void RowBadge_MapsValidityAndSymlinkState(
        ValidityState validity,
        bool isSymlinked,
        string expected)
    {
        var badge = SkillHealthFormatter.RowBadge(validity, isSymlinked);

        Assert.Equal(expected, badge);
    }

    [Theory]
    [InlineData(ValidityState.Valid, false, "OK")]
    [InlineData(ValidityState.Valid, true, "SYM")]
    [InlineData(ValidityState.MissingSkillMd, false, "REV")]
    public void CompactBadge_UsesShortForms(
        ValidityState validity,
        bool isSymlinked,
        string expected)
    {
        var badge = SkillHealthFormatter.CompactBadge(validity, isSymlinked);

        Assert.Equal(expected, badge);
    }
}
