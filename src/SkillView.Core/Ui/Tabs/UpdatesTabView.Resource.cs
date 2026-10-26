using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Maintenance;
using SkillView.Threading;

namespace SkillView.Ui.Tabs;

internal sealed partial class UpdatesTabView
{
    private readonly Func<PackageCheckFact, string, string, GhSkillUpdateService.Options, CancellationToken, Task<UpdateResult>>? _resourcePrepareRunner;
    private readonly Func<PackageCheckFact, string, ReplacementResult, CancellationToken, Task<string>>? _resourceCheckRunner;
    private sealed class ResourceIntent(PackageCheckFact fact, string target)
    {
        internal PackageCheckFact Fact { get; } = fact;
        internal string Target { get; } = target;
        internal bool Started { get; set; }
        internal string? Tag { get; set; }
        internal ReplacementResult? Plan { get; set; }
    }
    private ResourceIntent? _resourceIntent;
    private sealed record ResourceNote(string Id, string Fingerprint, string Text, bool Verifying = false);
    private ResourceNote? _resourceNote;
    private readonly CancellationOperationSlot _resourceChecks;
    private long _resourceVerificationEpoch;

    private void ClearResourceNote()
    {
        Interlocked.Increment(ref _resourceVerificationEpoch);
        _resourceChecks.Cancel();
        var previous = _resourceNote is not null ? _undoResult : null;
        var verifying = _resourceNote?.Verifying == true;
        _resourceNote = null;
        if (previous is not null) ShowResultPreview(previous, false);
        if (verifying) _status.Text = " replacement installed · Undo is available";
    }

    private Task PrepareSelectedAsync(string ghPath, GhSkillUpdateService.Options options)
    {
        var intent = _resourceIntent;
        // A second preparation cannot reuse the first activation's intention.
        if (intent is { Started: true } || options.Unpin || _resourcePrepareRunner is null)
            _resourceIntent = intent = null;
        if (intent is not null)
        {
            intent.Started = true;
            intent.Tag = options.Tag; // freeze revision policy before staging
        }
        return ExecuteReviewOperationAsync(token => intent is null
            ? _updateRunner(ghPath, options, token)
            : _resourcePrepareRunner!(intent.Fact, intent.Target, ghPath, options, token), preparingResource: intent);
    }

    private void BindResourceReview(ResourceIntent? intent, ReplacementResult? plan)
    {
        if (intent is null || !ReferenceEquals(intent, _resourceIntent)) return;
        if (intent.Plan is not null || plan is null || !PackageResourceReplacement.Ready(intent.Fact, intent.Target, plan)
            || intent.Tag is not null && plan.Revision?.Source.Ref != "refs/tags/" + intent.Tag)
        {
            _resourceIntent = null; // keep ordinary saved review and Skip
            return;
        }
        intent.Plan = plan; // bind ID/fingerprint once; never reconstruct from saved fields
    }

    private void StartResourceVerification(ResourceIntent? intent, UpdateResult result, string? revisionTag)
    {
        if (intent?.Plan is not { } reviewed || !ReferenceEquals(intent, _resourceIntent)
            || intent.Tag != revisionTag || result.Cancelled
            || result.SavedPlan is not { Succeeded: true, Canceled: false, Status: "installed", MutationObserved: true } applied
            || applied.Id != reviewed.Id || applied.PlanFingerprint != reviewed.PlanFingerprint || !result.MutationAttempted
            || _undoResult?.SavedPlan is not { } remembered || remembered.Id != applied.Id
            || remembered.PlanFingerprint != applied.PlanFingerprint) return;
        var epoch = Interlocked.Increment(ref _resourceVerificationEpoch);
        _resourceNote = new(applied.Id, applied.PlanFingerprint, "Verifying package… Undo is available.", Verifying: true);
        ShowResultPreview(_undoResult, false);
        _status.Text = " replacement installed · verifying package… · Undo is available";
        // The native result and RememberUndo have already been published and
        // the review operation released. This separately tracked follow-up
        // neither holds that operation nor blocks Undo.
        _runTask(() => VerifyResourceAsync(intent, applied, reviewed, epoch), "updates.verify-package");
    }

    private bool ResourceVerificationIsCurrent(long epoch, ReplacementResult applied) =>
        epoch == Interlocked.Read(ref _resourceVerificationEpoch) && Visible && !_lifetimeToken.IsCancellationRequested
        && _review is null && _undoReview is null && !_operationCancellations.HasActive && !_loadCancellations.HasActive
        && _undoResult?.SavedPlan is { } remembered && remembered.Id == applied.Id && remembered.PlanFingerprint == applied.PlanFingerprint;

    private async Task VerifyResourceAsync(ResourceIntent intent, ReplacementResult applied, ReplacementResult reviewed, long epoch)
    {
        if (!ResourceVerificationIsCurrent(epoch, applied)) return;
        using var ownership = _resourceChecks.TryBegin(_lifetimeToken);
        if (ownership is null) return;
        var token = ownership.Token;
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(30),
            CancellationCallbackReporter.For(_logger, "replacement package check"));
        var text = PackageResourceReplacement.Incomplete;
        try
        {
            if (_resourceCheckRunner is not null && applied.Comparison?.CandidatePayload == reviewed.Comparison?.CandidatePayload)
                text = await _resourceCheckRunner(intent.Fact, intent.Target, applied, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return; }
        catch (Exception ex)
        {
            // Package verification cannot turn an actual successful native apply
            // into an apply failure or discard its retained original/Undo.
            _logger.Warn("update.package-check", ex.ToString());
        }
        if (token.IsCancellationRequested) return;
        if (deadline.Token.IsCancellationRequested) text = PackageResourceReplacement.Incomplete;
        try
        {
            await _runOnUi(() =>
            {
                // The epoch makes every clear boundary win, including Undo
                // inspection followed by Cancel undo on this same saved plan.
                if (token.IsCancellationRequested || !ResourceVerificationIsCurrent(epoch, applied)) return;
                _resourceNote = new(applied.Id, applied.PlanFingerprint, text);
                ShowResultPreview(_undoResult!, false);
                _status.Text = " replacement installed · package check finished · Undo is available";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        { _logger.Debug("update.package-check", "package verification delivery canceled during teardown"); }
    }

    private string ResourcePresentation(UpdateResult result)
    {
        if (result.SavedPlan is not { } plan) return "";
        if (_resourceNote is { } note && note.Id == plan.Id && note.Fingerprint == plan.PlanFingerprint)
            return "\n\n" + note.Text;
        if (_resourceIntent?.Plan is { } reviewed && plan.Id == reviewed.Id && plan.PlanFingerprint == reviewed.PlanFingerprint)
            return "\n\nReview replacement for " + MarkdownTableFormatter.FormatCodeSpan(_resourceIntent.Target)
                + ". The saved full comparison shows this resource absent in Installed and a regular file in Candidate. " + PackageResourceReplacement.Disclosure;
        return "";
    }
}
