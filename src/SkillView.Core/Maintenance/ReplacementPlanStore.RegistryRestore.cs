using System.Collections.Immutable;
using System.Text;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    internal const string RegistryRestorePurpose = "restore-registry-recorded";

    // This is source selection from an attached nonexclusive record, never proof
    // that this tree was installed at this address. Keep Subject(plan) observed.
    private static InstalledSkill ExecutionSubject(ReplacementPlan plan) => plan.RegistryRestore is { } restore
        ? Subject(plan) with { FrontMatter = restore.DesiredMetadata, Agents = plan.Subject.Agents.AddRange(restore.ExpectedCatalogMemberships).Distinct().ToImmutableArray() }
        : Subject(plan);

    // gh omits missing-SKILL rows, then reports catalog memberships once
    // valid metadata exists. Bind possible memberships from its placement
    // contract before review, separately from the observed damaged Subject.
    // These never add an address or attest installed/enabled agent state.
    private ImmutableArray<AgentMembership> RegistryCatalogMemberships(InstalledSkill target, CancellationToken token)
    {
        var process = runner ?? throw new IOException("Registry restore needs the captured child context.");
        var parent = Path.GetDirectoryName(target.ResolvedPath)!;
        var result = ImmutableArray.CreateBuilder<AgentMembership>();
        foreach (var entry in GhPlacementCatalog.Entries)
        {
            token.ThrowIfCancellationRequested();
            var roots = GhPlacementCatalog.DestinationsAsync(new GhSkillInstallService.Options(Scope: "user",
                Agents: [entry.Id], WorkingDirectory: process.EffectiveWorkingDirectory), process, token).GetAwaiter().GetResult();
            if (roots.Any(root => OwnershipPath.Same(root, parent)))
                result.Add(new(AgentIdentity.Normalize(entry.Id), target.ResolvedPath, false, Scope.User));
        }
        return result.Distinct().OrderBy(agent => agent.AgentId, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static string RegistryRestoreNotice(string name, bool corroborated) =>
        $"Replace with the version recorded in gh’s registry for {name}; the registry does not record which folder it belongs to. "
        + (corroborated ? "Surviving entries exactly corroborate the candidate bytes and modes. " : "No surviving content corroborates this placement. ")
        + "The raw damaged original is retained for exact Undo. Every listed consumer is affected; running sessions are unknown. "
        + "For an unpinned record, the historical ref is unknown and remains absent. SkillView update/version choice remains unsupported; remove and reinstall where admitted.";

    internal static bool IsRegistryRestoreSubject(InstalledSkill target) =>
        target.Validity is ValidityState.MissingSkillMd or ValidityState.UnparsableFrontMatter or ValidityState.InvalidMetadata;

    private static SkillPackage RegistryClaim(InstalledSkill target)
    {
        var claims = target.AllOwnershipClaims.ToArray();
        if (claims.Length != 1 || claims[0] is not
            {
                Provider: "gh-vercel-global", SchemaVersion: 3,
                ReadOnly: false, IsExclusiveOwner: false, Diagnostic: null, HashKind: "recorded-git-tree", SourceType: "github"
            } claim
            || claim.ContentHash is not { Length: 40 } tree || !tree.All(char.IsAsciiHexDigit)
            || claim.Selector is not { Length: > 0 } selector || selector != Path.GetFileName(target.ResolvedPath)
            || selector.Contains('/') || selector.Contains('\\') || selector.Any(char.IsControl)
            || claim.SkillPath is not { } raw || !raw.EndsWith("/SKILL.md", StringComparison.Ordinal)
            || claim.EntryFingerprint is null || claim.RecordFingerprint is null || claim.LockFilePath is null || claim.CoveredRoots.IsDefault
            || !claim.Describes(target))
            throw new IOException("Registry restore requires exactly one already-attached compatible schema-3 gh Git-tree claim for this ordinary copy. A global same-name record cannot select a folder.");
        return claim;
    }

    private static SkillFrontMatter RegistryMetadata(InstalledSkill target, ResolvedSkillRevision? pin)
    {
        var claim = RegistryClaim(target);
        var repository = claim.SourceUrl ?? "https://github.com/" + claim.Source;
        if (GhRevisionResolver.Repository(repository) != claim.Source)
            throw new IOException("Registry repository evidence disagrees.");
        // Strip only the exact terminal filename; retain the raw path in Owners.
        var path = SkillSourceIdentity.NormalizeSubpath(claim.SkillPath![..^9])
            ?? throw new IOException("Registry source path is unsupported.");
        if (path.Length == 0 || path.Length > 4096 || path.Split('/').Length > 16
            || path.Split('/').Any(part => part is "" or "." or ".." or ".git" || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl)))
            throw new IOException("Registry source path is unsupported.");
        return new()
        {
            Name = claim.Selector,
            Upstream = repository,
            GithubPath = path,
            GithubTreeSha = claim.ContentHash,
            GithubRef = pin?.Ref,
            Pinned = claim.PinnedRef is not null,
            PinnedRef = claim.PinnedRef
        };
    }

    private static void RequireRegistryTracking(InstalledSkill target, ResolvedSkillRevision? pin)
    {
        var expected = RegistryMetadata(target, pin);
        SameTracking(expected, target.FrontMatter);
        if (target.FrontMatter.Name != expected.Name || !target.FrontMatter.Diagnostics.IsEmpty)
            throw new IOException("Registry candidate name must equal the raw selector and physical leaf.");
        if (expected.PinnedRef is null)
        {
            if (pin is not null || target.FrontMatter.GithubRef is not null)
                throw new IOException("Unpinned registry restore requires an absent historical ref; no ref may be guessed.");
        }
        else if (pin is null || pin.Tree != expected.GithubTreeSha
            || pin.Repository != expected.Upstream || pin.Subpath != expected.GithubPath
            || pin.Ref is not { Length: > 10 } reference || !reference.StartsWith("refs/tags/", StringComparison.Ordinal)
            || reference[10..] != expected.PinnedRef || pin.Commit is not { Length: 40 } commit || !commit.All(char.IsAsciiHexDigit))
            throw new IOException("The resolved exact pin must independently identify the registry-recorded subtree.");
    }

    private static bool Corroborate(ReplacementEvidence original, ReplacementEvidence candidate)
    {
        var surviving = original.Entries.Where(entry => entry.Path != "SKILL.md").ToArray();
        if (original.Entries.Any(entry => entry.Kind == "link") || candidate.Entries.Any(entry => entry.Kind == "link")
            || original.Entries.Any(entry => entry.Path.EndsWith("/SKILL.md", StringComparison.Ordinal)))
            throw new IOException("Registry restore supports ordinary nonnested copies without payload links only.");
        foreach (var entry in surviving)
        {
            var match = candidate.Entries.SingleOrDefault(other => other.Path == entry.Path);
            if (match is null || match.Kind != entry.Kind || match.ContentHash != entry.ContentHash
                || match.Bytes != entry.Bytes || match.UnixMode != entry.UnixMode || match.LinkTarget != entry.LinkTarget)
                throw new IOException("Surviving payload does not exactly corroborate the registry candidate bytes and modes: " + entry.Path);
        }
        return surviving.Any(entry => entry.Kind == "file");
    }

    private void ValidateRegistryRestore(ReplacementPlan plan, CancellationToken token, bool verifyCatalogContext = true)
    {
        token.ThrowIfCancellationRequested();
        if (plan.Purpose != RegistryRestorePurpose)
        {
            if (plan.RegistryRestore is not null) throw new IOException("Registry policy is forbidden for ordinary saved purposes.");
            return;
        }
        var restore = plan.RegistryRestore ?? throw new IOException("Missing registry restore policy.");
        static bool MetadataShape(SkillFrontMatter? metadata) => metadata is not null
            && metadata.Metadata is not null && metadata.Extra is not null
            && !metadata.Diagnostics.IsDefault && !metadata.AllowedTools.IsDefault && !metadata.Agents.IsDefault;
        if (!MetadataShape(restore.DesiredMetadata) || !MetadataShape(plan.Subject.Metadata)
            || plan.Subject.Scope is not (Scope.Project or Scope.User or Scope.Custom) || restore.ObservedValidity is not (ValidityState.MissingSkillMd or ValidityState.UnparsableFrontMatter or ValidityState.InvalidMetadata)
            || plan.SourceReview is null || plan.Subject.Agents.Any(agent => agent.IsSymlink)
            || plan.Original.Entries.Any(entry => Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json" or ".git")
            || plan.Candidate.Entries.Any(entry => Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json" or ".git"))
            throw new IOException("Registry restore policy, source review or ordinary topology is invalid.");
        if (restore.ExpectedCatalogMemberships.IsDefault
            || restore.ExpectedCatalogMemberships.Any(agent => agent is null
                || agent != new AgentMembership(agent.AgentId, plan.OriginalPath, false, Scope.User)
                || !GhPlacementCatalog.Entries.Any(entry => entry.Id == agent.AgentId))
            || !restore.ExpectedCatalogMemberships.Select(agent => agent.AgentId).SequenceEqual(
                restore.ExpectedCatalogMemberships.Select(agent => agent.AgentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || verifyCatalogContext && !restore.ExpectedCatalogMemberships.SequenceEqual(RegistryCatalogMemberships(Subject(plan), token)))
            throw new IOException("The possible gh catalog memberships or captured placement context changed.");
        RequireRegistryTracking(ExecutionSubject(plan), restore.ResolvedPin);
        if (restore.ObservedValidity == ValidityState.InvalidMetadata) SameTracking(plan.Subject.Metadata, restore.DesiredMetadata);
        if (restore.HasCorroboration != Corroborate(plan.Original, plan.Candidate))
            throw new IOException("Registry corroboration evidence disagrees.");
    }

    private static void ValidateRegistryArchivePolicy(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.RegistryRestore is null) return;
        var review = plan.SourceReview ?? throw new IOException("Registry restore requires source review archives.");
        using var beforeState = OpenArchive(operation, review.InstalledArchive, token);
        using var afterState = OpenArchive(operation, review.CandidateArchive, token);
        var before = CapturedPayloadArchive.Load(beforeState, review.InstalledManifest, token).Archive;
        var after = CapturedPayloadArchive.Load(afterState, review.CandidateManifest, token).Archive;
        ValidateRegistryCandidateArchive(plan, after, afterState, token);
        ValidateRegistryInstalledArchive(plan, before, beforeState, token);
    }

    // Retirement reuses each half only after loading that surviving archive
    // against its immutable manifest. Marker JSON supplies no metadata authority.
    private static void ValidateRegistryCandidateArchive(ReplacementPlan plan, CapturedPayloadArchive archive,
        NativeStateDirectory state, CancellationToken token)
    {
        var restore = plan.RegistryRestore ?? throw new IOException("Missing registry restore policy.");
        RequireRegistryArchiveMetadata(archive);
        var (desired, parsed) = ReadRegistryArchiveMetadata(archive, state, token);
        if (!parsed || !desired.Diagnostics.IsEmpty || !SameParsedMetadata(restore.DesiredMetadata, desired)
            || desired.Name != plan.Subject.Name)
            throw new IOException("Saved desired metadata is not the parsed immutable candidate.");
    }

    private static void ValidateRegistryInstalledArchive(ReplacementPlan plan, CapturedPayloadArchive archive,
        NativeStateDirectory state, CancellationToken token)
    {
        var restore = plan.RegistryRestore ?? throw new IOException("Missing registry restore policy.");
        RequireRegistryArchiveMetadata(archive);
        var missing = !archive.Entries.Any(item => item.Entry.Path == "SKILL.md");
        var (observed, parsed) = missing ? (SkillFrontMatter.Empty, false) : ReadRegistryArchiveMetadata(archive, state, token);
        var validityMatches = restore.ObservedValidity switch
        {
            ValidityState.MissingSkillMd => missing,
            ValidityState.UnparsableFrontMatter => !missing && !parsed,
            ValidityState.InvalidMetadata => !missing && parsed && !observed.Diagnostics.IsEmpty && observed.Name == restore.DesiredMetadata.Name,
            _ => false
        };
        if (!validityMatches)
            throw new IOException("Saved original validity disagrees with the raw immutable damaged archive.");
        if (!SameParsedMetadata(plan.Subject.Metadata, observed))
            throw new IOException("Saved observed metadata disagrees with the raw damaged original.");
    }

    private static (SkillFrontMatter Metadata, bool Parsed) ReadRegistryArchiveMetadata(CapturedPayloadArchive archive,
        NativeStateDirectory state, CancellationToken token)
    {
        var entry = archive.Entries.SingleOrDefault(item => item.Entry.Path == "SKILL.md");
        if (entry is null || entry.Entry.Kind != "file" || entry.Entry.Bytes > PayloadSnapshot.MaxSkillBytes)
            throw new IOException("Registry archive root metadata is missing, nonregular or exceeds its budget.");
        using var bytes = new MemoryStream();
        archive.CopyEntry(state, "SKILL.md", bytes, token);
        try
        {
            var (_, metadata, valid) = FrontMatterParser.Parse(new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)));
            return (metadata, valid);
        }
        catch (DecoderFallbackException) { return (SkillFrontMatter.Empty, false); }
    }

    private static void RequireRegistryArchiveMetadata(CapturedPayloadArchive archive)
    {
        foreach (var metadata in archive.Entries.Select(entry => entry.Metadata!).Prepend(archive.RootMetadata!))
            if (!metadata.Supported)
                throw new IOException("Registry restore requires complete native metadata coverage: " + string.Join(" ", metadata.Refusals));
    }

    private static void VerifyRegistryNative(ReplacementPlan plan, NativeStateDirectory operation, string topology,
        ReplacementSlot? original, ReplacementSlot? retained, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.RegistryRestore is null) return;
        var review = plan.SourceReview ?? throw new IOException("Registry restore requires immutable source review.");
        Verify(review.InstalledArchive, review.InstalledManifest, topology is "ready" or "restored" ? original! : retained!);
        Verify(review.CandidateArchive, review.CandidateManifest,
            topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!);

        void Verify(string name, string fingerprint, ReplacementSlot slot)
        {
            using var saved = OpenArchive(operation, name, token);
            var archive = CapturedPayloadArchive.Load(saved, fingerprint, token).Archive;
            RequireRegistryArchiveMetadata(archive);
            using var authority = PayloadMetadataAuthority.Open(saved, create: false, token);
            var metadata = archive.Entries.ToDictionary(entry => entry.Entry.Path, entry => entry.Metadata!, StringComparer.Ordinal);
            using var lease = NativeDirectoryMoveLease.OpenRegistryDamaged(PathIdentity.IsInside(slot.Path, operation.CanonicalPath)
                ? operation.CanonicalPath : plan.RootPath, slot.Path, token);
            var captured = lease.CaptureRawInto((item, _) => Check(item), Check, token);
            if (captured.Fingerprint != archive.Snapshot.Fingerprint)
                throw new IOException("Registry restore payload differs from its immutable archive.");
            void Check(NativePayloadItem item)
            {
                var expected = item.RelativePath.Length == 0 ? archive.RootMetadata! : metadata[item.RelativePath];
                var current = PayloadNativeMetadata.Capture(item, token, authority);
                if (!SameLocalMetadata(expected, current) || expected.LinkCount != current.LinkCount
                    || expected.EnumerationCoverage != current.EnumerationCoverage || !current.Refusals.SequenceEqual(expected.Refusals))
                    throw new IOException("Registry restore native metadata changed: " + item.RelativePath);
            }
        }
    }

    private static void BindDamagedMetadata(NativeDirectoryMoveLease original, PayloadSnapshot before,
        InstalledSkill target, SkillFrontMatter desired, CancellationToken token)
    {
        byte[]? bytes = null;
        var captured = original.CaptureRawInto((item, entry) =>
        {
            if (entry.Path != "SKILL.md") return;
            if (entry.Kind != "file" || entry.Bytes > PayloadSnapshot.MaxSkillBytes)
                throw new IOException("Damaged SKILL.md is not bounded ordinary metadata.");
            item.Content!.Position = 0;
            bytes = new byte[checked((int)entry.Bytes)];
            item.Content.ReadExactly(bytes);
            if (item.Content.ReadByte() != -1
                || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != entry.ContentHash)
                throw new IOException("Damaged metadata changed during held capture.");
        }, null, token);
        if (captured.Fingerprint != before.Fingerprint || captured.ObjectStateFingerprint != before.ObjectStateFingerprint)
            throw new IOException("Damaged original changed before planning.");
        if (target.Validity == ValidityState.MissingSkillMd)
        {
            if (bytes is not null) throw new IOException("SKILL.md is no longer missing; refresh.");
            return;
        }
        if (bytes is null) throw new IOException("Damaged SKILL.md disappeared; refresh.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) when (target.Validity == ValidityState.UnparsableFrontMatter) { return; }
        var (_, metadata, parsed) = FrontMatterParser.Parse(text);
        if (target.Validity == ValidityState.UnparsableFrontMatter)
        {
            if (parsed) throw new IOException("Frontmatter is no longer unparsable; refresh.");
            return;
        }
        if (!parsed || metadata.Diagnostics.IsEmpty || metadata.Name != Path.GetFileName(target.ResolvedPath))
            throw new IOException("Invalid metadata must retain a parsed matching name and exact registry tracking.");
        SameTracking(metadata, desired);
        SameTracking(target.FrontMatter, metadata);
    }

    internal async Task<ReplacementResult> PlanRegistryRestoreAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete
            || !IsRegistryRestoreSubject(target) || target.IsSymlinked || target.Agents.Any(agent => agent.IsSymlink)
            || !target.MetadataConflicts.IsEmpty || !target.OwnershipDiagnostics.IsEmpty
            || inventory.Skills.Count(skill => PathIdentity.Equals(skill.ResolvedPath, target.ResolvedPath)) != 1)
            throw new IOException("Select one exact ordinary missing/corrupt copy from fresh inventory; name mismatches and ambiguous copies are unsupported.");
        var process = runner ?? throw new IOException("Registry restore requires a captured child context.");
        RequireHistoryCapacity(inventory, token);
        ManagedSkillDiscovery.RefuseTarget(target, token, process);
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => PathIdentity.Equals(Path.GetDirectoryName(canonical)!, path)).Distinct(StringComparer.Ordinal).SingleOrDefault()
            ?? throw new IOException("Registry restore requires an ordinary direct child of one captured scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        var claim = RegistryClaim(target);
        var desired = RegistryMetadata(target, null);
        ResolvedSkillRevision? pin = null;
        if (claim.PinnedRef is { } tag)
        {
            pin = await new GhRevisionResolver(process, ghPath).ResolveTagAsync(desired.Upstream!, desired.GithubPath, tag, token).ConfigureAwait(false);
            desired = RegistryMetadata(target, pin);
        }
        var expectedCatalog = RegistryCatalogMemberships(target, token);
        var authority = target with
        {
            Validity = ValidityState.Valid,
            FrontMatter = desired,
            Agents = target.Agents.AddRange(expectedCatalog).Distinct().ToImmutableArray()
        };
        RequireRegistryTracking(authority, pin);
        inventory = Admit(authority, inventory, token, registryRestore: true, registryPin: pin);
        CheckPlacement(inventory, token);
        using var original = NativeDirectoryMoveLease.OpenRegistryDamaged(containing, canonical, token);
        var before = original.Capture(token);
        if (before.Entries.Any(entry => Path.GetFileName(entry.Path) is ".git" or ".skill-lock.json" or "skills-lock.json"))
            throw new IOException("Nested Git/owner metadata is unsupported by registry restore.");
        BindDamagedMetadata(original, before, target, desired, token);
        var context = Context(authority, inventory, token);
        beforeStateOpenForTests?.Invoke();
        CheckPlacement(inventory, token);
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
        using var writer = state.OpenFile("writer.lock", true, token); CheckWriter(writer); CheckPlanCapacity(state, token);
        var id = Guid.NewGuid().ToString("N");
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, id), Exclusions(inventory), token);
        using var operationWriter = operation.OpenFile("writer.lock", true, token); CheckWriter(operationWriter);
        try
        {
            using (var staging = operation.CreateStagingDirectory("candidate", token))
            {
                await new GhRecordedTreeStager(process).StageIntoAsync(ghPath, desired.Upstream!, desired.GithubTreeSha!, staging, bytes =>
                {
                    if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Staged SKILL.md exceeds its metadata budget.");
                    return Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(new UTF8Encoding(false, true).GetString(bytes), desired));
                }, token).ConfigureAwait(false);
                staging.SealStagingDirectory(493, token);
            }
            using var candidate = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "candidate"), token);
            var after = candidate.FlushPayload(token);
            var staged = PayloadSnapshot.Capture(after.Path, token);
            if (staged.FrontMatter is null || staged.Fingerprint != after.Fingerprint || staged.ObjectStateFingerprint != after.ObjectStateFingerprint)
                throw new IOException("Candidate metadata is not bound to held payload.");
            RequireRegistryTracking(authority with { FrontMatter = staged.FrontMatter }, pin);
            var corroboration = Corroborate(ReplacementEvidence.From(before), ReplacementEvidence.From(after));
            original.Revalidate(token);
            inventory = Admit(authority, inventory, token, registryRestore: true, registryPin: pin);
            var current = original.Capture(token);
            if (current.Fingerprint != before.Fingerprint || current.ObjectStateFingerprint != before.ObjectStateFingerprint
                || Context(authority, inventory, token) != context)
                throw new IOException("Damaged original, owner or consumer context changed during staging.");
            var subject = new ReplacementSubject(target.InstallationId, target.Name, target.Scope, target.Agents,
                target.FrontMatter, target.AllOwnershipClaims.ToImmutableArray());
            var plan = new ReplacementPlan(1, id, RegistryRestorePurpose, DateTimeOffset.UtcNow, canonical,
                original.RootIdentity.CanonicalPath, original.RootIdentity, original.ParentIdentity, subject, context,
                ReplacementEvidence.From(before), ReplacementEvidence.From(after), PayloadSnapshot.Differences(before, after, false)
                    .AddRange(before.RootUnixMode != after.RootUnixMode ? new string[] { "/" } : Array.Empty<string>()), "",
                RegistryRestore: new(target.Validity, staged.FrontMatter, pin, corroboration, expectedCatalog));
            // This purpose always needs the two independent approvals, even for a
            // store opened by a legacy caller without product review defaults.
            plan = FreezeSourceReview(plan, operation, original, candidate, token);
            plan = plan with { ReviewFingerprint = PlanHash(plan) };
            ValidateRegistryRestore(plan, token);
            ValidateRegistryArchivePolicy(plan, operation, token);
            Write(operation, "plan.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
            return Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
                "Saved one immutable registry-recorded candidate. Independent source and retention approvals are required.");
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        { throw new OperationCanceledException($"Registry restore preparation canceled; private evidence retained at {operation.CanonicalPath}. Inspect History before retrying.", ex, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw StagingFailure($"Registry restore preparation refused; installation unchanged. Private evidence retained at {operation.CanonicalPath}. {ex.Message}", ex); }
    }
}
