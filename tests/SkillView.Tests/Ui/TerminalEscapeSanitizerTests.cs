using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
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
            Package = new SkillPackage(
                Source: "pkg\x1b]0;bad\x07",
                SourceType: "git",
                SourceUrl: "https://example.test/\x1b]0;bad\x07demo",
                InstalledAt: null,
                UpdatedAt: null),
        });

        Assert.DoesNotContain('\x1b', detail);
        Assert.DoesNotContain('\x07', detail);
        Assert.Contains("## Summary", detail);
        Assert.Contains("`pkg`", detail);
        Assert.DoesNotContain("bad", detail);
    }

    [Fact]
    public void RenderCleanupDetail_StripsEscapeSequences_FromCandidateMetadata()
    {
        var detail = CleanupScreen.RenderDetail(new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.Malformed,
            "/skills/demo\x1b]0;bad\x07",
            "reason\x1b]8;;https://example.test\x07text",
            Skill: null));

        Assert.DoesNotContain('\x1b', detail);
        Assert.DoesNotContain('\x07', detail);
        Assert.Contains("| Field | Value |", detail);
        Assert.Contains("reasontext", detail);
        Assert.DoesNotContain("bad", detail);
    }

    [Fact]
    public void BuildRemoveSummary_StripsEscapeSequences_AndUsesStructuredTables()
    {
        var validation = new RemoveValidator.RemoveValidation(
            Errors: ImmutableArray.Create(
                new RemoveValidator.Error(
                    RemoveValidator.ErrorKind.ContainsGitDirectory,
                    ".git\x1b]0;bad\x07 found")),
            Warnings: ImmutableArray.Create(
                new RemoveValidator.Warning(
                    RemoveValidator.WarningKind.HasIncomingSymlinks,
                    "2 other install(s)\x1b]8;;https://example.test\x07 symlink in")),
            ResolvedPath: "/skills/demo\x1b]0;bad\x07",
            IncomingSymlinkPaths: ImmutableArray.Create("/agents/demo\x1b]0;bad\x07"));

        var screen = new RemoveScreen(
            null!,
            new RemoveService(new Logger()),
            new Logger(),
            CreateSkill(),
            validation);

        var summary = screen.BuildSummary();

        Assert.DoesNotContain('\x1b', summary);
        Assert.DoesNotContain('\x07', summary);
        Assert.Contains("### Target", summary);
        Assert.Contains("| Field | Value |", summary);
        Assert.Contains("### Errors", summary);
        Assert.Contains("| Kind | Detail |", summary);
        Assert.Contains("### Warnings", summary);
        Assert.Contains("### Evidence", summary);
        Assert.DoesNotContain("bad", summary);
    }

    private static InstalledSkill CreateSkill() => new()
    {
        Name = "demo\x1b]0;bad\x07",
        ResolvedPath = "/skills/demo\x1b]0;bad\x07",
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = ImmutableArray.Create(
            new AgentMembership("claude", "/agents/demo\x1b]0;bad\x07", true)),
        FrontMatter = new SkillFrontMatter
        {
            Name = "demo",
        },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = true,
        InstalledAt = null,
    };
}
