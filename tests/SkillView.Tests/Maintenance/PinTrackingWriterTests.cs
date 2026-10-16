using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class PinTrackingWriterTests
{
    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void RemovesWholePinMemberWithExactUtf8FramingAndUnrelatedBytes(string newline, bool bom)
    {
        var prefix = (bom ? "\uFEFF" : "") + "---\nname: demo\ndescription: 'Tést 日本語 🐈'\n# author comment\nmetadata:\n  github-repo: https://github.com/o/r\n  github-ref: refs/tags/v1\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  ";
        const string pin = "github-pinned: 'v1'";
        var suffix = " # retain this comment\n  own: 'café'\n---\nRaw body.\n";
        var source = (prefix + pin + suffix).Replace("\n", newline);
        var result = PinTrackingWriter.Unpin(Encoding.UTF8.GetBytes(source));
        Assert.Equal(Encoding.UTF8.GetBytes((prefix + suffix).Replace("\n", newline)), result);
        var (_, parsed, valid) = FrontMatterParser.Parse(Encoding.UTF8.GetString(result));
        Assert.True(valid); Assert.Empty(parsed.Diagnostics); Assert.False(parsed.Pinned); Assert.Null(parsed.PinnedRef);
        Assert.Equal("refs/tags/v1", parsed.GithubRef); Assert.Equal("café", parsed.Metadata["own"]);
    }

    [Theory]
    [InlineData("metadata:\n  github-pinned: v1\n")]
    [InlineData("metadata: {github-pinned: v1, own: value}\n")]
    [InlineData("metadata:\n  github-pinned: >-\n    v1\n  own: value\n")]
    [InlineData("metadata:\n  github-pinned: &pin v1\n  own: value\n")]
    [InlineData("metadata:\n  github-pinned: v1\n  github-pinned: v2\n")]
    [InlineData("pinned: true\nmetadata:\n  github-pinned: v1\n  own: value\n")]
    [InlineData("pinned: true\nmetadata:\n  own: value\n")]
    [InlineData("custom: &v one\nmetadata:\n  github-pinned: v1\n  own: value\n")]
    public void UnsupportedOrIndependentPolicyCannotBeReportedUnpinned(string yaml)
    {
        var source = Encoding.UTF8.GetBytes("---\nname: demo\ndescription: Fixture\n" + yaml + "---\nBody\n");
        Assert.Throws<IOException>(() => PinTrackingWriter.Unpin(source));
    }

    [Fact]
    public void AlreadyUnpinnedIsExactAndMalformedUtf8Refuses()
    {
        var bytes = Encoding.UTF8.GetBytes("---\nname: demo\ndescription: Fixture\nmetadata:\n  own: value\n---\nBody\n");
        Assert.Equal(bytes, PinTrackingWriter.Unpin(bytes));
        Assert.Throws<IOException>(() => PinTrackingWriter.Unpin([.. bytes, 0xff]));
    }
}
