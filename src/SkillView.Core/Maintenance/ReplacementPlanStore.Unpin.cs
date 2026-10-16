using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    internal const string UnpinPurpose = "unpin-only";
    internal static string Disposition(ReplacementPlan plan) => plan.LocalImport is not null ? LocalReimportDisposition : plan.PinPolicy is not null ? UnpinPurpose
        : plan.LocalDisposition is null ? "retain-original" : "merge-reviewed";

    private static void RequirePurpose(ReplacementPlan plan, string? expectedPurpose)
    {
        if (expectedPurpose is null ? plan.PinPolicy is not null || plan.LocalImport is not null : plan.Purpose != expectedPurpose)
            throw new IOException("This command cannot operate on the saved " + plan.Purpose + " purpose.");
    }

    private static ReplacementOwnerTransition? EffectiveOwner(ReplacementPlan plan)
    {
        if (plan.LocalImport is not null)
        {
            if (plan.SchemaVersion != 5 || plan.Purpose != LocalReimportPurpose || plan.Revision is not null
                || plan.LocalDisposition is not null || plan.PinPolicy is not null || !plan.Subject.Owners.IsEmpty)
                throw new IOException("Local reimport conflicts with saved schema, purpose or owner authority.");
            return null;
        }
        if (plan.SchemaVersion == 5 || plan.Purpose == LocalReimportPurpose)
            throw new IOException("Local reimport lacks its explicit source policy.");
        if (plan.PinPolicy is not null)
        {
            if (plan.SchemaVersion != 4 || plan.Purpose != UnpinPurpose || plan.Revision is not null || plan.LocalDisposition is not null)
                throw new IOException("Pin plan owner authority conflicts with its schema or purpose.");
            return plan.PinPolicy.Owner;
        }
        if (plan.SchemaVersion == 4 || plan.Purpose == UnpinPurpose)
            throw new IOException("Pin plan lacks its explicit policy and owner authority.");
        return plan.Revision?.Owner;
    }

    internal ReplacementPreparation PlanUnpin(InstalledSkill target, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Unpin needs a containing scan root; add --scan-root for its actual directory.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        // Read-only owner admission and complete native payload capture precede
        // every state writer, quota enumeration or private directory creation.
        var transition = PrepareOwner(target, null, inventory, token, unpinOnly: true, initializeState: false);
        PayloadSnapshot before;
        byte[] desiredSkill;
        using (var owner = OpenOwner(transition, token, requireBefore: true, exactEntryBytes: true))
        {
            inventory = Admit(target, inventory, token, owner: owner);
            CheckPlacement(inventory, token);
            using var original = NativeDirectoryMoveLease.Open(target.ScanRoot, target.ResolvedPath, token);
            before = original.Capture(token);
            byte[]? skill = null;
            var bound = original.CaptureInto((item, _) =>
            {
                if (item.RelativePath != "SKILL.md") return;
                var input = item.Content ?? throw new IOException("Unpin requires regular SKILL.md.");
                if (input.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("SKILL.md exceeds the pin review byte limit.");
                skill = new byte[checked((int)input.Length)]; input.ReadExactly(skill);
            }, _ => { }, token);
            if (bound.FrontMatter is not { } live) throw new IOException("Unpin requires captured tracking metadata.");
            SameTracking(target.FrontMatter, live);
            if (!SameParsedMetadata(target.FrontMatter, live))
                throw new IOException("Installed authored metadata changed after inventory capture; refresh before unpinning.");
            desiredSkill = PinTrackingWriter.Unpin(skill ?? throw new IOException("No installed SKILL.md was captured."));
            original.Revalidate(token);
            var after = original.Capture(token);
            if (before.Fingerprint != after.Fingerprint || before.ObjectStateFingerprint != after.ObjectStateFingerprint)
                throw new IOException("Installed content changed during pin inspection.");
            _ = Context(target, inventory, token, owner: owner);
            if (!live.Pinned)
            {
                if (transition is not null && transition.BeforeFingerprint != transition.DesiredFingerprint)
                    throw new IOException("Owner and installed pin evidence disagree; review ownership before unpinning.");
                return ReplacementPreparation.Current(new(target.InstallationId, target.ResolvedPath, before.Fingerprint,
                    "Verified already unpinned. Installed files, owner bytes and private state remain unchanged; no source was resolved.", null));
            }
        }
        // Initialize private ancestors before freezing the registry-parent
        // generation; creating state must never refresh an already saved plan.
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
        using var writer = state.OpenFile("writer.lock", true, token); CheckWriter(writer);
        var entries = state.EnumerateEntries(token).Take(MaxPlans + 2).ToArray();
        if (entries.Length > MaxPlans || entries.Any(entry => entry.Name != "writer.lock" && !QuarantineStore.ValidId(entry.Name)))
            throw new IOException("Replacement plan quota or state entry policy refused; inspect retained plans.");
        transition = PrepareOwner(target, null, inventory, token, unpinOnly: true, initializeState: false);
        using var heldOwner = OpenOwner(transition, token, requireBefore: true, exactEntryBytes: true);
        inventory = Admit(target, inventory, token, owner: heldOwner);
        var context = Context(target, inventory, token, owner: heldOwner);
        using var lease = NativeDirectoryMoveLease.Open(target.ScanRoot, target.ResolvedPath, token);
        VerifyPayload(ReplacementEvidence.From(before), lease.Capture(token));
        var id = Guid.NewGuid().ToString("N");
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, id), Exclusions(inventory), token);
        try
        {
            using var operationWriter = operation.OpenFile("writer.lock", true, token); CheckWriter(operationWriter);
            _ = CapturedPayloadArchive.Capture(operation, "installed-capture", lease, token);
            checkpoint?.Invoke("unpin-installed-captured");
            using var archive = OpenArchive(operation, "installed-capture", token);
            var loaded = CapturedPayloadArchive.Load(archive, null, token);
            var reader = loaded.Archive.OpenReader(archive);
            using var metadata = PayloadMetadataReview.PreserveInstalled(loaded.Archive, reader, token);
            using (var input = new MemoryStream())
            {
                reader.CopyEntry("SKILL.md", input, token);
                if (!PinTrackingWriter.Unpin(input.ToArray()).AsSpan().SequenceEqual(desiredSkill))
                    throw new IOException("Installed pin bytes changed before immutable capture.");
            }
            var staged = InstalledPayloadMaterializer.Stage(operation, loaded.Archive, reader, metadata, desiredSkill, token);
            using var candidate = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "candidate"), token);
            var desired = candidate.FlushPayload(token);
            VerifyPayload(ReplacementEvidence.From(LegacyProjection(staged, token)), desired);
            var desiredMetadata = staged.FrontMatter ?? throw new IOException("Unpinned stage has no valid tracking metadata.");
            SameTracking(target.FrontMatter with { Pinned = false, PinnedRef = null }, desiredMetadata);
            inventory = Admit(target, inventory, token, owner: heldOwner);
            VerifyPayload(ReplacementEvidence.From(before), lease.Capture(token));
            if (context != Context(target, inventory, token, owner: heldOwner)) throw new IOException("Unpin context changed during staging.");
            var policy = new ReplacementPinPolicy(desiredMetadata, loaded.Fingerprint, metadata.PolicyName, metadata.Recipes, transition);
            var subject = new ReplacementSubject(target.InstallationId, target.Name, target.Scope, target.Agents,
                target.FrontMatter, target.AllOwnershipClaims.ToImmutableArray());
            var plan = new ReplacementPlan(4, id, UnpinPurpose, DateTimeOffset.UtcNow, before.Path,
                lease.RootIdentity.CanonicalPath, lease.RootIdentity, lease.ParentIdentity, subject, context,
                ReplacementEvidence.From(before), ReplacementEvidence.From(desired), ["SKILL.md"], "", PinPolicy: policy);
            ValidatePinPolicy(plan, operation, token);
            plan = plan with { ReviewFingerprint = PlanHash(plan) };
            Write(operation, "plan.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
            return ReplacementPreparation.Saved(Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
                "Saved standalone unpin. Source, recorded ref/tree and authored payload are preserved; no source was resolved. All shown exposures share this policy change. Applying swaps a metadata-preserving clone with new native object identities and regenerated timestamps; exact original native objects remain retained for undo. Review pin/owner changes and approve retention before applying."));
        }
        catch (OperationCanceledException ex)
        {
            throw new OperationCanceledException($"Standalone unpin planning canceled. Private evidence retained for {id} at {operation.CanonicalPath}. {ex.Message}", ex, ex.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Standalone unpin planning refused. Private evidence retained for {id} at {operation.CanonicalPath}. {ex.Message}", ex);
        }
    }

    private static bool SameParsedMetadata(SkillFrontMatter expected, SkillFrontMatter actual) =>
        // Record equality binds every modeled scalar. Compare collection contents
        // separately: flattening can hide a list/scalar change in agents/tools.
        expected == (actual with
        {
            Metadata = expected.Metadata,
            Diagnostics = expected.Diagnostics,
            AllowedTools = expected.AllowedTools,
            Agents = expected.Agents,
            Extra = expected.Extra
        })
        && SameMap(expected.Metadata, actual.Metadata) && SameMap(expected.Extra, actual.Extra)
        && expected.Diagnostics.SequenceEqual(actual.Diagnostics)
        && expected.AllowedTools.SequenceEqual(actual.AllowedTools)
        && expected.Agents.SequenceEqual(actual.Agents);

    private static bool SameMap(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count && expected.All(item => actual.TryGetValue(item.Key, out var value) && item.Value == value);

    private static void ValidatePinPolicy(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.PinPolicy is not { } policy) return;
        _ = EffectiveOwner(plan);
        if (!plan.Subject.Metadata.Pinned || policy.DesiredMetadata is null || policy.DesiredMetadata.Pinned
            || policy.DesiredMetadata.PinnedRef is not null || !QuarantineStore.ValidHash(policy.ArchiveFingerprint)
            || policy.Metadata.IsDefault || !plan.ChangedPaths.SequenceEqual(["SKILL.md"]))
            throw new IOException("Saved unpin policy is inconsistent.");
        SameTracking(plan.Subject.Metadata with { Pinned = false, PinnedRef = null }, policy.DesiredMetadata);
        using var archive = OpenArchive(operation, "installed-capture", token);
        var installed = CapturedPayloadArchive.Load(archive, policy.ArchiveFingerprint, token).Archive;
        VerifyUnpinOriginal(plan.Original, installed, token);
        var reader = installed.OpenReader(archive);
        using var metadata = PayloadMetadataReview.PreserveInstalled(installed, reader, token);
        var recomputed = policy with { MetadataPolicy = metadata.PolicyName, Metadata = metadata.Recipes };
        if (!JsonSerializer.SerializeToUtf8Bytes(policy, ReplacementJsonContext.Default.ReplacementPinPolicy)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(recomputed, ReplacementJsonContext.Default.ReplacementPinPolicy)))
            throw new IOException("Unpin metadata policy differs from immutable installed evidence.");
        using var input = new MemoryStream(); reader.CopyEntry("SKILL.md", input, token);
        var desired = PinTrackingWriter.Unpin(input.ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(desired));
        var expected = installed.Snapshot.Entries.Select(entry => NativeUnpinEntry(entry.Path == "SKILL.md"
            ? entry with { ContentHash = hash, Bytes = desired.Length } : entry));
        if (plan.Original.Mode != plan.Candidate.Mode || !expected.SequenceEqual(plan.Candidate.Entries))
            throw new IOException("Unpin candidate changes content outside the exact pin member.");
        if (policy.Owner is { } owner) ValidateOwnerTransition(plan.Subject.Metadata, plan.Subject.Owners, null, owner, token, unpinOnly: true);
        else if (!plan.Subject.Owners.IsEmpty) throw new IOException("Unpin plan lacks its required owner transition.");
    }

    // Native Capture/FlushPayload use CaptureEvidence: SKILL.md comparison hashes
    // are raw hashes and Unix link modes are absent. CapturedPayloadArchive uses
    // strict CaptureInto instead, retaining normalized authored hashes and link
    // modes. Project only those representation differences; retain raw content,
    // complete native identities/generations and every other entry field.
    private static PayloadEntry NativeUnpinEntry(PayloadEntry entry) => entry with
    {
        ComparisonHash = entry.ContentHash,
        UnixMode = entry.Kind == "link" ? null : entry.UnixMode
    };

    internal static void VerifyUnpinOriginal(ReplacementEvidence original, CapturedPayloadArchive installed, CancellationToken token)
    {
        var legacy = LegacyProjection(installed.Snapshot, token);
        var identityMatches = legacy.RootIdentity == original.Identity;
        var modeMatches = legacy.RootUnixMode == original.Mode;
        var entriesMatch = legacy.Entries.Select(NativeUnpinEntry).SequenceEqual(original.Entries);
        var childrenMatch = legacy.Objects.SequenceEqual(original.Children);
        var payloadMatches = legacy.Fingerprint == original.Payload;
        var objectFingerprintMatches = legacy.ObjectStateFingerprint == original.Objects;
        if (!identityMatches || !modeMatches || !entriesMatch || !childrenMatch || !payloadMatches || !objectFingerprintMatches)
            throw new IOException("Unpin archive is not the actual captured original. "
                + $"Root identity/generation match: {identityMatches}; root mode match: {modeMatches}; "
                + $"native entry evidence match: {entriesMatch}; native child identities/generations match: {childrenMatch}; "
                + $"raw payload fingerprint match: {payloadMatches}; native object fingerprint match: {objectFingerprintMatches}.");
    }

    private static void VerifyPinPolicy(ReplacementPlan plan, NativeStateDirectory operation, string topology,
        ReplacementSlot? original, ReplacementSlot? retained, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.PinPolicy is not { } policy) return;
        ValidatePinPolicy(plan, operation, token);
        using var archive = OpenArchive(operation, "installed-capture", token);
        var installed = CapturedPayloadArchive.Load(archive, policy.ArchiveFingerprint, token).Archive;
        var reader = installed.OpenReader(archive);
        using var metadata = PayloadMetadataReview.PreserveInstalled(installed, reader, token);
        foreach (var entry in installed.Entries.Where(item => item.Blob is not null)) reader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        var old = topology is "ready" or "restored" ? original! : retained!;
        var current = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        Verify(old); Verify(current);

        void Verify(ReplacementSlot slot)
        {
            using var lease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(slot.Path, operation.CanonicalPath)
                ? operation.CanonicalPath : plan.RootPath, slot.Path, token);
            lease.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
        }
    }
}
