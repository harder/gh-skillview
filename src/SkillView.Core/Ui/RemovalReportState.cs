using System.Collections.Immutable;
using SkillView.Inventory;

namespace SkillView.Ui;

/// <summary>
/// Keeps dialog-session mutation totals separate from the latest attempt's
/// outcome, and builds bounded synthetic reports when an operation exits by
/// cancellation or an unexpected exception.
/// </summary>
internal static class RemovalReportState
{
    internal static RemoveService.RemoveReport Accumulate(
        RemoveService.RemoveReport? previous,
        RemoveService.RemoveReport current) =>
        current with
        {
            FilesDeleted = Add(previous?.FilesDeleted ?? 0, current.FilesDeleted),
            DirectoriesDeleted = Add(
                previous?.DirectoriesDeleted ?? 0,
                current.DirectoriesDeleted),
        };

    internal static RemoveService.BatchRemoveReport Accumulate(
        RemoveService.BatchRemoveReport? previous,
        RemoveService.BatchRemoveReport current) =>
        current with
        {
            TargetsDeleted = Add(previous?.TargetsDeleted ?? 0, current.TargetsDeleted),
            TargetsSkipped = Add(previous?.TargetsSkipped ?? 0, current.TargetsSkipped),
            FilesDeleted = Add(previous?.FilesDeleted ?? 0, current.FilesDeleted),
            DirectoriesDeleted = Add(
                previous?.DirectoriesDeleted ?? 0,
                current.DirectoriesDeleted),
        };

    internal static RemoveService.RemoveReport Canceled(
        string resolvedPath,
        RemoveService.RemoveProgress? progress)
    {
        var errorCount = Math.Max(0, progress?.Errors ?? 0);
        return new RemoveService.RemoveReport(
            Succeeded: false,
            ResolvedPath: resolvedPath,
            FilesDeleted: Math.Max(0, progress?.FilesProcessed ?? 0),
            DirectoriesDeleted: Math.Max(0, progress?.DirectoriesProcessed ?? 0),
            Errors: UnavailableErrorDetails(errorCount),
            DryRun: false)
        {
            ErrorCount = errorCount,
            IsCanceled = true,
        };
    }

    internal static RemoveService.BatchRemoveReport Canceled(
        RemoveService.RemoveProgress? progress)
    {
        var errorCount = Math.Max(0, progress?.Errors ?? 0);
        return new RemoveService.BatchRemoveReport(
            Succeeded: false,
            TargetsDeleted: Math.Max(0, progress?.TargetsDeleted ?? 0),
            FilesDeleted: Math.Max(0, progress?.FilesProcessed ?? 0),
            DirectoriesDeleted: Math.Max(0, progress?.DirectoriesProcessed ?? 0),
            Errors: UnavailableErrorDetails(errorCount),
            DryRun: false)
        {
            ErrorCount = errorCount,
            IsCanceled = true,
        };
    }

    internal static RemoveService.RemoveReport Failed(
        string resolvedPath,
        RemoveService.RemoveProgress? progress,
        string detail)
    {
        var priorErrorCount = Math.Max(0, progress?.Errors ?? 0);
        return new RemoveService.RemoveReport(
            Succeeded: false,
            ResolvedPath: resolvedPath,
            FilesDeleted: Math.Max(0, progress?.FilesProcessed ?? 0),
            DirectoriesDeleted: Math.Max(0, progress?.DirectoriesProcessed ?? 0),
            Errors: FailureDetails(detail, priorErrorCount),
            DryRun: false)
        {
            ErrorCount = Add(priorErrorCount, 1),
        };
    }

    internal static RemoveService.BatchRemoveReport Failed(
        RemoveService.RemoveProgress? progress,
        string detail)
    {
        var priorErrorCount = Math.Max(0, progress?.Errors ?? 0);
        return new RemoveService.BatchRemoveReport(
            Succeeded: false,
            TargetsDeleted: Math.Max(0, progress?.TargetsDeleted ?? 0),
            FilesDeleted: Math.Max(0, progress?.FilesProcessed ?? 0),
            DirectoriesDeleted: Math.Max(0, progress?.DirectoriesProcessed ?? 0),
            Errors: FailureDetails(detail, priorErrorCount),
            DryRun: false)
        {
            ErrorCount = Add(priorErrorCount, 1),
        };
    }

    private static ImmutableArray<string> UnavailableErrorDetails(int errorCount) =>
        errorCount == 0
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(
                $"… {errorCount} runtime error detail(s) unavailable after cancellation; see logs");

    private static ImmutableArray<string> FailureDetails(string detail, int priorErrorCount) =>
        priorErrorCount == 0
            ? ImmutableArray.Create(detail)
            : ImmutableArray.Create(
                detail,
                $"… {priorErrorCount} earlier runtime error detail(s) unavailable; see logs");

    private static int Add(int left, int right) =>
        (int)Math.Min(int.MaxValue, (long)Math.Max(0, left) + Math.Max(0, right));
}
