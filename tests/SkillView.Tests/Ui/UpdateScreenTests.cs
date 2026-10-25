using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class UpdateScreenTests
{
    [Fact]
    public void TagReviewLeadsWithVersionConsumersLocalChangesAndRetentionBeforeDetailedEvidence()
    {
        var fixture = LocalReimportScreenTests.Plan();
        var longAgent = new string('x', 100);
        var metadata = SkillFrontMatter.Empty with { GithubRef = "refs/tags/v2" };
        var plan = fixture with
        {
            LocalImport = null,
            Subject = fixture.Subject with
            {
                Metadata = metadata,
                Agents = [.. fixture.Subject.Agents, new(longAgent, "/another/demo", true)]
            },
            Revision = new(new("https://github.com/test/repo", "refs/tags/v1", new('c', 40), new('d', 40), "skills/demo"), metadata, null, ["local.txt"])
        };
        var body = UpdateScreen.RenderPlan(plan);
        var state = body.IndexOf("**State:**", StringComparison.Ordinal);
        Assert.True(state > 0);
        var summary = body[..state];
        Assert.Contains("refs/tags/v2 -> refs/tags/v1", summary);
        Assert.Contains("Affected recorded agents", summary); Assert.Contains("claude, codex", summary);
        Assert.Contains("**Local changes:** `1`", summary);
        Assert.Contains("Apply retains original, including local edits, for undo", summary);
        Assert.Contains("later ordinary Update follows latest", summary);
        Assert.Contains("no pin is created", summary);
        Assert.DoesNotContain("One-time", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Summary shortened", summary); Assert.DoesNotContain(longAgent, summary);
        Assert.True(state < body.IndexOf("**Plan ID:**", StringComparison.Ordinal));
        Assert.True(state < body.IndexOf("### Frozen source changes", StringComparison.Ordinal));
        Assert.Contains(longAgent, body); Assert.Contains(new string('c', 40), body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOutputCompletionIsOnlyReportedForSuccessfulResult(bool succeeded)
    {
        var result = UpdateResult.Failure(false, 2, "", []) with
        { Succeeded = succeeded, ExitCode = succeeded ? 0 : 2, ErrorMessage = succeeded ? null : "Native transition refused" };
        var body = UpdateScreen.RenderResult(result, "demo", localReimport: true);
        Assert.Equal(succeeded, body.Contains("Completed with no output", StringComparison.Ordinal));
        if (!succeeded)
        {
            Assert.Contains("Failed or refused", body);
            Assert.Contains("Native transition refused", body);
        }
    }

    [Fact]
    public void RenderResult_FormatsSingleSkillScopeSafely()
    {
        var result = new UpdateResult
        {
            DryRun = false,
            Succeeded = true,
            ExitCode = 0,
            StdOut = "updated",
            StdErr = string.Empty,
            ErrorMessage = string.Empty,
            CommandLine = [],
            Entries = ImmutableArray<UpdateEntry>.Empty,
        };

        var body = UpdateScreen.RenderResult(
            result,
            skillName: "skill`name|beta\nstable");

        Assert.Contains("**Scope:** `` skill`name\\|beta stable ``", body);
    }

    [Fact]
    public void RenderResult_UsesSafeFenceWhenOutputContainsTripleBackticks()
    {
        var result = new UpdateResult
        {
            DryRun = false,
            Succeeded = true,
            ExitCode = 0,
            StdOut = """
                update summary
                ```
                raw block
                ```
                """,
            StdErr = string.Empty,
            ErrorMessage = string.Empty,
            CommandLine = [],
            Entries = ImmutableArray<UpdateEntry>.Empty,
        };

        var body = UpdateScreen.RenderResult(
            result,
            skillName: "demo");

        Assert.Contains("````", body);
        Assert.Contains("```", body);
    }
}
