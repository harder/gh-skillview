using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class UpdateScreenTests
{
    [Fact]
    public void RenderResult_FormatsSingleSkillScopeSafely()
    {
        var result = new UpdateResult
        {
            DryRun = true,
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
            dryRun: true,
            allChecked: false,
            skills: ["skill`name|beta\nstable"]);

        Assert.Contains("**Scope:** `` skill`name\\|beta stable ``", body);
    }

    [Fact]
    public void RenderResult_RendersParsedDryRunEntriesAsMarkdownTable()
    {
        var result = new UpdateResult
        {
            DryRun = true,
            Succeeded = true,
            ExitCode = 0,
            StdOut = """
                demo: up-to-date
                skill`name|beta v1.0.0 -> v1.1.0
                """,
            StdErr = string.Empty,
            ErrorMessage = string.Empty,
            CommandLine = [],
            Entries =
            [
                new UpdateEntry
                {
                    Name = "demo",
                    Status = "up-to-date",
                },
                new UpdateEntry
                {
                    Name = "skill`name|beta",
                    Status = "updated",
                    FromVersion = "v1.0.0",
                    ToVersion = "v1.1.0",
                },
            ],
        };

        var body = UpdateScreen.RenderResult(
            result,
            dryRun: true,
            allChecked: false,
            skills: ["demo", "skill`name|beta"]);

        Assert.Contains("| Skill | Status | Change |", body);
        Assert.Contains("| `demo` | up-to-date | — |", body);
        Assert.Contains("| `` skill`name\\|beta `` | updated | `v1.0.0 -> v1.1.0` |", body);
    }

    [Fact]
    public void RenderResult_UsesSafeFenceWhenOutputContainsTripleBackticks()
    {
        var result = new UpdateResult
        {
            DryRun = true,
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
            dryRun: true,
            allChecked: false,
            skills: ["demo"]);

        Assert.Contains("````", body);
        Assert.Contains("```", body);
    }
}
