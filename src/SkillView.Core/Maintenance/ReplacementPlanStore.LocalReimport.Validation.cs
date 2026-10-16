using System.Text;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private static void ValidateLocalImport(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.LocalImport is not { } policy) return;
        _ = EffectiveOwner(plan);
        RequireLocalSourceMetadata(plan.Subject.Metadata, true);
        RequireLocalSourceMetadata(policy.DesiredMetadata, true);
        if (policy.SourcePath != plan.Subject.Metadata.LocalPath || policy.DesiredMetadata.LocalPath != policy.SourcePath
            || !Path.IsPathFullyQualified(policy.CanonicalSourcePath) || policy.Disposition != LocalReimportDisposition
            || policy.MetadataPolicy != PayloadMetadataReview.LocalImportPolicy || policy.Metadata.IsDefault
            || policy.Metadata.Any(item => item is null || item.Basis is null)
            || policy.SourceRootIdentity.CanonicalPath != Path.GetDirectoryName(policy.CanonicalSourcePath)
            || !policy.SourceRootIdentity.IsDirectory || policy.SourceRootIdentity.IsReparsePoint
            || !QuarantineStore.ValidHash(policy.InstalledArchiveFingerprint) || !QuarantineStore.ValidHash(policy.SourceArchiveFingerprint)
            || policy.Gh is null || !QuarantineStore.ValidHash(policy.Gh.BinarySha256)
            || !Path.IsPathFullyQualified(policy.Gh.BinaryPath) || policy.Gh.Version is not { Length: > 0 and <= 4096 }
            || !policy.Gh.Version.StartsWith("gh version ", StringComparison.Ordinal)
            || policy.Gh.StdOut is not { Length: <= 66 * 1024 } || policy.Gh.StdErr is not { Length: <= 66 * 1024 }
            || policy.Gh.EnvironmentPolicy != GhLocalImportStager.EnvironmentPolicy
            || !Path.IsPathFullyQualified(policy.Gh.InputPath) || !Path.IsPathFullyQualified(policy.Gh.OutputPath)
            || !QuarantineStore.ValidHash(policy.Gh.InputArchiveFingerprint) || !QuarantineStore.ValidHash(policy.Gh.OutputArchiveFingerprint))
            throw new IOException("Saved local reimport source, disposition or witness policy is invalid.");
        using var installedState = OpenArchive(operation, "installed-capture", token);
        using var sourceState = OpenArchive(operation, "local-source-capture", token);
        using var inputState = OpenArchive(operation, "gh-input-capture", token);
        using var outputState = OpenArchive(operation, "gh-output-capture", token);
        var installed = CapturedPayloadArchive.Load(installedState, policy.InstalledArchiveFingerprint, token).Archive;
        var source = CapturedPayloadArchive.Load(sourceState, policy.SourceArchiveFingerprint, token).Archive;
        var input = CapturedPayloadArchive.Load(inputState, policy.Gh.InputArchiveFingerprint, token).Archive;
        var output = CapturedPayloadArchive.Load(outputState, policy.Gh.OutputArchiveFingerprint, token).Archive;
        VerifyUnpinOriginal(plan.Original, installed, token);
        SameTracking(plan.Subject.Metadata, installed.Snapshot.FrontMatter ?? throw new IOException("Installed local metadata missing."));
        RequireLocalSourceMetadata(source.Snapshot.FrontMatter ?? throw new IOException("Captured local source metadata missing."), false);
        RequireSingleLocalSkill(source);
        if (source.Snapshot.Path != policy.CanonicalSourcePath || source.Snapshot.FrontMatter!.Name != plan.Subject.Metadata.Name
            || source.Snapshot.FrontMatter.LocalPath is { } path && path != policy.SourcePath
            || source.Entries.Any(entry => entry.Entry.Kind == "link" || Path.GetFileName(entry.Entry.Path) is ".skill-lock.json" or "skills-lock.json")
            || input.Snapshot.Path != policy.Gh.InputPath || output.Snapshot.Path != policy.Gh.OutputPath
            || !source.Snapshot.Entries.SequenceEqual(input.Snapshot.Entries)
            || output.Snapshot.FrontMatter?.LocalPath != policy.Gh.InputPath || output.Snapshot.FrontMatter.Name != plan.Subject.Metadata.Name
            || policy.Gh.Arguments.IsDefault || !policy.Gh.Arguments.SequenceEqual(new[] { "skill", "install", "--from-local", "--dir", Path.GetDirectoryName(policy.Gh.OutputPath)!, "--", policy.Gh.InputPath, plan.Subject.Metadata.Name! }))
            throw new IOException("Saved gh witness is not bound to the exact selected local input and output.");
        RequireLocalSourceMetadata(output.Snapshot.FrontMatter!, true);
        var reader = source.OpenReader(sourceState);
        var skill = LocalTrackingWriter.Bind(GhLocalImportStager.ReadSkill(reader, token), policy.SourcePath);
        var (_, desired, valid) = FrontMatterParser.Parse(new UTF8Encoding(false, true).GetString(skill));
        if (!valid || !SameParsedMetadata(desired, policy.DesiredMetadata)) throw new IOException("Local candidate metadata differs from the captured-source splice.");
        var hash = Hash(skill);
        var expected = source.Snapshot.Entries.Select(entry => NativeUnpinEntry(entry.Path == "SKILL.md"
            ? entry with { Bytes = skill.Length, ContentHash = hash } : entry)).ToArray();
        if (!plan.Candidate.Entries.SequenceEqual(expected) || plan.Candidate.Mode != plan.Original.Mode)
            throw new IOException("Local candidate differs from its exact captured-source bytes or installed root privacy.");
        using var metadata = PayloadMetadataReview.ReplaceFromLocal(installed, source, reader, token);
        if (!SameRecipes(metadata.Recipes, policy.Metadata)) throw new IOException("Saved local native metadata recipes differ from captured evidence.");
        foreach (var state in new[] { installedState, sourceState, inputState, outputState })
        {
            var archive = CapturedPayloadArchive.Load(state, null, token).Archive;
            var archiveReader = archive.OpenReader(state);
            foreach (var entry in archive.Entries.Where(entry => entry.Blob is not null)) archiveReader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        }
    }

    private static bool SameRecipes(System.Collections.Immutable.ImmutableArray<PayloadMetadataRecipe> expected,
        System.Collections.Immutable.ImmutableArray<PayloadMetadataRecipe> actual) => expected.Length == actual.Length
        && expected.Zip(actual).All(pair => pair.First.Path == pair.Second.Path && pair.First.BasisPath == pair.Second.BasisPath
            && pair.First.Basis.Identity == pair.Second.Basis.Identity && pair.First.CopyAttributes == pair.Second.CopyAttributes
            && pair.First.SelectedMode == pair.Second.SelectedMode && pair.First.DestinationKind == pair.Second.DestinationKind
            && SameLocalMetadata(pair.First.Desired, pair.Second.Desired));

    private static void VerifyLocalImport(ReplacementPlan plan, NativeStateDirectory operation, string topology,
        ReplacementSlot? original, ReplacementSlot? retained, ReplacementSlot? candidate, ReplacementSlot? displaced, CancellationToken token)
    {
        if (plan.LocalImport is not { } policy) return;
        using var installedState = OpenArchive(operation, "installed-capture", token);
        using var sourceState = OpenArchive(operation, "local-source-capture", token);
        var installed = CapturedPayloadArchive.Load(installedState, policy.InstalledArchiveFingerprint, token).Archive;
        var source = CapturedPayloadArchive.Load(sourceState, policy.SourceArchiveFingerprint, token).Archive;
        using var oldMetadata = PayloadMetadataReview.PreserveInstalled(installed, installed.OpenReader(installedState), token);
        using var newMetadata = PayloadMetadataReview.ReplaceFromLocal(installed, source, source.OpenReader(sourceState), token);
        var old = topology is "ready" or "restored" ? original! : retained!;
        var current = topology is "ready" or "original-retained" ? candidate! : topology == "installed" ? original! : displaced!;
        Verify(old, oldMetadata); Verify(current, newMetadata);
        void Verify(ReplacementSlot slot, PayloadMetadataReview metadata)
        {
            using var lease = NativeDirectoryMoveLease.Open(PathIdentity.IsInside(slot.Path, operation.CanonicalPath)
                ? operation.CanonicalPath : plan.RootPath, slot.Path, token);
            lease.CaptureInto((item, _) => metadata.Verify(item, token), item => metadata.Verify(item, token), token);
        }
    }

    private ReplacementLocalSourceObservation? InspectLocalSource(ReplacementPlan plan, NativeStateDirectory operation,
        string topology, CancellationToken token)
    {
        if (plan.LocalImport is null) return null;
        if (topology != "ready") return new("not-required-after-retain", "Recovery and undo use the frozen captured source; its current external path is not required.");
        try { RequireFreshLocalSource(plan, operation, token); return new("current", "Durable local source still matches the full captured native evidence."); }
        catch (FileNotFoundException ex) { return new("unavailable", ex.Message); }
        catch (DirectoryNotFoundException ex) { return new("unavailable", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("stale", ex.Message); }
    }

    private void RequireFreshLocalSource(ReplacementPlan plan, NativeStateDirectory operation, CancellationToken token)
    {
        if (plan.LocalImport is not { } policy) return;
        ManagedSkillDiscovery.RefusePaths([policy.SourcePath, policy.CanonicalSourcePath], token, runner);
        if (PathResolver.IsSymlink(policy.SourcePath) || Canonical(policy.SourcePath) != policy.CanonicalSourcePath)
            throw new IOException("Durable local source address changed; create a fresh reimport review.");
        using var state = OpenArchive(operation, "local-source-capture", token);
        var captured = CapturedPayloadArchive.Load(state, policy.SourceArchiveFingerprint, token).Archive;
        using var source = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(policy.CanonicalSourcePath)!, policy.CanonicalSourcePath, token);
        if (source.RootIdentity != policy.SourceRootIdentity) throw new IOException("Local source parent authority changed; create a fresh review.");
        VerifyLocalSourceCapture(source, captured, state, token);
    }

    internal static void VerifyLocalSourceCapture(NativeDirectoryMoveLease source, CapturedPayloadArchive captured,
        NativeStateDirectory authorityState, CancellationToken token)
    {
        using var authority = PayloadMetadataAuthority.Open(authorityState, false, token);
        var entries = captured.Entries.ToDictionary(entry => entry.Entry.Path, StringComparer.Ordinal);
        var current = source.CaptureInto((item, _) => Verify(item, entries.TryGetValue(item.RelativePath, out var expected)
                ? expected.Metadata! : throw new IOException("Local source path set changed after capture.")),
            item => Verify(item, captured.RootMetadata!), token);
        if (current.Fingerprint != captured.Snapshot.Fingerprint || current.ObjectStateFingerprint != captured.Snapshot.ObjectStateFingerprint)
            throw new IOException("Durable local source changed after capture; create a fresh reimport review.");
        source.Revalidate(token);
        void Verify(NativePayloadItem item, PayloadNativeMetadata expected)
        {
            var actual = PayloadNativeMetadata.Capture(item, token, authority);
            if (actual.Identity != expected.Identity || !SameLocalMetadata(actual, expected))
                throw new IOException("Durable local source native metadata changed after capture: " + item.RelativePath);
        }
    }
}
