using SkillView.Logging;

namespace SkillView.Threading;

/// <summary>Creates consistent, bounded diagnostics for cancellation callback faults.</summary>
internal static class CancellationCallbackReporter
{
    internal static Action<AggregateException> For(
        Logger logger,
        string owner,
        string category = "cancellation")
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return exception => logger.Error(
            category,
            $"{owner} cancellation callback failed: {exception}");
    }
}
