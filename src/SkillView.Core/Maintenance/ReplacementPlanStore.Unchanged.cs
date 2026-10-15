using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private string? RemoveUnchangedStaging(string path, OwnedDirectoryLease ownership, NativeStateDirectory parent)
    {
        try
        {
            // Cancellation of the caller cannot strand an already verified
            // no-op stage. Only the pinned private operation can be removed.
            using var deadline = new CancellationSource(TimeSpan.FromSeconds(3),
                CancellationCallbackReporter.For(logger, "unchanged update staging cleanup"));
            using (ownership)
            {
                checkpoint?.Invoke("unchanged-cleanup");
                var snapshot = ownership.Capture(deadline.Token);
                var removed = false;
                var failures = 0;
                SecureRemovalBackend.RemoveTree(path, snapshot.Identity, false, PayloadSnapshot.MaxDepth + 2,
                    _ => { }, (_, _) => { },
                    (deleted, directory) => { if (directory && PathIdentity.Equals(deleted, path)) removed = true; },
                    (_, error) => { if (++failures <= 8) logger.Warn("update.staging", error); }, deadline.Token);
                if (!removed || failures != 0) throw new IOException($"Private staging cleanup was not complete ({failures} errors).");
            }
            // Closing the duplicated ownership handle precedes publication:
            // Windows' legacy deletion fallback can finish at the last close.
            parent.FlushPublication(deadline.Token);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException)
        {
            var warning = $"Installed payload is unchanged. Cleanup/publication of private staging was not verified at {path}: {ex.Message}";
            logger.Warn("update.staging", warning);
            return warning;
        }
    }
}
