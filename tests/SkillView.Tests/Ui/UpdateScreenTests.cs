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
}
