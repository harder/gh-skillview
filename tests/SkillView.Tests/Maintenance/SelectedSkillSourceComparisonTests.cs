using System.Text;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SelectedSkillSourceComparisonTests
{
    private const string Source = "---\nname: demo\ndescription: authored\nversion: 'one'\nmetadata:\n  custom: retained\n---\nAuthored instructions.\n";
    private const string Output = "---\nversion: one\ndescription: authored\nname: demo\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-ref: refs/tags/v1\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  github-path: demo\n  github-pinned: v1\n  custom: retained\n---\n\nAuthored instructions.\n";
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void SameSelectedAuthoredYamlAndBodyMayHaveKnownInstallerPresentation()
        => SelectedSkillSourceComparison.Require(Bytes(Source), Bytes(Output), 420, 420);

    [Theory]
    [InlineData("Authored instructions.", "Different instructions.")]
    [InlineData("description: authored", "description: changed")]
    [InlineData("version: one", "version: two")]
    [InlineData("custom: retained", "custom: replaced")]
    public void TrackingCannotConcealChangedAuthoredContent(string before, string after)
    {
        var error = Assert.Throws<IOException>(() => SelectedSkillSourceComparison.Require(Bytes(Source), Bytes(Output.Replace(before, after)), 420, 420));
        Assert.Contains("authored SKILL.md", error.Message);
    }

    [Fact]
    public void ExecutableModeCannotChangeWhileAuthoredBytesAndTrackingMatch()
    {
        var error = Assert.Throws<IOException>(() => SelectedSkillSourceComparison.Require(Bytes(Source), Bytes(Output), 420, 493));
        Assert.Contains("mode differs", error.Message);
    }

    [Fact]
    public void InvalidTrackingCannotBecomeValidAfterRemovingInstallerFields()
    {
        var bad = Output.Replace("github-pinned: v1", "github-pinned: [v1]");
        var error = Assert.Throws<IOException>(() => SelectedSkillSourceComparison.Require(Bytes(Source), Bytes(bad), 420, 420));
        Assert.Contains("before installer normalization", error.Message);
    }

    [Fact]
    public void UnknownScalarCoercionDoesNotBecomeAnAuthoredVersionMatch()
    {
        Assert.Throws<IOException>(() => SelectedSkillSourceComparison.Require(Bytes(Source.Replace("'one'", "'1'")), Bytes(Output.Replace("version: one", "version: 1")), 420, 420));
    }
}
