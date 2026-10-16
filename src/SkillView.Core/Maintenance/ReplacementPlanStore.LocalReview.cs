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

    internal LocalUpdateReview ReviewLocalChanges(string id, string expectedPlan, InventorySnapshot inventory,
        CancellationToken token)
    {
        using var session = Open(id, inventory, token);
        RequireLocalReviewReady(session, expectedPlan, inventory, token);
        if (!session.Operation.EnumerateEntries(token).Any(item => item.Name == "local-review"))
            CaptureLocalReview(session, inventory, token);
        using var draft = OpenLocalReview(session.Operation, inventory, token);
        var evidence = LoadLocalReview(session.Plan, draft, token);
        return LocalReviewResult(session.Plan, draft, evidence, token);
    }

    internal LocalUpdateReview ChooseLocalChanges(string id, string expectedReview, IReadOnlyList<LocalPathChoice> choices,
        InventorySnapshot inventory, CancellationToken token, IReadOnlyList<LocalResolutionInput>? resolutions = null)
    {
        token.ThrowIfCancellationRequested();
        using var session = Open(id, inventory, token);
        RequireLocalReviewReady(session, session.Plan.ReviewFingerprint, inventory, token);
        using var draft = OpenLocalReview(session.Operation, inventory, token);
        var evidence = LoadLocalReview(session.Plan, draft, token);
        if (expectedReview != evidence.SelectionFingerprint)
            throw new IOException("Local review changed; inspect its current fingerprint before choosing paths.");
        var next = evidence.SelectionCount;
        if (next >= MaxLocalSelections) throw new IOException("Local review choice history is full; retained evidence was left unchanged.");
        var selection = CaptureResolutionSelection(session, draft, evidence, choices, resolutions ?? [], inventory, token);
        var selectedFiles = LoadResolutions(draft, selection, new(StringComparer.Ordinal), token);
        // Validate all choices before publishing even a private review.
        _ = AnalyzeLocal(evidence, selection.Choices, selectedFiles.Values.Select(item => item.Entry.Entry).ToArray());
        RequireLocalReviewReady(session, session.Plan.ReviewFingerprint, inventory, token);
        WriteLocal(draft, $"choices-{next:D2}.json", selection, LocalReviewJsonContext.Default.LocalReviewSelection, token, session.Operation);
        return LocalReviewResult(session.Plan, draft, LoadLocalReview(session.Plan, draft, token), token);
    }

    internal ReplacementResult FinalizeLocalChanges(string id, string expectedReview, InventorySnapshot inventory,
        CancellationToken token)
    {
        using var session = Open(id, inventory, token);
        RequireLocalReviewReady(session, session.Plan.ReviewFingerprint, inventory, token);
        using var draft = OpenLocalReview(session.Operation, inventory, token);
        var evidence = LoadLocalReview(session.Plan, draft, token);
        if (expectedReview != evidence.SelectionFingerprint) throw new IOException("Local review changed; inspect before building the merged plan.");
        var review = AnalyzeLocal(evidence, evidence.Selection.Choices);
        if (!review.CanMaterialize) throw new IOException("Resolve all path and topology conflicts before building the merged plan.");
        using var installedArchive = OpenArchive(draft, "installed", token);
        using var sourceArchive = OpenArchive(draft, "source", token);
        using var resolved = OpenResolutionReaders(draft, evidence.Resolutions, token);
        var readers = new Dictionary<string, IPayloadEvidenceReader>(StringComparer.Ordinal)
        {
            ["installed"] = evidence.Installed.OpenReader(installedArchive),
            ["source"] = evidence.Source.OpenReader(sourceArchive),
            ["resolved"] = resolved
        };
        // Metadata admission precedes creation. Unsupported evidence remains a
        // reviewable refusal and can never authorize a lossy publication.
        using (var metadata = PayloadMetadataReview.Create(review, (CapturedPayloadArchive.Reader)readers["installed"], token, readers["source"], resolved)) { }
        var attemptNames = session.Operation.EnumerateEntries(token).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var attempt = 0;
        while (attemptNames.Contains($"merged-{attempt:D2}") && attempt < MaxMergeAttempts) attempt++;
        if (attempt >= MaxMergeAttempts) throw new IOException("Merged draft attempt history is full; partial evidence retained. Create a new revision plan.");
        var mergedName = $"merged-{attempt:D2}";
        var merged = ReviewedPayloadMaterializer.StagePreservingMetadata(session.Operation, mergedName, review,
            readers, session.Plan.Revision!.DesiredMetadata, token);
        using var mergedLease = NativeDirectoryMoveLease.Open(session.Operation.CanonicalPath,
            Path.Combine(session.Operation.CanonicalPath, mergedName), token);
        var legacyMerged = mergedLease.Capture(token);
        var plan = session.Plan with
        {
            SchemaVersion = 3,
            Candidate = ReplacementEvidence.From(legacyMerged),
            ChangedPaths = PayloadSnapshot.Differences(evidence.Installed.Snapshot, merged.Snapshot, false)
                .AddRange(evidence.Installed.Snapshot.RootUnixMode != merged.Snapshot.RootUnixMode ? new[] { "/" } : []),
            LocalDisposition = new("merge-reviewed", session.Plan.ReviewFingerprint, evidence.HeaderFingerprint,
                evidence.SelectionFingerprint, session.Plan.Candidate, merged.MetadataPolicy, mergedName, merged.Snapshot.FrontMatter!.Version),
            ReviewFingerprint = ""
        };
        plan = plan with { ReviewFingerprint = PlanHash(plan) };
        using var owner = OpenOwner(EffectiveOwner(plan)!, token, requireBefore: true);
        var current = Observe(plan, session.Operation, inventory, token, owner);
        if (current.Topology != "ready" || current.Original?.Objects != plan.Original.Objects
            || current.Candidate?.Objects != plan.Candidate.Objects || current.Root != plan.RootIdentity || current.Parent != plan.ParentIdentity)
            throw new IOException("The installation, source or context changed while building the merged plan; private evidence retained.");
        Write(session.Operation, PlanName(plan), plan, ReplacementJsonContext.Default.ReplacementPlan, token);
        return Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
            "Saved reviewed merge. Approved source remains the future baseline; local bytes have their own exact result fingerprint. Review the full plan before applying; original and owner remain unchanged.");
    }

    private void RequireLocalReviewReady(Session session, string expectedPlan, InventorySnapshot inventory, CancellationToken token)
    {
        var plan = session.Plan;
        if (plan.SchemaVersion != 2 || plan.Revision is null || expectedPlan != plan.ReviewFingerprint
            || session.StepCount != 0 || session.IncompleteJournal || session.RecoveryJournal is not null)
            throw new IOException("Local review requires the exact never-started revision plan.");
        using var owner = OpenOwner(EffectiveOwner(plan)!, token, requireBefore: true);
        var observation = Observe(plan, session.Operation, inventory, token, owner);
        if (observation.Topology != "ready" || observation.Original?.Objects != plan.Original.Objects
            || observation.Candidate?.Objects != plan.Candidate.Objects || observation.Root != plan.RootIdentity || observation.Parent != plan.ParentIdentity)
            throw new IOException("The reviewed installation or stage changed; create a new revision plan.");
    }

    private void CaptureLocalReview(Session session, InventorySnapshot inventory, CancellationToken token)
    {
        // Creating this directory closes admission for the old replacement
        // approval, even if a crash leaves only partial private capture files.
        using var draft = session.Operation.CreateStagingDirectory("local-review", token);
        checkpoint?.Invoke("local-review-created");
        Capture("installed", session.Plan.RootPath, session.Plan.OriginalPath);
        Capture("recorded", session.Operation.CanonicalPath, Path.Combine(session.Operation.CanonicalPath, "recorded-base"));
        Capture("source", session.Operation.CanonicalPath, Path.Combine(session.Operation.CanonicalPath, "candidate"));
        RequireLocalReviewReady(session, session.Plan.ReviewFingerprint, inventory, token);
        var installed = Manifest("installed"); var recorded = Manifest("recorded"); var source = Manifest("source");
        var header = new LocalReviewHeader(1, session.Plan.ReviewFingerprint, recorded, installed, source);
        var headerHash = WriteLocal(draft, "header.json", header, LocalReviewJsonContext.Default.LocalReviewHeader, token);
        WriteLocal(draft, "choices-00.json", new LocalReviewSelection(1, headerHash, []), LocalReviewJsonContext.Default.LocalReviewSelection, token);
        draft.SealStagingDirectory(448, token);

        void Capture(string name, string scanRoot, string path)
        {
            using var lease = NativeDirectoryMoveLease.Open(scanRoot, path, token);
            CapturedPayloadArchive.Capture(draft, name, lease, token);
        }
        string Manifest(string name)
        {
            using var archive = OpenArchive(draft, name, token);
            return CapturedPayloadArchive.Load(archive, null, token).Fingerprint;
        }
    }

    private static NativeStateDirectory OpenLocalReview(NativeStateDirectory operation, InventorySnapshot inventory, CancellationToken token) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(operation.CanonicalPath, "local-review"), Exclusions(inventory), token, allowCreate: false);
    private static NativeStateDirectory OpenArchive(NativeStateDirectory draft, string name, CancellationToken token) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(draft.CanonicalPath, name), [], token, allowCreate: false);

    private sealed record LocalReviewEvidence(LocalReviewHeader Header, string HeaderFingerprint,
        CapturedPayloadArchive Recorded, CapturedPayloadArchive Installed, CapturedPayloadArchive Source,
        LocalReviewSelection Selection, string SelectionFingerprint, int SelectionCount,
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
        _ = ResolutionBudget(draft, token);
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
        return new(header, headerHash, recorded, installed, source, selection, fingerprint, count, resolved);
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

    private static ThreeWayPayloadReview AnalyzeLocal(LocalReviewEvidence evidence, IReadOnlyList<LocalPathChoice> choices,
        IReadOnlyList<PayloadEntry>? resolutions = null) =>
        ThreeWayPayloadReview.Analyze(evidence.Recorded.Snapshot, evidence.Installed.Snapshot, evidence.Source.Snapshot,
            choices, path => PathIdentity.NormalizeKey(Path.Combine(evidence.Installed.Snapshot.Path, path)),
            resolutions ?? evidence.Resolutions.Values.Select(item => item.Entry.Entry).ToArray());

    private static LocalUpdateReview LocalReviewResult(ReplacementPlan plan, NativeStateDirectory draft, LocalReviewEvidence evidence, CancellationToken token)
    {
        var review = AnalyzeLocal(evidence, evidence.Selection.Choices);
        var refusals = ImmutableArray.CreateBuilder<string>();
        using var installed = OpenArchive(draft, "installed", token);
        using var source = OpenArchive(draft, "source", token);
        using var resolved = OpenResolutionReaders(draft, evidence.Resolutions, token);
        try { using var metadata = PayloadMetadataReview.Create(review, evidence.Installed.OpenReader(installed), token, evidence.Source.OpenReader(source), resolved); }
        catch (IOException ex) { refusals.Add(ex.Message); }
        return new(plan.Id, plan.ReviewFingerprint, evidence.HeaderFingerprint, evidence.SelectionFingerprint,
            draft.CanonicalPath, review, refusals.ToImmutable(), review.CanMaterialize && refusals.Count == 0,
            "Saved immutable recorded/installed/source review. Live payload, owner and preferences remain unchanged. Resolve every conflict, then build and review a separate merged plan.");
    }

    private static string WriteLocal<T>(NativeStateDirectory draft, string name, T value, JsonTypeInfo<T> type, CancellationToken token,
        NativeStateDirectory? publicationAuthority = null)
    {
        using var file = draft.OpenFile(name, false, token);
        var hash = BoundedReviewJson.Write(file, value, type, MaxJournalBytes, token);
        file.Flush(true); draft.ValidateLocation(token);
        // Reopened drafts use the operation's still-held writer durability
        // barrier. Initial capture instead owns a staging-directory guard.
        draft.FlushNamespace(token);
        (publicationAuthority ?? draft).FlushPublication(token);
        return hash;
    }
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
        using var metadata = PayloadMetadataReview.Create(AnalyzeLocal(evidence, evidence.Selection.Choices), evidence.Installed.OpenReader(installedArchive), token,
            evidence.Source.OpenReader(sourceArchive), resolved);
        var merged = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        using var mergedLease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(merged.Path, operation.CanonicalPath)
            ? operation.CanonicalPath : plan.RootPath, merged.Path, token);
        var authored = mergedLease.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
        var review = AnalyzeLocal(evidence, evidence.Selection.Choices);
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
