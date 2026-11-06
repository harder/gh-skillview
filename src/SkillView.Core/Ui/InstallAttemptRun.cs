using SkillView.Gh.Models;
using SkillView.Inventory.Models;

namespace SkillView.Ui;

internal enum InstallRefreshState { NotNeeded, NotRequested, Refreshing, Completed, Partial, Failed, WriterUnverified }

internal sealed record InstallAttemptReceipt(InstallAttemptSummary Summary, InstallRefreshState Refresh,
    InventorySnapshot? Snapshot = null, string? Error = null, long CatalogGeneration = -1)
{
    internal string Guidance => !Summary.MayHaveChangedFiles ? "No install started."
        : Refresh switch
        {
            InstallRefreshState.NotRequested => "The install may have changed files. Refresh Installed and review the result before retrying.",
            InstallRefreshState.Refreshing => "The install may have changed files. Installed is refreshing; review the result before retrying.",
            InstallRefreshState.Completed => "The install may have changed files. Installed refreshed; review the result before retrying.",
            InstallRefreshState.Partial => "The install may have changed files. Installed refresh is incomplete; unknown files remain protected. Refresh explicitly before retrying.",
            InstallRefreshState.WriterUnverified => "The install may have changed files. Child completion is unverified; refresh Installed after it stops before retrying.",
            _ => "The install may have changed files. Installed refresh failed; refresh explicitly before retrying.",
        };
}

// Called inside the modal's drained worker, before any cancelable UI delivery.
// Receipts survive Cancel, escalation and disposed modal controls.
internal sealed class InstallAttemptRun(Func<InstallAttemptSummary, Task<InstallAttemptReceipt>>? reconcile = null)
{
    private readonly List<InstallAttemptReceipt> _receipts = [];
    internal IReadOnlyList<InstallAttemptReceipt> Receipts => _receipts;
    internal InstallAttemptReceipt? Last => _receipts.LastOrDefault();

    internal async Task<InstallAttemptReceipt> CompleteAsync(InstallAttemptSummary summary,
        Action<InstallAttemptReceipt>? refreshing = null)
    {
        var index = _receipts.Count;
        var receipt = new InstallAttemptReceipt(summary, !summary.MayHaveChangedFiles ? InstallRefreshState.NotNeeded
            : summary.Results.Any(result => result.MayHaveChangedFiles && !result.ChildDrained)
                ? InstallRefreshState.WriterUnverified : InstallRefreshState.Refreshing);
        _receipts.Add(receipt);
        try { refreshing?.Invoke(receipt); }
        catch (Exception ex) { receipt = receipt with { Error = ex.Message }; }
        try
        {
            receipt = reconcile is not null ? await reconcile(summary).ConfigureAwait(false)
                : receipt with
                {
                    Refresh = receipt.Refresh == InstallRefreshState.Refreshing
                    ? InstallRefreshState.NotRequested : receipt.Refresh
                };
        }
        catch (Exception ex) { receipt = receipt with { Refresh = InstallRefreshState.Failed, Error = ex.Message }; }
        _receipts[index] = receipt;
        return receipt;
    }
}
