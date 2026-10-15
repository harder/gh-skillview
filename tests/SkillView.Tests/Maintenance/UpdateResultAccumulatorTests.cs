using SkillView.Gh.Models;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class UpdateResultAccumulatorTests
{
    private static UpdateResult Result() => new()
    {
        DryRun = false,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "",
        StdErr = "",
        ErrorMessage = null,
        CommandLine = [],
        Entries = []
    };

    [Fact]
    public void LaterSuccessCannotEraseFailureOrEarlierMutation()
    {
        var queue = new UpdateResultAccumulator();
        queue.Append(Result() with { MutationAttempted = true, StdOut = "first changed" });
        queue.Append(Result() with { Succeeded = false, ExitCode = 7, StdErr = "second failed", ErrorMessage = "remote failure" });
        var result = queue.Append(Result() with { StdOut = "third current" });
        Assert.False(result.Succeeded);
        Assert.True(result.MutationAttempted);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("first changed\nthird current", result.StdOut);
        Assert.Equal("second failed", result.StdErr);
        Assert.Equal("remote failure", result.ErrorMessage);
    }

    [Fact]
    public void CancellationAndIncompleteReconciliationSurviveLaterSuccess()
    {
        var queue = new UpdateResultAccumulator();
        queue.Append(Result() with { ReconciliationPartial = true, MutationAttempted = true });
        queue.Append(Result() with { Cancelled = true, ExitCode = 130 });
        var result = queue.Append(Result());
        Assert.False(result.Succeeded);
        Assert.True(result.ReconciliationPartial);
        Assert.True(result.MutationAttempted);
        Assert.True(result.Cancelled);
        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public void ExcessOutputAndFieldsAreBoundedWithExplicitOmissionNotices()
    {
        var queue = new UpdateResultAccumulator();
        var large = new string('x', UpdateResultAccumulator.MaxStreamChars + 1);
        var notice = new UpdateNotice(large, large);
        var entry = new UpdateEntry { Name = large, Repo = large, FromVersion = large, ToVersion = large, Status = large };
        var result = queue.Append(Result() with
        {
            StdOut = "earliest stdout\n" + large,
            StdErr = "earliest stderr\n" + large,
            ErrorMessage = "earliest error\n" + large,
            Notices = [.. Enumerable.Repeat(notice, UpdateResultAccumulator.MaxNotices + 1)],
            Entries = [.. Enumerable.Repeat(entry, UpdateResultAccumulator.MaxEntries + 1)]
        });
        result = queue.Append(Result() with { StdOut = "late stdout", StdErr = "late stderr", ErrorMessage = "late error" });
        Assert.Equal(UpdateResultAccumulator.MaxStreamChars, result.StdOut.Length);
        Assert.Equal(UpdateResultAccumulator.MaxStreamChars, result.StdErr.Length);
        Assert.Equal(UpdateResultAccumulator.MaxErrorChars, result.ErrorMessage!.Length);
        Assert.StartsWith("earliest stdout", result.StdOut);
        Assert.StartsWith("earliest stderr", result.StdErr);
        Assert.StartsWith("earliest error", result.ErrorMessage);
        Assert.Equal(UpdateResultAccumulator.MaxNotices + 5, result.Notices.Length);
        Assert.Equal(UpdateResultAccumulator.MaxEntries, result.Entries.Length);
        Assert.All(result.Notices.Take(UpdateResultAccumulator.MaxNotices), value =>
        {
            Assert.Equal(UpdateResultAccumulator.MaxFieldChars, value.Stream.Length);
            Assert.Equal(UpdateResultAccumulator.MaxFieldChars, value.Text.Length);
        });
        Assert.All(result.Entries, value =>
        {
            foreach (var field in new[] { value.Name, value.Repo, value.FromVersion, value.ToVersion, value.Status })
                Assert.Equal(UpdateResultAccumulator.MaxFieldChars, field!.Length);
        });
        Assert.All(result.Notices.Skip(UpdateResultAccumulator.MaxNotices), value => Assert.Equal("capture", value.Stream));
    }
}
