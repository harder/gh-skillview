using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    // Schema 6 belongs to the separately reviewed source-switch workflow.
    internal const int SelectedRevisionSchema = 7;
    internal const string SelectedRevisionPurpose = "selected-revision";
    internal const string SelectedRevisionDisposition = "selected-revision-retain-original";
    private string SelectedPreviewRoot => Root + "-selected-previews";
    private static readonly string[] SelectedArchives = ["selected-source-capture", "selected-gh-capture"];

    internal Task<ReplacementPreparation> PlanSelectedAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, SelectedRevisionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var process = (runner ?? throw new IOException("Selected revision requires captured child context.")).CaptureContext();
        checkpoint?.Invoke("selected-context-captured");
        return new ReplacementPlanStore(Root, logger, checkpoint, process, beforeStateOpenForTests, subjectObserved)
            .PlanSelectedCoreAsync(ghPath, target, inventory, request, token);
    }

    private async Task<ReplacementPreparation> PlanSelectedCoreAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, SelectedRevisionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); request.Validate();
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        if (target.Pinned && !request.Pin) throw new IOException("A selected revision cannot implicitly clear an installed pin; review standalone unpin first.");
        if (target.Validity != ValidityState.Valid || !target.MetadataConflicts.IsEmpty || !target.OwnershipDiagnostics.IsEmpty
            || !target.FrontMatter.Diagnostics.IsEmpty || target.FrontMatter.LocalPath is not null
            || target.FrontMatter.Upstream is null || target.FrontMatter.GithubPath is null)
            throw new IOException("Selected revision requires an unambiguous observed remote installation.");
        NativeStateDirectory.CheckName(target.FrontMatter.Name!);
        var canonical = Canonical(target.ResolvedPath);
        var containing = inventory.ScannedRoots.Select(scan => Canonical(scan.Path))
            .Where(path => !PathIdentity.Equals(path, canonical) && PathIdentity.IsInside(canonical, path))
            .OrderByDescending(path => path.Length).FirstOrDefault()
            ?? throw new IOException("Selected revision requires the actual containing installation scan root.");
        target = target with { ResolvedPath = canonical, ScanRoot = containing };
        var preflightOwner = PrepareOwner(target, null, inventory, token, initializeState: false, selection: request, selectedTree: target.TreeSha);
        using (var owner = OpenOwner(preflightOwner, token, requireBefore: true)) inventory = Admit(target, inventory, token, owner: owner);
        using (var current = NativeDirectoryMoveLease.Open(containing, canonical, token))
        {
            var observed = current.CaptureInto((_, _) => { }, null, token);
            SameTracking(target.FrontMatter, observed.FrontMatter ?? throw new IOException("Installed metadata is unavailable."));
            if (!observed.FrontMatter.Diagnostics.IsEmpty) throw new IOException("Installed metadata changed before selected capture.");
        }
        var process = runner ?? throw new IOException("Selected revision requires captured child context.");
        using var policy = AcquireDesiredPolicy(target, inventory, SelectedRevisionPurpose, token, selection: request);
        CheckPlacement(inventory, token);
        // Initialize both private roots before freezing any installed/registry
        // namespace. Failed/unknown previews reserve one full working budget.
        using var previews = NativeStateDirectory.OpenOrCreate(SelectedPreviewRoot, Exclusions(inventory), token);
        using var previewWriter = previews.OpenFile("writer.lock", true, token); CheckWriter(previewWriter);
        using (var initialized = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token)) initialized.FlushNamespace(token);
        if (previews.EnumerateEntries(token).Take(3).Any(item => item.Name != "writer.lock" || item.Error is not null))
            throw new IOException("Selected revision working budget is reserved by retained partial or unknown preview evidence at " + previews.CanonicalPath + ".");
        var id = Guid.NewGuid().ToString("N");
        var previewPath = Path.Combine(previews.CanonicalPath, id);
        OwnedDirectoryLease? ownership = null;
        try
        {
            ReplacementPreparation result;
            using (var preview = previews.CreateStagingDirectory(id, token))
            {
                ownership = preview.CaptureOwnership(token);
                var binary = GhLocalImportStager.BindBinary(ghPath, token);
                var resolver = new GhRevisionResolver(process, binary.Path);
                var selected = await resolver.ResolveSelectedAsync(target.FrontMatter.Upstream, target.FrontMatter.GithubPath, request, token).ConfigureAwait(false);
                ImmutableArray<RecordedGitEntry> git;
                using (var raw = preview.CreateStagingDirectory("selected-source", token))
                {
                    git = await new GhRecordedTreeStager(process).CaptureIntoAsync(binary.Path, selected.Repository, selected.Tree, raw, null, token).ConfigureAwait(false);
                    raw.SealStagingDirectory(493, token);
                }
                using (var raw = NativeDirectoryMoveLease.Open(preview.CanonicalPath, Path.Combine(preview.CanonicalPath, "selected-source"), token))
                    _ = CapturedPayloadArchive.Capture(preview, "selected-source-capture", raw, token);
                using var sourceState = OpenArchive(preview, "selected-source-capture", token);
                var source = CapturedPayloadArchive.Load(sourceState, null, token);
                var witness = await new GhPinnedRevisionStager(process, logger).StageAsync(binary.Path, preview, source.Archive,
                    source.Archive.OpenReader(sourceState), source.Fingerprint, git, selected, target.FrontMatter.Name!, token, binary.Hash).ConfigureAwait(false);
                // This is the last mutable-tag lookup. Applying or restarting
                // the saved operation only consumes its frozen native archives.
                await resolver.VerifySelectedAsync(selected, token).ConfigureAwait(false);
                if (GhLocalImportStager.BinaryHash(binary.Path, token) != binary.Hash)
                    throw new IOException("The gh executable changed during selected revision resolution.");
                using var outputState = OpenArchive(preview, "selected-gh-capture", token);
                var output = CapturedPayloadArchive.Load(outputState, witness.OutputArchiveFingerprint, token).Archive;
                var reader = output.OpenReader(outputState);
                var desiredSkill = SelectedSkill(reader, request, token);
                var desiredMetadata = ParseSelectedSkill(desiredSkill, target.ResolvedPath);
                var resolved = new ResolvedSkillRevision(selected.Repository, desiredMetadata.GithubRef!, selected.Commit, selected.Tree, selected.Subpath);
                using var metadata = PayloadMetadataReview.PreserveInstalled(output, reader, token);
                var selection = new ReplacementSelection(witness, metadata.PolicyName, metadata.Recipes);
                var revision = new ReplacementRevision(resolved, desiredMetadata, null, [],
                    PrepareOwner(target, resolved, inventory, token, initializeState: false, selection: request));
                result = await PlanCoreAsync(target, inventory, (_, _) => throw new InvalidOperationException("Selected staging requires its typed archive path."), revision,
                    async (baseline, ct) =>
                    {
                        await new GhRecordedTreeStager(process).StageIntoAsync(binary.Path, selected.Repository, target.TreeSha!, baseline, bytes =>
                            Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(new UTF8Encoding(false, true).GetString(bytes), target.FrontMatter)), ct).ConfigureAwait(false);
                        if (GhLocalImportStager.BinaryHash(binary.Path, ct) != binary.Hash)
                            throw new IOException("The gh executable changed during recorded-base capture.");
                    },
                    token, selection: selection, stageSelected: (operation, ct) =>
                    {
                        foreach (var name in SelectedArchives)
                        {
                            using var archive = OpenArchive(preview, name, ct);
                            var expected = name == "selected-source-capture" ? witness.SourceArchiveFingerprint : witness.OutputArchiveFingerprint;
                            CapturedPayloadArchive.CopyInto(archive, operation, name, expected, ct);
                        }
                        _ = InstalledPayloadMaterializer.Stage(operation, output, reader, metadata, desiredSkill, ct);
                        return Task.CompletedTask;
                    }, admittedPolicy: policy).ConfigureAwait(false);
            }
            var warning = RemoveUnchangedStaging(previewPath, ownership, previews); ownership = null;
            return warning is null ? result : ReplacementPreparation.Saved(result.Plan! with { Message = result.Plan!.Message + " " + warning });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw StagingFailure("Selected capture refused; retained preview remains charged at " + previewPath + ". " + ex.Message, ex); }
        finally { ownership?.Dispose(); }
    }

    private static byte[] SelectedSkill(CapturedPayloadArchive.Reader reader, SelectedRevisionRequest request, CancellationToken token)
    {
        var bytes = GhLocalImportStager.ReadSkill(reader, token);
        return request.Pin ? bytes : PinTrackingWriter.Unpin(bytes);
    }

    private static SkillFrontMatter ParseSelectedSkill(byte[] bytes, string path)
    {
        var (_, parsed, valid) = FrontMatterParser.Parse(new UTF8Encoding(false, true).GetString(bytes));
        if (!valid || !parsed.Diagnostics.IsEmpty) throw new IOException("Selected candidate metadata is invalid at " + path + ".");
        return parsed;
    }

    private static void ValidateSelection(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.Selection is not { } selection) return;
        _ = EffectiveOwner(plan);
        var provider = selection.Provider ?? throw new IOException("Selected provider witness is missing.");
        var selected = provider.Selection ?? throw new IOException("Selected immutable identity is missing.");
        if (selected.Request is null || selected.Repository is null || selected.Subpath is null || selected.Commit is not { Length: 40 }
            || !selected.Commit.All(char.IsAsciiHexDigit) || selected.Tree is not { Length: 40 } || !selected.Tree.All(char.IsAsciiHexDigit)
            || provider.Version is null || provider.StdOut is null || provider.StdErr is null || provider.BinaryPath is null
            || selection.Metadata.IsDefault || plan.Subject.Metadata.Upstream is null)
            throw new IOException("Saved selected provider witness is incomplete.");
        selected.Request.Validate();
        if (plan.Subject.Metadata.Pinned && !selected.Request.Pin || plan.Revision is not { } revision
            || revision.Source is null || revision.DesiredMetadata is null
            || GhRevisionResolver.Repository(plan.Subject.Metadata.Upstream!) != GhRevisionResolver.Repository(selected.Repository)
            || SkillSourceIdentity.NormalizeSubpath(plan.Subject.Metadata.GithubPath) != selected.Subpath
            || selected.Request.Kind == "commit" && selected.Commit != selected.Request.Selector
            || selected.Ref != (selected.Request.Kind == "tag" ? "refs/tags/" + selected.Request.Value : null)
            || !QuarantineStore.ValidHash(provider.BinarySha256) || !Path.IsPathFullyQualified(provider.BinaryPath)
            || !QuarantineStore.ValidHash(provider.SourceArchiveFingerprint) || !QuarantineStore.ValidHash(provider.OutputArchiveFingerprint)
            || !QuarantineStore.ValidHash(provider.ContextFingerprint)
            || provider.EnvironmentPolicy != GhPinnedRevisionStager.EnvironmentPolicy || provider.Version.Length > 4096
            || provider.StdOut.Length > 64 * 1024 + 100 || provider.StdErr.Length > 64 * 1024 + 100
            || provider.Arguments.IsDefault || provider.Arguments.Length != 8 || provider.Arguments.Any(string.IsNullOrEmpty)
            || !Path.IsPathFullyQualified(provider.Arguments[3])
            || !provider.Arguments.SequenceEqual(GhPinnedRevisionStager.BuildArguments(provider.Arguments[3], selected)))
            throw new IOException("Saved selected provider/request/source evidence is inconsistent.");
        using var sourceState = OpenArchive(operation, "selected-source-capture", token);
        var source = CapturedPayloadArchive.Load(sourceState, provider.SourceArchiveFingerprint, token).Archive;
        GhRecordedTreeStager.VerifyArchiveTree(source, source.OpenReader(sourceState), provider.GitEntries, selected.Tree, token);
        using var outputState = OpenArchive(operation, "selected-gh-capture", token);
        var output = CapturedPayloadArchive.Load(outputState, provider.OutputArchiveFingerprint, token).Archive;
        var version = provider.Version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (version.Length < 3 || version[0] != "gh" || version[1] != "version" || !SkillView.Gh.GhBinaryLocator.SatisfiesMinimum(version[2])
            || !PathIdentity.Equals(output.Snapshot.Path, Path.Combine(provider.Arguments[3], plan.Subject.Metadata.Name!)))
            throw new IOException("Saved selected executable version or actual output address is inconsistent.");
        var reader = output.OpenReader(outputState);
        GhPinnedRevisionStager.ValidateOutput(source.Snapshot, source.OpenReader(sourceState), output.Snapshot, reader, selected, plan.Subject.Metadata.Name!, token);
        foreach (var entry in output.Entries.Where(item => item.Blob is not null)) reader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        using var metadata = PayloadMetadataReview.PreserveInstalled(output, reader, token);
        if (!JsonSerializer.SerializeToUtf8Bytes(selection, ReplacementJsonContext.Default.ReplacementSelection)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(new ReplacementSelection(provider, metadata.PolicyName, metadata.Recipes), ReplacementJsonContext.Default.ReplacementSelection)))
            throw new IOException("Saved selected metadata recipes differ from retained native evidence.");
        var skill = SelectedSkill(reader, selected.Request, token);
        var desired = ParseSelectedSkill(skill, plan.OriginalPath);
        SameTracking(desired, revision.DesiredMetadata);
        if (revision.Source != new ResolvedSkillRevision(selected.Repository, desired.GithubRef!, selected.Commit, selected.Tree, selected.Subpath))
            throw new IOException("Saved selected revision differs from its actual provider output.");
        var skillHash = Convert.ToHexString(SHA256.HashData(skill));
        var expected = output.Snapshot.Entries.Select(entry => NativeUnpinEntry(entry.Path == "SKILL.md"
            ? entry with { ContentHash = skillHash, Bytes = skill.Length } : entry));
        if (plan.Candidate.Mode != output.Snapshot.RootUnixMode || !expected.SequenceEqual(plan.Candidate.Entries))
            throw new IOException("Selected candidate content differs from its captured provider output and explicit pin intent.");
    }

    private static void VerifySelection(ReplacementPlan plan, NativeStateDirectory operation, string topology,
        ReplacementSlot? original, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.Selection is not { } selection) return;
        using var state = OpenArchive(operation, "selected-gh-capture", token);
        var output = CapturedPayloadArchive.Load(state, selection.Provider.OutputArchiveFingerprint, token).Archive;
        using var metadata = PayloadMetadataReview.PreserveInstalled(output, output.OpenReader(state), token);
        var current = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        using var lease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(current.Path, operation.CanonicalPath)
            ? operation.CanonicalPath : plan.RootPath, current.Path, token);
        lease.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
    }
}
