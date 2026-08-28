using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class RemovalReportStateTests
{
    [Fact]
    public void AccumulateSingle_PreservesEarlierMutationsAcrossRetry()
    {
        var previous = Single(
            succeeded: false,
            files: 3,
            directories: 1,
            errors: ["first failure"],
            errorCount: 1);
        var current = Single(
            succeeded: false,
            files: 0,
            directories: 0,
            errors: ["retry failure"],
            errorCount: 1);

        var accumulated = RemovalReportState.Accumulate(previous, current);

        Assert.False(accumulated.Succeeded);
        Assert.Equal(3, accumulated.FilesDeleted);
        Assert.Equal(1, accumulated.DirectoriesDeleted);
        Assert.Equal(1, accumulated.ErrorCount);
        Assert.Equal(["retry failure"], accumulated.Errors);
    }

    [Fact]
    public void AccumulateBatch_PreservesCompactMutationsAcrossWizardResult()
    {
        var compact = Batch(
            succeeded: false,
            targets: 0,
            files: 2,
            directories: 0,
            errors: ["compact failure"],
            errorCount: 1);
        var wizard = Batch(
            succeeded: false,
            targets: 0,
            files: 0,
            directories: 0,
            errors: ["wizard failure"],
            errorCount: 1);

        var accumulated = RemovalReportState.Accumulate(compact, wizard);

        Assert.False(accumulated.Succeeded);
        Assert.Equal(0, accumulated.TargetsDeleted);
        Assert.Equal(2, accumulated.FilesDeleted);
        Assert.Equal(0, accumulated.DirectoriesDeleted);
        Assert.Equal(["wizard failure"], accumulated.Errors);
    }

    [Fact]
    public void Accumulate_UsesLatestOutcomeWhileRetainingMutationTotals()
    {
        var previous = Single(
            succeeded: false,
            files: 2,
            directories: 0,
            errors: ["transient failure"],
            errorCount: 1);
        var current = Single(
            succeeded: true,
            files: 1,
            directories: 1,
            errors: [],
            errorCount: 0);

        var accumulated = RemovalReportState.Accumulate(previous, current);

        Assert.True(accumulated.Succeeded);
        Assert.Equal(3, accumulated.FilesDeleted);
        Assert.Equal(1, accumulated.DirectoriesDeleted);
        Assert.Equal(0, accumulated.ErrorCount);
        Assert.Empty(accumulated.Errors);
    }

    [Fact]
    public void CanceledSingle_PreservesExactObservedRuntimeErrorCount()
    {
        var progress = Progress(
            targetsProcessed: 1,
            targetsDeleted: 0,
            files: 4,
            directories: 1,
            errors: 7);

        var report = RemovalReportState.Canceled("/skills/demo", progress);

        Assert.True(report.IsCanceled);
        Assert.False(report.Succeeded);
        Assert.Equal(4, report.FilesDeleted);
        Assert.Equal(1, report.DirectoriesDeleted);
        Assert.Equal(7, report.ErrorCount);
        Assert.Single(report.Errors);
        Assert.Contains("7 runtime error detail(s)", report.Errors[0]);
    }

    [Fact]
    public void CanceledBatch_UsesDeletedRatherThanProcessedTargets()
    {
        var progress = Progress(
            targetsProcessed: 3,
            targetsDeleted: 1,
            files: 4,
            directories: 1,
            errors: 2);

        var report = RemovalReportState.Canceled(progress);

        Assert.True(report.IsCanceled);
        Assert.Equal(1, report.TargetsDeleted);
        Assert.Equal(4, report.FilesDeleted);
        Assert.Equal(1, report.DirectoriesDeleted);
        Assert.Equal(2, report.ErrorCount);
        Assert.Contains("2 runtime error detail(s)", report.Errors[0]);
    }

    private static RemoveService.RemoveReport Single(
        bool succeeded,
        int files,
        int directories,
        ImmutableArray<string> errors,
        int errorCount) => new(
            Succeeded: succeeded,
            ResolvedPath: "/skills/demo",
            FilesDeleted: files,
            DirectoriesDeleted: directories,
            Errors: errors,
            DryRun: false)
        {
            ErrorCount = errorCount,
        };

    private static RemoveService.BatchRemoveReport Batch(
        bool succeeded,
        int targets,
        int files,
        int directories,
        ImmutableArray<string> errors,
        int errorCount) => new(
            Succeeded: succeeded,
            TargetsDeleted: targets,
            FilesDeleted: files,
            DirectoriesDeleted: directories,
            Errors: errors,
            DryRun: false)
        {
            ErrorCount = errorCount,
        };

    private static RemoveService.RemoveProgress Progress(
        int targetsProcessed,
        int targetsDeleted,
        int files,
        int directories,
        int errors) => new(
            TargetsProcessed: targetsProcessed,
            TargetsDeleted: targetsDeleted,
            FilesProcessed: files,
            DirectoriesProcessed: directories,
            Errors: errors,
            CurrentPath: "/skills/demo",
            IsCompleted: false,
            IsCanceled: true);
}
