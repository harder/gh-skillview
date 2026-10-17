using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private ReplacementPlan FreezeSourceReview(ReplacementPlan plan, NativeStateDirectory operation,
        NativeDirectoryMoveLease original, NativeDirectoryMoveLease candidate, CancellationToken token)
    {
        if ((!sourceReviewRequired && !RequiresSourceReview(plan)) || plan.PinPolicy is not null) return plan;
        var suffix = plan.LocalDisposition?.MergedSlot ?? "candidate";
        var oldName = "review-installed-" + suffix; var newName = "review-source-" + suffix;
        CapturedPayloadArchive.Capture(operation, oldName, original, token);
        CapturedPayloadArchive.Capture(operation, newName, candidate, token);
        return ReviewFromArchives(plan, operation, oldName, newName, token);
    }

    private ReplacementPlan ReviewFromArchives(ReplacementPlan plan, NativeStateDirectory operation,
        string oldName, string newName, CancellationToken token)
    {
        using var oldState = OpenSourceReviewArchive(plan, operation, oldName, token);
        using var newState = OpenSourceReviewArchive(plan, operation, newName, token);
        var before = CapturedPayloadArchive.Load(oldState, null, token);
        var after = CapturedPayloadArchive.Load(newState, null, token);
        // Historical replacement evidence deliberately excludes Unix link
        // modes. Bound archives retain them. Adapt only the comparison domain;
        // preserve the complete raw archive, manifest hash and report below.
        if (LegacyProjection(before.Archive.Snapshot, token).Fingerprint != plan.Original.Payload
            || LegacyProjection(after.Archive.Snapshot, token).Fingerprint != plan.Candidate.Payload)
            throw new IOException("Review archives differ from the saved payloads.");
        var report = SourceUpdateReview.Analyze(before.Archive.Snapshot, before.Archive.OpenReader(oldState),
            after.Archive.Snapshot, after.Archive.OpenReader(newState), token);
        return plan with
        {
            SourceReview = new(1, oldName, newName, before.Fingerprint, after.Fingerprint,
            SourceUpdateReview.Hash(report), report)
        };
    }

    private static NativeStateDirectory OpenSourceReviewArchive(ReplacementPlan plan, NativeStateDirectory operation,
        string name, CancellationToken token) => RequiresSourceReview(plan)
            ? operation.OpenExistingDirectory(name, token) : OpenArchive(operation, name, token);

    private const int MaxSourceReviewEntries = 3 * (MaxMergeAttempts + 1);
    private static bool IsSourceReviewEntry(string name) =>
        name.StartsWith("source-review-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
            && QuarantineStore.ValidHash(name[14..^5])
        || new[] { "review-installed-", "review-source-" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)
            && (name[prefix.Length..] == "candidate" || IsMergeSlot(name[prefix.Length..])));

    // A failed current merge may leave private capture files before its new
    // contract is published. Admit only bounded, native directories belonging
    // to real local-review/merge history; these bytes confer no review authority.
    private static HashSet<string> ValidateLegacyReviewAttemptArchives(ReplacementPlan plan,
        NativeStateDirectory operation, HashSet<string> names, CancellationToken token)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        if (plan.SchemaVersion != 2 || plan.SourceReview is not null || !names.Contains("local-review")) return admitted;
        foreach (var name in names)
        {
            var prefix = name.StartsWith("review-installed-", StringComparison.Ordinal) ? "review-installed-"
                : name.StartsWith("review-source-", StringComparison.Ordinal) ? "review-source-" : null;
            if (prefix is null || !IsMergeSlot(name[prefix.Length..]) || !names.Contains(name[prefix.Length..])) continue;
            admitted.Add(name);
        }
        if (admitted.Count == 0) return admitted;
        using (var draft = OpenArchive(operation, "local-review", token)) _ = LoadLocalReview(plan, draft, token);
        foreach (var name in admitted)
        {
            using var archive = OpenArchive(operation, name, token);
            var leaves = archive.EnumerateEntries(token).Take(PayloadSnapshot.MaxEntries + 2).ToArray();
            if (leaves.Length > PayloadSnapshot.MaxEntries + 1 || leaves.Any(leaf => leaf.Error is not null
                || leaf.Bytes < 0 || (leaf.Name == "manifest.json" ? leaf.Bytes > CapturedPayloadArchive.MaxManifestBytes
                    : leaf.Name.Length != 11 || !leaf.Name.StartsWith("entry-", StringComparison.Ordinal)
                        || leaf.Name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') >= 0 || leaf.Bytes > PayloadSnapshot.MaxBytes))
                || leaves.Where(leaf => leaf.Name != "manifest.json").Sum(leaf => leaf.Bytes) > PayloadSnapshot.MaxBytes)
                throw new IOException("Unsafe or excessive retained source-review attempt archive.");
        }
        return admitted;
    }

    private static string ReceiptName(ReplacementPlan plan) => "source-review-" + plan.ReviewFingerprint + ".json";
    private static SourceReviewReceipt ExpectedReceipt(ReplacementPlan plan)
    {
        var review = plan.SourceReview ?? throw new IOException("Legacy plan has no frozen source-review evidence; create a new product plan. No review acknowledgement was invented.");
        return new(1, plan.Id, plan.ReviewFingerprint, review.Fingerprint, review.InstalledManifest,
            review.CandidateManifest, plan.Original.Payload, plan.Candidate.Payload, plan.ContextFingerprint, SourceUpdateReview.Declaration);
    }
    private static bool RequiresSourceReview(ReplacementPlan plan) => plan.SchemaVersion is SelectedRevisionSchema or ApprovedRestoreSchema;

    private static void RequireSourceReceipt(Session session, CancellationToken token)
    {
        if (session.Plan.SourceReview is null)
        {
            if (RequiresSourceReview(session.Plan)) throw new IOException("Schema7/8 requires frozen independent source-review evidence; no receipt-less producer exists.");
            return; // Explicit historical schemas1–6 contract, never a reviewed state.
        }
        SourceReviewReceipt actual;
        try { actual = Read(session.Operation, ReceiptName(session.Plan), SourceReviewJsonContext.Default.SourceReviewReceipt, token); }
        catch (IOException ex) { throw new IOException("Explicit source-review acknowledgement is required for this exact saved plan. Inspect its frozen diff and acknowledge its source-review fingerprint separately from retention approval.", ex); }
        if (actual != ExpectedReceipt(session.Plan)) throw new IOException("Source-review receipt does not match the exact saved plan, payloads, manifests and context.");
    }
    internal ReplacementResult AcknowledgeSourceReview(string id, string expectedPlan, string expectedReview,
        InventorySnapshot inventory, CancellationToken token, string? expectedPurpose = null)
    {
        using var session = Open(id, inventory, token);
        var plan = session.Plan; RequirePurpose(plan, expectedPurpose);
        if (plan.ReviewFingerprint != expectedPlan || plan.SourceReview?.Fingerprint != expectedReview)
            throw new IOException("Source acknowledgement needs the exact saved plan and source-review fingerprints.");
        ValidateSourceReview(plan, session.Operation, token, verifyBytes: true);
        var name = ReceiptName(plan);
        if (session.Operation.EnumerateEntries(token).Any(entry => entry.Name == name)) RequireSourceReceipt(session, token);
        else Write(session.Operation, name, ExpectedReceipt(plan), SourceReviewJsonContext.Default.SourceReviewReceipt, token);
        return Result(plan, "source-reviewed", true, false, false, plan.ReviewFingerprint,
            SourceUpdateReview.Declaration + " Retention approval is still required before apply.") with
        { SourceReviewAcknowledged = true };
    }
    private static void ValidateSourceReview(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token, bool verifyBytes = false)
    {
        if (plan.SourceReview is not { } review)
        {
            if (RequiresSourceReview(plan)) throw new IOException("Schema7/8 requires its actual independent source-review contract.");
            return;
        }
        var suffix = plan.LocalDisposition?.MergedSlot ?? "candidate";
        if (review.Version != 1 || plan.PinPolicy is not null
            || review.InstalledArchive != "review-installed-" + suffix || review.CandidateArchive != "review-source-" + suffix
            || !QuarantineStore.ValidHash(review.InstalledManifest) || !QuarantineStore.ValidHash(review.CandidateManifest)
            || !QuarantineStore.ValidHash(review.Fingerprint) || review.Report is null || SourceUpdateReview.Hash(review.Report) != review.Fingerprint)
            throw new IOException("Source-review contract or report fingerprint is invalid.");
        using var oldState = OpenSourceReviewArchive(plan, operation, review.InstalledArchive, token);
        using var newState = OpenSourceReviewArchive(plan, operation, review.CandidateArchive, token);
        var before = CapturedPayloadArchive.Load(oldState, review.InstalledManifest, token).Archive;
        var after = CapturedPayloadArchive.Load(newState, review.CandidateManifest, token).Archive;
        if (LegacyProjection(before.Snapshot, token).Fingerprint != plan.Original.Payload
            || LegacyProjection(after.Snapshot, token).Fingerprint != plan.Candidate.Payload)
            throw new IOException("Source-review manifests do not match the saved payloads.");
        if (!verifyBytes) return;
        foreach (var item in before.Entries.Where(e => e.Blob is not null)) before.CopyEntry(oldState, item.Entry.Path, Stream.Null, token);
        foreach (var item in after.Entries.Where(e => e.Blob is not null)) after.CopyEntry(newState, item.Entry.Path, Stream.Null, token);
        var report = SourceUpdateReview.Analyze(before.Snapshot, before.OpenReader(oldState), after.Snapshot, after.OpenReader(newState), token);
        if (SourceUpdateReview.Hash(report) != review.Fingerprint) throw new IOException("Frozen source review differs from the exact archived bytes.");
    }
}
