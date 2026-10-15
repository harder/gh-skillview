using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class RecordedTrackingWriterTests
{
    [Theory]
    [InlineData("name: demo\ndescription: Fixture\n")]
    [InlineData("name: demo\ndescription: Fixture\nmetadata:\n  author: 'Keep this' # authored\n")]
    [InlineData("name: demo\ndescription: Fixture\nmetadata: # authored header\n    version: '1.0'\n    owner: me\n")]
    [InlineData("name: demo\ndescription: |\n  Long\n  description\nmetadata:\n  github-repo: stale\n  github-ref: stale\n  author: me\n")]
    public void RebindingPreservesAllAuthoredBytesAndPinPolicy(string yaml)
    {
        var source = "\uFEFF---\n" + yaml + "---\nBody\n  preserve whitespace \n";
        var (_, installed, _) = FrontMatterParser.Parse("---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  github-path: skills/demo/SKILL.md\n  github-pinned: release-1\n---\n");
        var bound = RecordedTrackingWriter.Bind(source, installed);
        Assert.StartsWith("\uFEFF---", bound);
        Assert.Equal(InstallerMetadataNormalizer.Normalize(source), InstallerMetadataNormalizer.Normalize(bound));
        var (_, parsed, valid) = FrontMatterParser.Parse(bound); Assert.True(valid);
        Assert.Equal(installed.Upstream, parsed.Upstream); Assert.Equal(installed.GithubRef, parsed.GithubRef);
        Assert.Equal(installed.GithubPath, parsed.GithubPath); Assert.Equal(installed.PinnedRef, parsed.PinnedRef);
        Assert.Equal(installed.GithubTreeSha, parsed.GithubTreeSha);
        Assert.DoesNotContain("local-path", bound);
    }

    [Fact]
    public void FlowMetadataIsRefusedInsteadOfRewritingAuthoredMapping()
    {
        var (_, installed, _) = FrontMatterParser.Parse("---\nname: demo\ndescription: Fixture\n---\n");
        Assert.ThrowsAny<Exception>(() => RecordedTrackingWriter.Bind("---\nname: demo\ndescription: Fixture\nmetadata: {author: me}\n---\nBody\n", installed));
    }
    [Fact]
    public void MalformedRecordedYamlBecomesStructuredIoRefusal()
    {
        var (_, installed, _) = FrontMatterParser.Parse("---\nname: demo\ndescription: Fixture\n---\n");
        Assert.Throws<IOException>(() => RecordedTrackingWriter.Bind("---\nname: [unterminated\n---\nBody\n", installed));
    }

}
