using System.Collections.Immutable;
using System.Security.Cryptography;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

/// Immutable native archives have their own retained budget. Every directory,
/// including a failed or unreferenced attempt, reserves its full working bound.
internal sealed class ApprovedPayloadStore(string root, ProcessRunner runner, Logger logger)
{
    internal const int MaxCaptures = 4;
    internal const int MaxHeaderBytes = 16 * 1024 * 1024;
    internal const long ReservedBytesPerCapture = 3 * (PayloadSnapshot.MaxBytes + CapturedPayloadArchive.MaxManifestBytes) + MaxHeaderBytes;
    internal const string SourceReviewContract = "schema8-independent-source-review-required-on-final-base-v1";
    internal string Root { get; } = Path.GetFullPath(root);
    private string CurrentContextFingerprint => runner.CaptureContext().CaptureContextFingerprint();
    internal static ApprovedPayloadStore For(ProcessRunner process, Logger logger) => new(Path.Combine(
        Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(process))!, "approved-payloads"), process, logger);

    internal ApprovedPayloadReference CaptureCustom(InstalledSkill target, InventorySnapshot inventory,
        string expectedPayload, CancellationToken token) => Capture(target, inventory, expectedPayload, null, null, null, token);

    internal ApprovedPayloadReference CaptureSelected(InstalledSkill target, InventorySnapshot inventory,
        string expectedPayload, string planStoreAddress, string planId, string expectedPlan, CancellationToken token) =>
        Capture(target, inventory, expectedPayload, planStoreAddress, planId, expectedPlan, token);

    private ApprovedPayloadReference Capture(InstalledSkill target, InventorySnapshot inventory, string expectedPayload,
        string? planStoreAddress, string? planId, string? expectedPlan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var process = runner.CaptureContext();
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete || target.Validity != ValidityState.Valid
            || !target.MetadataConflicts.IsEmpty || !target.OwnershipDiagnostics.IsEmpty || !target.FrontMatter.Diagnostics.IsEmpty)
            throw new IOException("Approval capture requires complete inventory and valid unambiguous installed metadata.");
        if (!QuarantineStore.ValidHash(expectedPayload) || (planId is null) != (expectedPlan is null)
            || (planId is null) != (planStoreAddress is null)
            || planId is not null && (!QuarantineStore.ValidId(planId) || !QuarantineStore.ValidHash(expectedPlan!) || !Path.IsPathFullyQualified(planStoreAddress!)))
            throw new IOException("Approval capture needs the independently reviewed installed payload and optional exact selected plan.");
        ManagedSkillDiscovery.RefuseTarget(target, token, process);
        ManagedSkillDiscovery.RefusePaths([Root], token, process);
        var exclusions = Exclusions(inventory);
        // Capture itself does not set desired intent or edit actual pins.
        using var policy = DesiredRevisionPolicyStore.For(process).Acquire(exclusions, token);
        DesiredRevisionPolicyGate.RequireKnown(policy.Observe(target, token));
        using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token);
        using var writer = state.OpenFile("writer.lock", true, token); CheckWriter(writer);
        CheckBinding(state, exclusions, token);
        var entries = Entries(state, token);
        if (entries.Length >= MaxCaptures + 1)
            throw new IOException("Approved archive quota is reserved by complete, partial or unreferenced captures; none are silently discarded.");
        var id = Guid.NewGuid().ToString("N");
        using var capture = state.CreateStagingDirectory(id, token);
        // Reservation precedes native capture. Any failure deliberately retains
        // this full-budget slot, without creating a completed approval header.
        var replacements = new ReplacementPlanStore(planStoreAddress ?? Path.Combine(Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(process))!, "replacements"), logger, runner: process);
        var observed = replacements.CaptureApprovedPayload(target, inventory, capture, expectedPayload, planId, expectedPlan, token);
        using var archive = OpenChild(capture, "payload", token);
        var loaded = CapturedPayloadArchive.Load(archive, null, token);
        using var metadata = PayloadMetadataReview.PreserveInstalled(loaded.Archive, loaded.Archive.OpenReader(archive), token);
        var actual = observed.Target;
        var header = new ApprovedPayloadHeader(1, id, DateTimeOffset.UtcNow,
            observed.Selected is null ? "captured-custom" : "verified-selected-result", process.CaptureContextFingerprint(),
            DesiredRevisionPolicyStore.Bind(actual), new(actual.InstallationId, actual.Name, actual.Scope, actual.Agents,
                actual.FrontMatter, actual.AllOwnershipClaims.ToImmutableArray()), ReplacementEvidence.From(loaded.Archive.Snapshot),
            loaded.Fingerprint, metadata.PolicyName, metadata.Recipes, SourceReviewContract, "", observed.Selected, observed.Owner);
        header = header with { Fingerprint = Hash(header) };
        Validate(header, loaded.Archive, loaded.Archive.OpenReader(archive), capture, token);
        using (var output = capture.OpenFile("approved.json", false, token))
        { BoundedReviewJson.Write(output, header, ApprovedPayloadJsonContext.Default.ApprovedPayloadHeader, MaxHeaderBytes, token); output.Flush(true); }
        capture.FlushPublication(token); capture.SealStagingDirectory(448, token);
        state.FlushPublication(token);
        return new(state.CanonicalPath, id, header.Fingerprint);
    }

    internal ApprovedPayloadInventory Inventory(IReadOnlyList<string> exclusions, CancellationToken token)
    {
        try
        {
            if (NativeStatePresence.IsMissing(Root, token)) return new([], null);
            ManagedSkillDiscovery.RefusePaths([Root], token, runner);
            using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
            using var writer = state.OpenExistingFile("writer.lock", true, token); CheckWriter(writer);
            CheckBinding(state, exclusions, token);
            var result = ImmutableArray.CreateBuilder<ApprovedPayloadSummary>();
            var partial = false;
            foreach (var item in Entries(state, token).Where(item => item.Name != "writer.lock"))
            {
                using var capture = OpenChild(state, item.Name, token);
                var files = capture.EnumerateEntries(token).Take(6).ToArray();
                if (!files.Any(entry => entry.Name == "approved.json")) { partial = true; continue; }
                var header = ReadHeader(capture, token);
                if (header.SchemaVersion != 1 || header.Id != item.Name || header.Subject is null || header.Binding is null || header.Captured is null
                    || header.Fingerprint != Hash(header) || header.SourceReviewContract != SourceReviewContract)
                    throw new IOException("Approval header projection is corrupt.");
                result.Add(new(new(state.CanonicalPath, header.Id, header.Fingerprint), header.Kind,
                    header.Binding.Path, header.Subject.Name, header.Captured.Payload, header.MetadataPolicy,
                    header.Selected is null ? "Captured installed/custom bytes; upstream pristine status is unproved." : "Completed selected-result evidence; source authenticity/session state remain separate."));
            }
            CheckBinding(state, exclusions, token);
            return new(result.ToImmutable(), partial ? "Incomplete attempts remain charged and cannot authorize approval or restore." : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return new([], "Approved archives unavailable: " + ex.Message); }
    }

    internal ReadLease Open(ApprovedPayloadReference reference, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!QuarantineStore.ValidId(reference.Id) || !QuarantineStore.ValidHash(reference.Fingerprint)
            || !Path.IsPathFullyQualified(reference.StoreAddress)) throw new IOException("Exact local approval reference is invalid.");
        ManagedSkillDiscovery.RefusePaths([Root], token, runner);
        var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
        FileStream? writer = null; NativeStateDirectory? capture = null;
        try
        {
            writer = state.OpenExistingFile("writer.lock", true, token); CheckWriter(writer);
            _ = Entries(state, token); CheckBinding(state, exclusions, token);
            if (!PathIdentity.Equals(state.CanonicalPath, reference.StoreAddress)) throw new IOException("Approval belongs to a different native store.");
            capture = OpenChild(state, reference.Id, token);
            var header = ReadHeader(capture, token);
            if (header.Id != reference.Id || header.Fingerprint != reference.Fingerprint || header.Fingerprint != Hash(header))
                throw new IOException("Approval fingerprint changed; inspect its exact completed capture.");
            var lease = new ReadLease(this, state, writer, capture, header, exclusions.ToArray());
            lease.Verify(token); return lease;
        }
        catch { capture?.Dispose(); writer?.Dispose(); state.Dispose(); throw; }
    }

    internal DesiredPolicyObservation SetPolicy(ApprovedPayloadReference reference, InstalledSkill target,
        InventorySnapshot inventory, string expectedPolicy, bool approved, CancellationToken token)
    {
        if (!approved) throw new IOException("Explicit approval is required to publish immutable desired intent.");
        if (!SecureRemovalBackend.TryCanonicalizePath(target.ResolvedPath, out var canonical, out var error)) throw new IOException(error);
        target = target with { ResolvedPath = canonical };
        var process = runner.CaptureContext(); var exclusions = Exclusions(inventory);
        using var policy = DesiredRevisionPolicyStore.For(process).Acquire(exclusions, token);
        using var capture = new ApprovedPayloadStore(Root, process, logger).Open(reference, exclusions, token);
        capture.RequireTarget(target, process, token);
        return policy.Set(target, new("approved-immutable", ApprovalId: reference.Id, ApprovalFingerprint: reference.Fingerprint), expectedPolicy, token, capture);
    }

    internal sealed class ReadLease(ApprovedPayloadStore store, NativeStateDirectory state, FileStream writer,
        NativeStateDirectory capture, ApprovedPayloadHeader header, IReadOnlyList<string> exclusions) : IDisposable
    {
        internal ApprovedPayloadHeader Header => header;
        internal ApprovedPayloadReference Reference => new(state.CanonicalPath, header.Id, header.Fingerprint);
        internal void Verify(CancellationToken token)
        {
            store.CheckBinding(state, exclusions, token);
            var current = ReadHeader(capture, token);
            if (current.Fingerprint != header.Fingerprint || Hash(current) != header.Fingerprint)
                throw new IOException("Approval header changed while held.");
            using var payload = OpenChild(capture, "payload", token);
            var loaded = CapturedPayloadArchive.Load(payload, header.ArchiveFingerprint, token);
            Validate(header, loaded.Archive, loaded.Archive.OpenReader(payload), capture, token);
            store.CheckBinding(state, exclusions, token);
        }
        internal void RequireTarget(InstalledSkill target, ProcessRunner process, CancellationToken token)
        {
            Verify(token);
            if (DesiredRevisionPolicyStore.Bind(target) != header.Binding || target.Name != header.Subject.Name
                || target.Validity != ValidityState.Valid || !target.FrontMatter.Diagnostics.IsEmpty
                || !target.MetadataConflicts.IsEmpty || !target.OwnershipDiagnostics.IsEmpty
                || process.CaptureContextFingerprint() != header.ContextFingerprint)
                throw new IOException("Approval source, installation, owner namespace or process context differs; explicit remapping/review is required.");
        }
        internal void RequirePolicy(InstalledSkill target, DesiredRevisionPolicy policy, CancellationToken token)
        {
            Verify(token);
            if (DesiredRevisionPolicyStore.Bind(target) != header.Binding || policy.ApprovalId != header.Id
                || policy.ApprovalFingerprint != header.Fingerprint
                || target.Name != header.Subject.Name || target.FrontMatter.Name != header.Subject.Metadata.Name
                || store.CurrentContextFingerprint != header.ContextFingerprint)
                throw new IOException("Desired policy is not bound to this held completed approval.");
        }
        internal void CopyInto(NativeStateDirectory operation, CancellationToken token)
        {
            Verify(token);
            using (var payload = OpenChild(capture, "payload", token))
                CapturedPayloadArchive.CopyInto(payload, operation, "approved-payload", header.ArchiveFingerprint, token);
            if (header.Selected is { } selected)
            {
                using (var source = OpenChild(capture, "selected-source-capture", token))
                    CapturedPayloadArchive.CopyInto(source, operation, "approved-selected-source", selected.Provider.SourceArchiveFingerprint, token);
                using (var provider = OpenChild(capture, "selected-gh-capture", token))
                    CapturedPayloadArchive.CopyInto(provider, operation, "approved-selected-provider", selected.Provider.OutputArchiveFingerprint, token);
            }
            store.CheckBinding(state, exclusions, token);
        }
        public void Dispose() { capture.Dispose(); writer.Dispose(); state.Dispose(); }
    }

    internal static void Validate(ApprovedPayloadHeader header, CapturedPayloadArchive payload,
        CapturedPayloadArchive.Reader reader, NativeStateDirectory capture, CancellationToken token, bool savedOperation = false)
    {
        if (header.SchemaVersion != 1 || !QuarantineStore.ValidId(header.Id) || header.Fingerprint != Hash(header)
            || !QuarantineStore.ValidHash(header.ContextFingerprint) || !QuarantineStore.ValidHash(header.ArchiveFingerprint)
            || header.Subject is null || header.Binding is null || header.Captured is null || header.Metadata.IsDefault
            || header.SourceReviewContract != SourceReviewContract || header.Subject.Metadata is null
            || !header.Subject.Metadata.Diagnostics.IsEmpty || header.Subject.Agents.IsDefault || header.Subject.Owners.IsDefault
            || string.IsNullOrWhiteSpace(header.Subject.Name) || !QuarantineStore.ValidHash(header.Subject.InstallationId)
            || !Path.IsPathFullyQualified(header.Binding.Path) || header.Binding.Source is null || header.Binding.Owners is null
            || header.Subject.Agents.Any(agent => agent is null) || header.Subject.Owners.Any(owner => owner is null)
            || header.Kind != (header.Selected is null ? "captured-custom" : "verified-selected-result")
            || header.Binding.InstallationId != header.Subject.InstallationId || header.Binding.Path != payload.Snapshot.Path)
            throw new IOException("Completed approval schema, source review contract or installed binding is inconsistent.");
        var subject = new InstalledSkill
        {
            Name = header.Subject.Name,
            ResolvedPath = header.Binding.Path,
            ScanRoot = Path.GetDirectoryName(header.Binding.Path)!,
            Scope = header.Subject.Scope,
            Agents = header.Subject.Agents,
            FrontMatter = header.Subject.Metadata,
            OwnershipClaims = header.Subject.Owners,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = header.Subject.Agents.Any(agent => agent.IsSymlink),
            InstalledAt = null
        };
        if (DesiredRevisionPolicyStore.Bind(subject) != header.Binding)
            throw new IOException("Approval source/owner namespace does not match its observed installed subject.");
        if ((header.OwnerCapture is null) != header.Subject.Owners.IsEmpty)
            throw new IOException("Approved owned payload requires its actual held native registry observation.");
        if (header.OwnerCapture is { } owner)
        {
            if (!Path.IsPathFullyQualified(owner.RegistryPath) || owner.RegistryPath != owner.Identity.CanonicalPath
                || owner.Identity.IsDirectory || owner.Identity.IsReparsePoint || !owner.ParentIdentity.IsDirectory || owner.ParentIdentity.IsReparsePoint
                || owner.ParentIdentity.CanonicalPath != Path.GetDirectoryName(owner.RegistryPath)
                || string.IsNullOrWhiteSpace(owner.Selector) || !QuarantineStore.ValidHash(owner.EntryFingerprint)
                || owner.Entry is not { Length: > 0 } || owner.Entry.Length > SkillLockFileReader.MaxFileBytes
                || header.Subject.Owners.Any(claim => claim.Provider != "gh-vercel-global" || claim.ReadOnly || claim.IsExclusiveOwner
                    || claim.LockFilePath is null || !Path.IsPathFullyQualified(claim.LockFilePath)
                    || claim.Selector != owner.Selector || claim.EntryFingerprint != owner.EntryFingerprint))
                throw new IOException("Approval native registry/parent/raw-entry witness is inconsistent.");
            var entry = GhRegistryEditor.CaptureEntry(OwnerRegistry(owner.Selector, owner.Entry), owner.Selector, token);
            if (entry.Fingerprint != owner.EntryFingerprint) throw new IOException("Captured owner raw entry differs from its fingerprint.");
        }
        if (!savedOperation)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "payload", "approved.json" };
            if (header.Selected is not null) { allowed.Add("selected-source-capture"); allowed.Add("selected-gh-capture"); }
            var names = capture.EnumerateEntries(token).Take(6).ToArray();
            if (names.Any(entry => !allowed.Contains(entry.Name) || entry.Name == "approved.json" && entry.Error is not null) || names.Length > allowed.Count)
                throw new IOException("Approval capture contains unknown or excess retained evidence.");
            foreach (var entry in names.Where(entry => entry.Name != "approved.json"))
            { using var directory = OpenChild(capture, entry.Name, token); directory.ValidateLocation(token); }
        }
        if (!ApprovedPayloadJsonContext.Canonical(header.Captured, ReplacementJsonContext.Default.ReplacementEvidence).AsSpan()
            .SequenceEqual(ApprovedPayloadJsonContext.Canonical(ReplacementEvidence.From(payload.Snapshot), ReplacementJsonContext.Default.ReplacementEvidence)))
            throw new IOException("Approval native evidence differs from its exact captured archive.");
        using var metadata = PayloadMetadataReview.PreserveInstalled(payload, reader, token);
        if (header.MetadataPolicy != metadata.PolicyName || !ApprovedPayloadJsonContext.Canonical(header.Metadata,
            ApprovedPayloadJsonContext.Default.ImmutableArrayPayloadMetadataRecipe).AsSpan().SequenceEqual(
            ApprovedPayloadJsonContext.Canonical(metadata.Recipes, ApprovedPayloadJsonContext.Default.ImmutableArrayPayloadMetadataRecipe)))
            throw new IOException("Approval metadata coverage or preservation recipe changed.");
        var bytes = GhLocalImportStager.ReadSkill(reader, token);
        var (_, parsed, valid) = FrontMatterParser.Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes));
        if (!valid || !parsed.Diagnostics.IsEmpty || !ApprovedPayloadJsonContext.Canonical(parsed, ReplacementJsonContext.Default.SkillFrontMatter).AsSpan()
            .SequenceEqual(ApprovedPayloadJsonContext.Canonical(header.Subject.Metadata, ReplacementJsonContext.Default.SkillFrontMatter)))
            throw new IOException("Approval authored/tracking metadata differs from captured SKILL.md.");
        foreach (var entry in payload.Entries.Where(item => item.Blob is not null)) reader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        if (header.Selected is { } selected) ValidateSelected(selected, payload, reader, capture, savedOperation, token);
    }

    private static void ValidateSelected(ApprovedSelectedResult selected, CapturedPayloadArchive payload,
        CapturedPayloadArchive.Reader reader, NativeStateDirectory capture, bool savedOperation, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(selected.PlanStoreAddress) || !QuarantineStore.ValidId(selected.PlanId) || !QuarantineStore.ValidHash(selected.PlanFingerprint)
            || selected.Provider is not { Selection: { Request: not null } } provider
            || !QuarantineStore.ValidHash(provider.BinarySha256) || !Path.IsPathFullyQualified(provider.BinaryPath)
            || !QuarantineStore.ValidHash(provider.SourceArchiveFingerprint) || !QuarantineStore.ValidHash(provider.OutputArchiveFingerprint)
            || !QuarantineStore.ValidHash(provider.ContextFingerprint) || provider.EnvironmentPolicy != GhPinnedRevisionStager.EnvironmentPolicy
            || provider.Version is null || provider.Version.Length > 4096 || provider.StdOut is null || provider.StdErr is null
            || provider.StdOut.Length > 64 * 1024 + 100 || provider.StdErr.Length > 64 * 1024 + 100
            || provider.GitEntries.IsDefault || provider.Arguments.IsDefault || provider.Arguments.Length != 8
            || !Path.IsPathFullyQualified(provider.Arguments[3])
            || provider.Selection.Commit is not { Length: 40 } || !provider.Selection.Commit.All(char.IsAsciiHexDigit)
            || provider.Selection.Tree is not { Length: 40 } || !provider.Selection.Tree.All(char.IsAsciiHexDigit)
            || provider.Selection.Repository is null || provider.Selection.Subpath is null
            || selected.InstalledPayload != payload.Snapshot.Fingerprint) throw new IOException("Selected approval lacks its completed installed-result proof.");
        provider.Selection.Request.Validate();
        var version = provider.Version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (version.Length < 3 || version[0] != "gh" || version[1] != "version" || !SkillView.Gh.GhBinaryLocator.SatisfiesMinimum(version[2])
            || !provider.Arguments.SequenceEqual(GhPinnedRevisionStager.BuildArguments(provider.Arguments[3], provider.Selection))
            || provider.Selection.Ref != (provider.Selection.Request.Kind == "tag" ? "refs/tags/" + provider.Selection.Request.Value : null)
            || provider.Selection.Request.Kind == "commit" && provider.Selection.Commit != provider.Selection.Request.Selector)
            throw new IOException("Approved selected provider/request evidence is inconsistent.");
        using var raw = OpenChild(capture, savedOperation ? "approved-selected-source" : "selected-source-capture", token);
        var source = CapturedPayloadArchive.Load(raw, selected.Provider.SourceArchiveFingerprint, token).Archive;
        GhRecordedTreeStager.VerifyArchiveTree(source, source.OpenReader(raw), selected.Provider.GitEntries, selected.Provider.Selection.Tree, token);
        using var output = OpenChild(capture, savedOperation ? "approved-selected-provider" : "selected-gh-capture", token);
        var providerArchive = CapturedPayloadArchive.Load(output, selected.Provider.OutputArchiveFingerprint, token).Archive;
        var outputReader = providerArchive.OpenReader(output);
        GhPinnedRevisionStager.ValidateOutput(source.Snapshot, source.OpenReader(raw), providerArchive.Snapshot, outputReader,
            selected.Provider.Selection, payload.Snapshot.FrontMatter!.Name!, token);
        var expectedSkill = GhLocalImportStager.ReadSkill(outputReader, token);
        if (!selected.Provider.Selection.Request.Pin) expectedSkill = PinTrackingWriter.Unpin(expectedSkill);
        if (!expectedSkill.AsSpan().SequenceEqual(GhLocalImportStager.ReadSkill(reader, token))
            || payload.Snapshot.RootUnixMode != providerArchive.Snapshot.RootUnixMode
            || !payload.Snapshot.Entries.Where(entry => entry.Path != "SKILL.md").SequenceEqual(providerArchive.Snapshot.Entries.Where(entry => entry.Path != "SKILL.md"))
            || reader.Entry("SKILL.md").UnixMode != outputReader.Entry("SKILL.md").UnixMode)
            throw new IOException("Approved installed bytes are not the actual immutable selected result.");
    }

    internal static string Hash(ApprovedPayloadHeader header) => Convert.ToHexStringLower(SHA256.HashData(
        ApprovedPayloadJsonContext.Canonical(header with { Fingerprint = "" }, ApprovedPayloadJsonContext.Default.ApprovedPayloadHeader)));
    private static byte[] OwnerRegistry(string selector, byte[] entry)
    {
        using var bytes = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 3); writer.WriteStartObject("skills");
            writer.WritePropertyName(selector); writer.WriteRawValue(entry); writer.WriteEndObject(); writer.WriteEndObject();
        }
        return bytes.ToArray();
    }
    private static ApprovedPayloadHeader ReadHeader(NativeStateDirectory capture, CancellationToken token)
    {
        using var file = capture.OpenExistingFile("approved.json", false, token);
        return BoundedReviewJson.Read(file, ApprovedPayloadJsonContext.Default.ApprovedPayloadHeader, MaxHeaderBytes, token).Value;
    }
    private static void CheckWriter(FileStream writer) { if (writer.Length != 0) throw new IOException("Approval writer must be empty."); }
    private static (string Name, long Bytes, string? Error)[] Entries(NativeStateDirectory state, CancellationToken token)
    {
        var entries = state.EnumerateEntries(token).Take(MaxCaptures + 2).ToArray();
        if (entries.Length > MaxCaptures + 1 || entries.Any(entry => entry.Name == "writer.lock" ? entry.Error is not null : !QuarantineStore.ValidId(entry.Name)))
            throw new IOException("Approval store has incomplete/unknown namespace or exceeds its capture budget.");
        // The metadata enumerator reports directories as non-regular leaves.
        // Admit only known capture names, then prove their native directory
        // type/address by opening them; never classify an error string.
        foreach (var entry in entries.Where(entry => entry.Name != "writer.lock"))
        { using var directory = OpenChild(state, entry.Name, token); directory.ValidateLocation(token); }
        return entries;
    }
    internal static NativeStateDirectory OpenChild(NativeStateDirectory parent, string name, CancellationToken token) =>
        parent.OpenExistingDirectory(name, token);
    internal static string[] Exclusions(InventorySnapshot inventory) => inventory.ScannedRoots.Concat(inventory.ConfiguredRoots)
        .Select(root => OwnershipPath.CanonicalCandidate(root.Path) ?? Path.GetFullPath(root.Path)).Distinct(StringComparer.Ordinal).ToArray();
    private void CheckBinding(NativeStateDirectory held, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        using var current = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
        using var before = held.CaptureOwnership(token); using var after = current.CaptureOwnership(token);
        var identity = before.Capture(token).Identity;
        if (identity != after.Capture(token).Identity || before.Capture(token).Identity != identity)
            throw new IOException("Approval original configured address no longer identifies its held native store.");
    }
}
