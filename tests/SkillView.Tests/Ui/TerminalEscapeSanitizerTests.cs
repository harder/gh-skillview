using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class TerminalEscapeSanitizerTests
{
    [Fact]
    public void Sanitize_RemovesDangerousTerminalBytes()
    {
        var input = "safe\x1b]8;;https://example.test\x07link\x1b[31mtext";

        var sanitized = TerminalEscapeSanitizer.Sanitize(input);

        Assert.Equal("safelinktext", sanitized);
    }

    [Fact]
    public void SanitizeRenderedOutput_PreservesSgr_AndStripsOsc()
    {
        var rendered = "\x1b[31mred\x1b[0m \x1b]0;title\x07";

        var sanitized = TerminalEscapeSanitizer.SanitizeRenderedOutput(rendered);

        Assert.Equal("\x1b[31mred\x1b[0m ", sanitized);
    }

    [Fact]
    public void RenderSearchMetadata_StripsEscapeSequences_FromRemoteDescription()
    {
        var metadata = SkillViewApp.RenderSearchMetadata(
            new SearchResultSkill(
                Description: "hello\x1b]8;;https://example.test\x07world",
                Namespace: "ns",
                Path: "skills/demo",
                Repo: "owner/repo",
                SkillName: "demo",
                Stars: 5),
            GhAuthStatus.Unknown);

        Assert.DoesNotContain('\x1b', metadata);
        Assert.DoesNotContain('\x07', metadata);
        Assert.Contains("helloworld", metadata);
    }

    [Fact]
    public void RenderInstalledDetail_StripsEscapeSequences_FromFrontMatter()
    {
        var detail = InstalledScreen.RenderDetail(new InstalledSkill
        {
            Name = "demo\x1b]0;bad\x07",
            ResolvedPath = "/skills/demo\x1b]0;bad\x07",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = SkillFrontMatter.Empty with
            {
                Description = "desc\x1b]8;;https://example.test\x07text",
            },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        });

        Assert.DoesNotContain('\x1b', detail);
        Assert.DoesNotContain('\x07', detail);
        Assert.Contains("desctext", detail);
    }
}
