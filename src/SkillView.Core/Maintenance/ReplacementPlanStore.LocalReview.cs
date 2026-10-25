using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private const int MaxLocalSelections = 16;
    private const int MaxMergeAttempts = 8;
    private static string CandidateName(ReplacementPlan plan) => plan.LocalDisposition?.MergedSlot ?? "candidate";
    private static string PlanName(ReplacementPlan plan) => plan.LocalDisposition is { } local
        ? "merge-plan-" + local.MergedSlot[7..] + ".json" : "plan.json";
    private static bool IsMergeSlot(string name) => Enumerable.Range(0, MaxMergeAttempts).Any(i => name == $"merged-{i:D2}");
    private static bool IsMergePlan(string name) => Enumerable.Range(0, MaxMergeAttempts).Any(i => name == $"merge-plan-{i:D2}.json");

    internal ReplacementResult PreserveInstalled(string id, string expectedPlan, InventorySnapshot inventory, CancellationToken token)
    {
        using var session = Open(id, inventory, token);
        var plan = session.Plan;
        RequirePurpose(plan, null);
        if (expectedPlan != plan.ReviewFingerprint || session.StepCount != 0 || session.IncompleteJournal || session.RecoveryJournal is not null)
            throw new IOException("Preserve requires the exact never-started plan; inspect started operations before recovery.");
        using var owner = OpenOwner(EffectiveOwner(plan), token, requireBefore: true);
        var observation = Observe(plan, session.Operation, inventory, token, owner);
        if (observation.Topology != "ready" || observation.Original?.Objects != plan.Original.Objects
            || observation.Root != plan.RootIdentity || observation.Parent != plan.ParentIdentity)
            throw new IOException("The reviewed installation changed; refresh before preserving this result.");
        return Result(plan, "preserved", true, false, false, ObservationHash(plan, observation),
            "Installed payload and native owner verified unchanged. The unused private plan remains available; no manual-maintenance preference was recorded.");
    }

    private static NativeStateDirectory OpenLocalReview(NativeStateDirectory operation, InventorySnapshot inventory, CancellationToken token) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(operation.CanonicalPath, "local-review"), Exclusions(inventory), token, allowCreate: false);
    private static NativeStateDirectory OpenArchive(NativeStateDirectory draft, string name, CancellationToken token) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(draft.CanonicalPath, name), [], token, allowCreate: false);

    private sealed record LocalReviewEvidence(string HeaderFingerprint,
        CapturedPayloadArchive Recorded, CapturedPayloadArchive Installed, CapturedPayloadArchive Source,
        LocalReviewSelection Selection, string SelectionFingerprint,
        ImmutableDictionary<string, ResolvedFile> Resolutions);

    private static LocalReviewEvidence LoadLocalReview(ReplacementPlan plan, NativeStateDirectory draft, CancellationToken token)
    {
        var (header, headerHash) = ReadLocal(draft, "header.json", LocalReviewJsonContext.Default.LocalReviewHeader, token);
        var sourcePlan = plan.LocalDisposition?.SourcePlanFingerprint ?? plan.ReviewFingerprint;
        if (header.SchemaVersion != 1 || header.PlanFingerprint != sourcePlan)
            throw new IOException("Local review belongs to a different revision plan.");
        CapturedPayloadArchive Load(string name, string hash)
        {
            if (!QuarantineStore.ValidHash(hash)) throw new IOException("Invalid local archive fingerprint.");
            using var archive = OpenArchive(draft, name, token);
            return CapturedPayloadArchive.Load(archive, hash, token).Archive;
        }
        var recorded = Load("recorded", header.RecordedManifest);
        var installed = Load("installed", header.InstalledManifest);
        var source = Load("source", header.SourceManifest);
        VerifyPayload(plan.Original, LegacyProjection(installed.Snapshot, token));
        VerifyPayload(plan.Revision!.RecordedBase!, LegacyProjection(recorded.Snapshot, token));
        VerifyPayload(plan.LocalDisposition?.SourceCandidate ?? plan.Candidate, LegacyProjection(source.Snapshot, token));
        ValidateResolutionBudget(draft, token);
        var entries = draft.EnumerateEntries(token).Take(MaxLocalSelections + MaxResolutionArchives + 6).ToArray();
        if (entries.Length > MaxLocalSelections + MaxResolutionArchives + 4 || entries.Any(item => item.Error is not null
            && !IsResolutionArchive(item.Name) && item.Name is not ("installed" or "recorded" or "source")))
            throw new IOException("Local review has unsafe or excessive entries.");
        var names = entries.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var count = 0; LocalReviewSelection? selection = null; string fingerprint = "";
        var archives = new Dictionary<string, ResolutionCapture>(StringComparer.Ordinal);
        var resolved = ImmutableDictionary<string, ResolvedFile>.Empty;
        while (names.Contains($"choices-{count:D2}.json") && count < MaxLocalSelections)
        {
            (selection, fingerprint) = ReadLocal(draft, $"choices-{count:D2}.json", LocalReviewJsonContext.Default.LocalReviewSelection, token);
            if (selection.HeaderFingerprint != headerHash || selection.Choices.IsDefault)
                throw new IOException("Local choice history has an invalid identity.");
            resolved = LoadResolutions(draft, selection, archives, token);
            // An incomplete or invalid newest record never revives older choices.
            _ = ThreeWayPayloadReview.Analyze(recorded.Snapshot, installed.Snapshot, source.Snapshot, selection.Choices,
                path => PathIdentity.NormalizeKey(Path.Combine(plan.OriginalPath, path)), resolved.Values.Select(item => item.Entry.Entry).ToArray());
            count++;
        }
        if (selection is null || names.Count != count + 4 + names.Count(IsResolutionArchive))
            throw new IOException("Local review has incomplete capture, unknown entries or a choice history gap; private evidence retained.");
        return new(headerHash, recorded, installed, source, selection, fingerprint, resolved);
    }

    private static PayloadSnapshot LegacyProjection(PayloadSnapshot snapshot, CancellationToken token)
    {
        var entries = snapshot.Entries.Select(entry => entry.Kind == "link" ? entry with { UnixMode = null } : entry).ToImmutableArray();
        var links = snapshot.Entries.Where(entry => entry.Kind == "link").Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var objects = snapshot.Objects.Select(item => links.Contains(item.Path)
            ? item with { UnixMode = null } : item).ToImmutableArray();
        return snapshot with
        {
            Entries = entries,
            Objects = objects,
            Fingerprint = PayloadSnapshot.Digest(entries, false),
            ComparisonFingerprint = PayloadSnapshot.Digest(entries, true),
            ObjectStateFingerprint = PayloadSnapshot.DigestObjects(new(snapshot.RootIdentity!.Value, snapshot.RootUnixMode), objects, token)
        };
    }

    private static ThreeWayPayloadReview AnalyzeLocal(LocalReviewEvidence evidence) =>
        ThreeWayPayloadReview.Analyze(evidence.Recorded.Snapshot, evidence.Installed.Snapshot, evidence.Source.Snapshot,
            evidence.Selection.Choices, path => PathIdentity.NormalizeKey(Path.Combine(evidence.Installed.Snapshot.Path, path)),
            evidence.Resolutions.Values.Select(item => item.Entry.Entry).ToArray());

    private static (T Value, string Fingerprint) ReadLocal<T>(NativeStateDirectory draft, string name, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var file = draft.OpenExistingFile(name, false, token);
        return BoundedReviewJson.Read(file, type, MaxJournalBytes, token);
    }

    private static void VerifyLocalDisposition(ReplacementPlan plan, NativeStateDirectory operation, InventorySnapshot inventory,
        string topology, ReplacementSlot? original, ReplacementSlot? retained, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.LocalDisposition is not { } disposition) return;
        using var draft = OpenLocalReview(operation, inventory, token);
        var evidence = LoadLocalReview(plan, draft, token);
        if (disposition.Kind != "merge-reviewed" || disposition.HeaderFingerprint != evidence.HeaderFingerprint
            || disposition.SelectionFingerprint != evidence.SelectionFingerprint || disposition.MetadataPolicy != PayloadMetadataReview.PolicyFor(evidence.Installed.RootMetadata!))
            throw new IOException("Merged plan differs from its exact saved local review.");
        using (var source = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "candidate"), token))
            VerifyPayload(disposition.SourceCandidate, source.Capture(token));
        VerifyNative(evidence.Installed, "installed", topology is "ready" or "restored" ? original! : retained!);
        VerifyNative(evidence.Recorded, "recorded", new(Path.Combine(operation.CanonicalPath, "recorded-base"), "", "", null, default));
        VerifyNative(evidence.Source, "source", new(Path.Combine(operation.CanonicalPath, "candidate"), "", "", null, default));
        using var installedArchive = OpenArchive(draft, "installed", token);
        using var sourceArchive = OpenArchive(draft, "source", token);
        using var resolved = OpenResolutionReaders(draft, evidence.Resolutions, token);
        foreach (var path in evidence.Resolutions.Keys) resolved.CopyEntry(path, Stream.Null, token);
        using var metadata = PayloadMetadataReview.Create(AnalyzeLocal(evidence), evidence.Installed.OpenReader(installedArchive), token,
            evidence.Source.OpenReader(sourceArchive), resolved);
        var merged = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        using var mergedLease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(merged.Path, operation.CanonicalPath)
            ? operation.CanonicalPath : plan.RootPath, merged.Path, token);
        var authored = mergedLease.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
        var review = AnalyzeLocal(evidence);
        if (!review.CanMaterialize || authored.Entries.Length != review.Paths.Count(row => row.Selected is not null)
            || authored.RootUnixMode != review.RootMode.Selected || authored.FrontMatter?.Version != disposition.ResultAuthoredVersion)
            throw new IOException("Merged result path set or root mode differs from review.");
        var rows = review.Paths.ToDictionary(row => row.Path, StringComparer.Ordinal);
        foreach (var entry in authored.Entries)
        {
            var expected = rows.GetValueOrDefault(entry.Path)?.Selected;
            if (expected is null || expected.Kind != entry.Kind || expected.UnixMode != entry.UnixMode
                || (entry.Path == "SKILL.md" ? expected.ComparisonHash != entry.ComparisonHash : expected.ContentHash != entry.ContentHash))
                throw new IOException("Merged authored content differs from review.");
        }

        void VerifyNative(CapturedPayloadArchive archive, string name, ReplacementSlot slot)
        {
            using var saved = OpenArchive(draft, name, token);
            using var authority = PayloadMetadataAuthority.Open(saved, create: false, token);
            var metadata = archive.Entries.ToDictionary(entry => entry.Entry.Path, entry => entry.Metadata!, StringComparer.Ordinal);
            using var lease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(slot.Path, operation.CanonicalPath)
                ? operation.CanonicalPath : plan.RootPath, slot.Path, token);
            var captured = lease.CaptureInto((item, _) => Check(item), Check, token);
            if (captured.Fingerprint != archive.Snapshot.Fingerprint) throw new IOException("A local review input payload changed.");
            void Check(NativePayloadItem item)
            {
                var expected = item.RelativePath.Length == 0 ? archive.RootMetadata! : metadata[item.RelativePath];
                var current = PayloadNativeMetadata.Capture(item, token, authority);
                if (current.Kind != expected.Kind || current.UnixMode != expected.UnixMode || current.UserId != expected.UserId
                    || current.GroupId != expected.GroupId || current.LinkCount != expected.LinkCount || current.DirectoryLink != expected.DirectoryLink
                    || current.EnumerationCoverage != expected.EnumerationCoverage || !current.Refusals.SequenceEqual(expected.Refusals)
                    || !WindowsPayloadMetadata.Same(expected.Windows, current.Windows)
                    || current.Attributes.Length != expected.Attributes.Length
                    || expected.Attributes.Any(attr => !current.Attributes.Any(other => attr.Name == other.Name && attr.Value.AsSpan().SequenceEqual(other.Value))))
                    throw new IOException("Native metadata changed since local review: " + item.RelativePath);
            }
        }
    }
}
