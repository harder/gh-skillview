using System.Collections.Immutable;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    internal const string LocalReimportPurpose = "local-reimport";
    internal const string LocalReimportDisposition = "replace-local-retain-original";
    internal const long LocalImportWorkingBytes = 4L * PayloadSnapshot.MaxBytes;
    private string LocalPreviewRoot => Root + "-local-previews";
    private static readonly string[] LocalArchiveNames = ["installed-capture", "local-source-capture", "gh-input-capture", "gh-output-capture"];

    internal Task<ReplacementPreparation> PlanLocalReimportAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, CancellationToken token) => PlanLocalImportAsync(ghPath, target, null, inventory, token);

    internal const string LocalSourceSwitchPurpose = "switch-local-source";
    internal const string LocalSourceSwitchDisposition = "replace-local-source-retain-original";

    internal Task<ReplacementPreparation> PlanLocalSourceSwitchAsync(string ghPath, InstalledSkill target, string newSourcePath,
        InventorySnapshot inventory, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);
        return PlanLocalImportAsync(ghPath, target, newSourcePath, inventory, token);
    }

    private async Task<ReplacementPreparation> PlanLocalImportAsync(string ghPath, InstalledSkill target, string? newSourcePath,
        InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireLocalSourceMetadata(target.FrontMatter, requirePath: true);
        if (target.AllOwnershipClaims.Any()) throw new IOException("Local reimport currently requires an ownerless installation; native owner transitions remain pending.");
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Local reimport needs a containing installation scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        inventory = Admit(target, inventory, token, localReimport: true);
        var switching = newSourcePath is not null;
        if (switching && (!Path.IsPathFullyQualified(newSourcePath!) || newSourcePath!.Any(char.IsControl)))
            throw new IOException("New source must be an absolute ordinary local directory.");
        var sourcePath = switching ? Path.GetFullPath(newSourcePath!) : target.FrontMatter.LocalPath!;
        if (switching && (newSourcePath == target.FrontMatter.LocalPath || sourcePath == target.FrontMatter.LocalPath))
            throw new IOException("No source transition: use local reimport for the same recorded source; installed content has not been verified current.");
        using var previousPlacement = switching ? NativePlacementLease.Open(target.FrontMatter.LocalPath!, token) : null;
        using var selectedPlacement = switching ? NativePlacementLease.Open(newSourcePath!, token) : null;
        if (selectedPlacement?.Evidence.MissingSuffix.IsEmpty == false) throw new IOException("New local source does not exist.");
        var sourceCanonical = selectedPlacement?.Evidence.CanonicalPath ?? Canonical(sourcePath);
        CheckLocalLocations(target, inventory, sourcePath, sourceCanonical, token);
        if (previousPlacement is not null) CheckSwitchPlacement(previousPlacement.Evidence, inventory, sourcePath, sourceCanonical, token);
        // Refuse missing/inadmissible payloads before private writes, but do
        // not retain preflight generations across our namespace initialization.
        using (NativeDirectoryMoveLease.Open(containing, canonical, token)) { }
        using (NativeDirectoryMoveLease.Open(Path.GetDirectoryName(sourceCanonical)!, sourceCanonical, token)) { }
        // Initialize admitted namespaces before freezing payload parents: a
        // first-use ancestor or sibling creation can change their generation.
        // This admits no retained operation, replacement writer or plan quota.
        RevalidatePlacements();
        using var previews = NativeStateDirectory.OpenOrCreate(LocalPreviewRoot, LocalImportExclusions(), token);
        using var previewCoordinator = previews.OpenFile("writer.lock", true, token); CheckWriter(previewCoordinator);
        RevalidatePlacements();
        using (var initialized = NativeStateDirectory.OpenOrCreate(Root, LocalImportExclusions(), token))
            initialized.FlushNamespace(token);
        var retained = previews.EnumerateEntries(token).Take(6).ToArray();
        if (retained.Any(entry => entry.Name != "writer.lock") || retained.Any(entry => entry.Error is not null))
            throw new IOException($"Local reimport working budget is reserved by retained preview evidence at {previews.CanonicalPath}. Unknown or failed output is charged the full {LocalImportWorkingBytes} logical bytes; inspect it before retrying.");
        using var original = NativeDirectoryMoveLease.Open(containing, canonical, token);
        using var source = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(sourceCanonical)!, sourceCanonical, token);
        if (source.TargetIdentity.CanonicalPath != sourceCanonical) throw new IOException("Local source authority changed.");
        var before = original.Capture(token);
        var context = Context(target, inventory, token);
        var id = Guid.NewGuid().ToString("N");
        var previewPath = Path.Combine(previews.CanonicalPath, id);
        OwnedDirectoryLease? ownership = null;
        string? finalPath = null;
        CapturedPayloadArchive? freshSource = null;
        var unknownGhOutput = false;
        var cleanupVerified = false;
        try
        {
            ReplacementPreparation result;
            RevalidatePlacements();
            using (var preview = previews.CreateStagingDirectory(id, token))
            {
                ownership = preview.CaptureOwnership(token);
                using var previewWriter = preview.OpenFile("writer.lock", true, token); CheckWriter(previewWriter);
                original.Revalidate(token); source.Revalidate(token);
                _ = CapturedPayloadArchive.Capture(preview, "installed-capture", original, token);
                _ = CapturedPayloadArchive.Capture(preview, "local-source-capture", source, token);
                checkpoint?.Invoke("local-source-captured");
                using var installedState = OpenArchive(preview, "installed-capture", token);
                using var sourceState = OpenArchive(preview, "local-source-capture", token);
                var installed = CapturedPayloadArchive.Load(installedState, null, token);
                var captured = CapturedPayloadArchive.Load(sourceState, null, token);
                freshSource = captured.Archive;
                VerifyUnpinOriginal(ReplacementEvidence.From(before), installed.Archive, token);
                SameTracking(target.FrontMatter, installed.Archive.Snapshot.FrontMatter ?? throw new IOException("Installed tracking is unavailable."));
                if (switching && !SameParsedMetadata(target.FrontMatter, installed.Archive.Snapshot.FrontMatter!))
                    throw new IOException("Installed authored metadata changed after inventory; refresh before source switching.");
                RequireLocalSourceMetadata(captured.Archive.Snapshot.FrontMatter ?? throw new IOException("Local source requires root SKILL.md."), false);
                RequireSingleLocalSkill(captured.Archive);
                if (switching) RequireOptionalCapturedLocalPath(captured.Archive.Snapshot.FrontMatter!.LocalPath);
                if (captured.Archive.Snapshot.FrontMatter!.Name != target.FrontMatter.Name
                    || !switching && captured.Archive.Snapshot.FrontMatter.LocalPath is { } recorded && recorded != sourcePath
                    || captured.Archive.Entries.Any(item => item.Entry.Kind == "link" || Path.GetFileName(item.Entry.Path) is ".skill-lock.json" or "skills-lock.json"))
                    throw new IOException("Local source needs the same authored name, unambiguous tracking and no links or owner records.");
                CheckLocalWorkingBudget(installed.Archive, captured.Archive);
                var sourceReader = captured.Archive.OpenReader(sourceState);
                using (var installedPolicy = PayloadMetadataReview.PreserveInstalled(installed.Archive, installed.Archive.OpenReader(installedState), token)) { }
                using (var policy = PayloadMetadataReview.ReplaceFromLocal(installed.Archive, captured.Archive, sourceReader, token)) { }
                var process = runner ?? throw new IOException("Local reimport requires captured child process context.");
                unknownGhOutput = true;
                var witness = await new GhLocalImportStager(process, logger).StageAsync(ghPath, preview, captured.Archive, sourceReader, token).ConfigureAwait(false);
                unknownGhOutput = false;
                var staged = BuildLocalCandidate(preview, installed.Archive, captured.Archive, sourceReader, sourcePath, token);
                VerifyCurrent();
                if (!switching && LocalCandidateUnchanged(installed.Archive, staged.Snapshot, staged.Metadata))
                {
                    result = ReplacementPreparation.Current(new(target.InstallationId, canonical, before.Fingerprint,
                        "Verified complete local content, tracking and supported metadata policy are current. No retained plan was allocated; installed objects remain unchanged.", null,
                        new("current", "Durable source matches the captured complete native evidence.")));
                }
                else
                {
                    // Preview coordinator precedes the replacement writer. No
                    // open state directory is renamed or treated as a payload.
                    RevalidatePlacements();
                    using var state = NativeStateDirectory.OpenOrCreate(Root, LocalImportExclusions(), token);
                    using var writer = state.OpenFile("writer.lock", true, token); CheckWriter(writer);
                    var entries = state.EnumerateEntries(token).Take(MaxPlans + 2).ToArray();
                    if (entries.Length > MaxPlans || entries.Any(entry => entry.Name != "writer.lock" && !QuarantineStore.ValidId(entry.Name)))
                        throw new IOException("Retained replacement plan quota is full; local preview evidence remains charged.");
                    using var operation = state.CreateStagingDirectory(id, token);
                    finalPath = operation.CanonicalPath;
                    using var operationWriter = operation.OpenFile("writer.lock", true, token); CheckWriter(operationWriter);
                    foreach (var slot in LocalArchiveNames)
                    {
                        using var input = OpenArchive(preview, slot, token);
                        var fingerprint = CapturedPayloadArchive.Load(input, null, token).Fingerprint;
                        CapturedPayloadArchive.CopyInto(input, operation, slot, fingerprint, token);
                    }
                    checkpoint?.Invoke("local-evidence-copied");
                    using var finalSource = OpenArchive(operation, "local-source-capture", token);
                    var finalCaptured = CapturedPayloadArchive.Load(finalSource, captured.Fingerprint, token).Archive;
                    var built = BuildLocalCandidate(operation, installed.Archive, finalCaptured, finalCaptured.OpenReader(finalSource), sourcePath, token);
                    using var candidate = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "candidate"), token);
                    var desired = candidate.FlushPayload(token);
                    VerifyCurrent();
                    var subject = new ReplacementSubject(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []);
                    var policy = new ReplacementLocalImport(sourcePath, sourceCanonical, source.RootIdentity,
                        installed.Fingerprint, captured.Fingerprint, built.Snapshot.FrontMatter!, PayloadMetadataReview.LocalImportPolicy, built.Metadata, witness, switching ? LocalSourceSwitchDisposition : LocalReimportDisposition);
                    var plan = new ReplacementPlan(switching ? 6 : 5, id, switching ? LocalSourceSwitchPurpose : LocalReimportPurpose, DateTimeOffset.UtcNow, canonical,
                        original.RootIdentity.CanonicalPath, original.RootIdentity, original.ParentIdentity, subject, context,
                        ReplacementEvidence.From(before), ReplacementEvidence.From(desired), LocalChangedPaths(installed.Archive, built.Snapshot, built.Metadata), "", LocalImport: policy,
                        LocalSourceSwitch: switching ? new(target.FrontMatter.LocalPath!, previousPlacement!.Evidence,
                            captured.Archive.Snapshot.FrontMatter!.LocalPath) : null);
                    ValidateLocalImport(plan, operation, token);
                    plan = plan with { ReviewFingerprint = PlanHash(plan) };
                    Write(operation, "plan.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
                    state.FlushPublication(token);
                    result = ReplacementPreparation.Saved(Result(plan, "planned", true, false, false, plan.ReviewFingerprint,
                        (switching ? "Saved baseline-less local source transition. Old source is placement-only; no old base or ancestry is claimed. " : "Saved baseline-less local replacement from the same recorded source. ") + "Review every installed-only/different path and all consumers; no automatic merge is claimed. Candidate preserves captured source bytes and supported descendant metadata plus installed root privacy after actual gh staging. Applying creates new active object identities/timestamps; original native objects remain retained for undo.")
                        with
                    { LocalSource = new("current", "Durable source matches its complete captured native evidence.") });
                }
            }
            // Close every preview child, writer and state handle before the
            // owned native cleanup; Windows can complete deletion at last close.
            var warning = RemoveUnchangedStaging(previewPath, ownership, previews); ownership = null;
            cleanupVerified = warning is null;
            VerifyCurrent();
            if (result.Unchanged is { } unchanged)
                return ReplacementPreparation.Current(unchanged with { CleanupWarning = warning });
            return ReplacementPreparation.Saved(result.Plan! with { Message = result.Plan!.Message + (warning is null ? "" : " " + warning) });
        }
        catch (OperationCanceledException ex)
        {
            var cleanup = CleanKnownFailure();
            throw new OperationCanceledException($"{(switching ? "Local source switch" : "Local reimport")} canceled. Preview {id}: {previewPath}; final operation: {finalPath ?? "not allocated"}. {cleanup} {ex.Message}", ex, ex.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var cleanup = CleanKnownFailure();
            throw StagingFailure($"{(switching ? "Local source switch" : "Local reimport")} refused. Preview {id}: {previewPath}; final operation: {finalPath ?? "not allocated"}. {cleanup} {ex.Message}", ex);
        }
        finally { ownership?.Dispose(); }

        string CleanKnownFailure()
        {
            if (cleanupVerified)
                return "Preview cleanup was verified and its working reservation released. " + (finalPath is null ? "No retained operation was allocated." : "The final operation remains retained and charged to its plan quota.");
            if (!unknownGhOutput && finalPath is null && ownership is not null)
            {
                var warning = RemoveUnchangedStaging(previewPath, ownership, previews); ownership = null;
                cleanupVerified = warning is null;
                return warning ?? "Known private staging was removed with verified owned native cleanup.";
            }
            return "Private evidence remains retained and charged the full logical working reservation; unknown gh output is never counted as zero.";
        }

        string[] LocalImportExclusions() => previousPlacement is null ? LocalExclusions(inventory, sourceCanonical)
            : LocalExclusions(inventory, sourceCanonical).Concat(new[] { previousPlacement.Evidence.Path, previousPlacement.Evidence.CanonicalPath }).Distinct().ToArray();

        void RevalidatePlacements()
        {
            previousPlacement?.Revalidate(token);
            selectedPlacement?.Revalidate(token);
            if (previousPlacement is not null) CheckSwitchPlacement(previousPlacement.Evidence, inventory, sourcePath, sourceCanonical, token);
        }

        void VerifyCurrent()
        {
            RevalidatePlacements();
            inventory = Admit(target, inventory, token, localReimport: true);
            VerifyPayload(ReplacementEvidence.From(before), original.Capture(token));
            if (context != Context(target, inventory, token) || Canonical(sourcePath) != sourceCanonical)
                throw new IOException("Installed context or durable local source address changed; create a fresh review.");
            original.Revalidate(token); source.Revalidate(token);
            if (freshSource is not null) VerifyLocalSourceCapture(source, freshSource, previews, token);
        }
    }

    private static VerifiedMergedPayload BuildLocalCandidate(NativeStateDirectory operation, CapturedPayloadArchive installed,
        CapturedPayloadArchive source, CapturedPayloadArchive.Reader reader, string path, CancellationToken token)
    {
        using var metadata = PayloadMetadataReview.ReplaceFromLocal(installed, source, reader, token);
        var skill = LocalTrackingWriter.Bind(GhLocalImportStager.ReadSkill(reader, token), path);
        var snapshot = InstalledPayloadMaterializer.Stage(operation, source, reader, metadata, skill, token);
        return new(snapshot, PayloadMetadataReview.LocalImportPolicy, metadata.Recipes);
    }

    private static void CheckLocalWorkingBudget(CapturedPayloadArchive installed, CapturedPayloadArchive source)
    {
        // Reserve duplicate archives, input/output, both candidate stages and
        // bounded manifests. This is logical accounting, not a child OS quota.
        long Size(CapturedPayloadArchive archive) => archive.Entries.Sum(item => item.StoredBytes + (item.Metadata?.AlternateStreamBytes ?? 0))
            + (archive.RootMetadata?.AlternateStreamBytes ?? 0) + CapturedPayloadArchive.MaxManifestBytes;
        if (checked(2 * Size(installed) + 10 * Size(source) + 8L * PayloadSnapshot.MaxSkillBytes) > LocalImportWorkingBytes)
            throw new IOException("Local reimport capture and transient duplicate reservation exceed the logical working budget.");
    }

    private static bool LocalCandidateUnchanged(CapturedPayloadArchive installed, PayloadSnapshot candidate,
        ImmutableArray<PayloadMetadataRecipe> metadata) => installed.Snapshot.Fingerprint == candidate.Fingerprint
        && metadata.Length == installed.Entries.Length + 1 && metadata.All(recipe => SameLocalMetadata(recipe.Desired,
            recipe.Path.Length == 0 ? installed.RootMetadata! : installed.Entries.Single(entry => entry.Entry.Path == recipe.Path).Metadata!));

    private static ImmutableArray<string> LocalChangedPaths(CapturedPayloadArchive installed, PayloadSnapshot candidate,
        ImmutableArray<PayloadMetadataRecipe> metadata) => PayloadSnapshot.Differences(installed.Snapshot, candidate, false)
        .AddRange(metadata.Where(recipe => recipe.Path.Length == 0 ? !SameLocalMetadata(recipe.Desired, installed.RootMetadata!)
            : installed.Entries.FirstOrDefault(entry => entry.Entry.Path == recipe.Path) is not { } entry || !SameLocalMetadata(recipe.Desired, entry.Metadata!))
            .Select(recipe => recipe.Path.Length == 0 ? "/" : recipe.Path)).Distinct(StringComparer.Ordinal).ToImmutableArray();

    private static bool SameLocalMetadata(PayloadNativeMetadata left, PayloadNativeMetadata right) => left.Supported && right.Supported
        && left.Platform == right.Platform && left.Kind == right.Kind && left.UnixMode == right.UnixMode
        && left.UserId == right.UserId && left.GroupId == right.GroupId && left.DirectoryLink == right.DirectoryLink
        && WindowsPayloadMetadata.Same(left.Windows, right.Windows) && left.Attributes.Length == right.Attributes.Length
        && left.Attributes.All(attribute => right.Attributes.Any(other => other.Name == attribute.Name && other.Value.AsSpan().SequenceEqual(attribute.Value)));

    private static void RequireLocalSourceMetadata(SkillFrontMatter metadata, bool requirePath)
    {
        if (!metadata.Diagnostics.IsEmpty || metadata.Pinned || metadata.PinnedRef is not null || metadata.Upstream is not null
            || metadata.GithubTreeSha is not null || metadata.GithubRef is not null || metadata.GithubPath is not null
            || string.IsNullOrWhiteSpace(metadata.Name) || requirePath && (metadata.LocalPath is not { } path || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)))
            throw new IOException("Local reimport requires unpinned, unambiguous local-only tracking with an absolute recorded source. Mixed remote/source and native-owner transitions remain pending.");
    }

    private static void RequireSingleLocalSkill(CapturedPayloadArchive source)
    {
        if (source.Entries.Any(item => item.Entry.Path != "SKILL.md"
            && Path.GetFileName(item.Entry.Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Nested SKILL.md packages or case variants are not supported by the local reimport gh adapter; no gh child was launched. Review nested source support separately.");
    }

    private string[] LocalExclusions(InventorySnapshot inventory, string source) => Exclusions(inventory).Append(source).ToArray();

    private void CheckLocalLocations(InstalledSkill target, InventorySnapshot inventory, string sourcePath, string source, CancellationToken token)
    {
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        ManagedSkillDiscovery.RefusePaths([sourcePath, source, Root, LocalPreviewRoot], token, runner);
        CheckPlacement(inventory, token);
        foreach (var state in new[] { Root, LocalPreviewRoot })
        {
            NativeStateDirectory.CheckExcluded(Canonical(state), LocalExclusions(inventory, source));
            if (ScanRootResolver.FindGitRoot(state) is not null || PathIdentity.IsInside(source, Canonical(state)))
                throw new IOException("Local reimport state and source must be disjoint and state outside Git.");
        }
        if (PathResolver.IsSymlink(sourcePath) || PathIdentity.IsInside(source, target.ResolvedPath)
            || PathIdentity.IsInside(target.ResolvedPath, source) || PathIdentity.Equals(source, target.ResolvedPath))
            throw new IOException("Local source must be a separate ordinary directory, not an installation or a final-component link.");
    }
}
