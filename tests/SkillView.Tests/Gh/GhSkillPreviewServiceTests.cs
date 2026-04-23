using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillPreviewServiceTests
{
    [Fact]
    public void BuildArgs_RepoOnly()
    {
        var args = GhSkillPreviewService.BuildArgs("vercel-labs/skills", skillName: null, version: null);
        Assert.Equal(new[] { "skill", "preview", "vercel-labs/skills" }, args);
    }

    [Fact]
    public void BuildArgs_RepoAndSkill()
    {
        var args = GhSkillPreviewService.BuildArgs("vercel-labs/skills", "render-md", version: null);
        Assert.Equal(new[] { "skill", "preview", "vercel-labs/skills", "render-md" }, args);
    }

    [Fact]
    public void BuildArgs_VersionIsConcatenatedWithAt()
    {
        var args = GhSkillPreviewService.BuildArgs("owner/repo", "skill", version: "v2.0.0");
        Assert.Equal(new[] { "skill", "preview", "owner/repo@v2.0.0", "skill" }, args);
    }

    [Fact]
    public void Split_NoFilesSection_ReturnsFullBody()
    {
        var body = """
            # render-md

            Renders markdown content.

            ## Usage
            Just call it.
            """;
        var (md, files) = GhSkillPreviewService.Split(body);
        Assert.Empty(files);
        Assert.Equal(body, md);
    }

    [Fact]
    public void Split_CapturesBulletedAssociatedFiles()
    {
        var body = """
            # render-md

            The skill body.

            ## Associated files
            - scripts/render.sh
            - templates/base.md
            """;
        var (md, files) = GhSkillPreviewService.Split(body);
        Assert.Contains("scripts/render.sh", files);
        Assert.Contains("templates/base.md", files);
        Assert.Contains("skill body", md);
        Assert.DoesNotContain("Associated files", md);
    }

    [Fact]
    public void Split_AcceptsHeadingSynonyms()
    {
        var body = "# body\n\n## Bundled files\n- file-a.txt\n- file-b.txt\n";
        var (_, files) = GhSkillPreviewService.Split(body);
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Split_SkipsProseLinesUnderHeading()
    {
        var body = "## Files\nThese are the files for this skill:\n- a.md\n";
        var (_, files) = GhSkillPreviewService.Split(body);
        Assert.Single(files);
        Assert.Equal("a.md", files[0]);
    }
}
