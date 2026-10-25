using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SkillView.Inventory;
using SkillView.Maintenance;

namespace SkillView.Tests.Maintenance;

// Only builds the flat file/empty-directory fixtures used by saved-schema tests.
// Actual old-producer compatibility is proved by prove-old-native-plans.py.
internal static class HistoricalMergeFixture
{
    internal sealed record Review(string ReviewPath, string HeaderFingerprint, string SelectionFingerprint,
        CapturedPayloadArchive Installed, CapturedPayloadArchive Source, ThreeWayPayloadReview Paths,
        CapturedResolutionArchive? Resolution = null);

    internal static Review CaptureReview(string state, ReplacementResult result, CancellationToken token, string side = "installed", string? resolutionFile = null)
    {
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state, result.Id), [], token, allowCreate: false);
        using var writer = operation.OpenExistingFile("writer.lock", true, token);
        var plan = JsonSerializer.Deserialize(File.ReadAllBytes(result.PlanPath), ReplacementJsonContext.Default.ReplacementPlan)!;
        using var draft = operation.CreateStagingDirectory("local-review", token);
        var installed = Capture("installed", plan.RootPath, plan.OriginalPath);
        var recorded = Capture("recorded", operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "recorded-base"));
        var source = Capture("source", operation.CanonicalPath, result.CandidatePath);
        var headerHash = Write(draft, "header.json", new LocalReviewHeader(1, result.PlanFingerprint,
            recorded.Hash, installed.Hash, source.Hash), LocalReviewJsonContext.Default.LocalReviewHeader, token);
        CapturedResolutionArchive? resolution = null; string? resolutionHash = null;
        if (resolutionFile is not null) (resolution, resolutionHash) = CaptureResolution(draft, resolutionFile, token);
        LocalPathChoice[] choices = resolution is null ? [new("SKILL.md", side)] : [new("SKILL.md", side), new("resource", "resolved")];
        var selectionHash = Write(draft, "choices-00.json", new LocalReviewSelection(resolution is null ? 1 : 2, headerHash, [.. choices],
                resolution is null ? null : new([new("resource", "resolutions-00", resolutionHash!)])),
            LocalReviewJsonContext.Default.LocalReviewSelection, token);
        draft.SealStagingDirectory(448, token);
        return new(draft.CanonicalPath, headerHash, selectionHash, installed.Archive, source.Archive,
            ThreeWayPayloadReview.Analyze(recorded.Archive.Snapshot, installed.Archive.Snapshot, source.Archive.Snapshot, choices,
                resolvedFiles: resolution?.Entries.Select(item => item.Entry).ToArray()), resolution);

        (CapturedPayloadArchive Archive, string Hash) Capture(string name, string root, string path)
        {
            using var lease = NativeDirectoryMoveLease.Open(root, path, token);
            var archive = CapturedPayloadArchive.Capture(draft, name, lease, token);
            using var directory = NativeStateDirectory.OpenOrCreate(Path.Combine(draft.CanonicalPath, name), [], token, allowCreate: false);
            return (archive, CapturedPayloadArchive.Load(directory, null, token).Fingerprint);
        }
    }

    internal static void Create(string state, ReplacementResult result, CancellationToken token, string side = "installed", Review? capturedReview = null)
    {
        var review = capturedReview ?? CaptureReview(state, result, token, side);
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state, result.Id), [], token, allowCreate: false);
        using var writer = operation.OpenExistingFile("writer.lock", true, token);
        using var installed = NativeStateDirectory.OpenOrCreate(Path.Combine(review.ReviewPath, "installed"), [], token, allowCreate: false);
        using var source = NativeStateDirectory.OpenOrCreate(Path.Combine(review.ReviewPath, "source"), [], token, allowCreate: false);
        using var resolved = review.Resolution is null ? null : NativeStateDirectory.OpenOrCreate(Path.Combine(review.ReviewPath, "resolutions-00"), [], token, allowCreate: false);
        var readers = new Dictionary<string, IPayloadEvidenceReader>
        { ["installed"] = review.Installed.OpenReader(installed), ["source"] = review.Source.OpenReader(source) };
        if (resolved is not null) readers["resolved"] = review.Resolution!.OpenReader(resolved);
        using var metadata = PayloadMetadataReview.Create(review.Paths, (CapturedPayloadArchive.Reader)readers["installed"], token,
            readers["source"], readers.GetValueOrDefault("resolved"));
        var plan = JsonSerializer.Deserialize(File.ReadAllBytes(result.PlanPath), ReplacementJsonContext.Default.ReplacementPlan)!;
        PayloadSnapshot authored;
        using (var root = operation.CreateMetadataPayloadDirectory("merged-00", token))
        {
            foreach (var row in review.Paths.Paths.Where(row => row.Selected is not null))
            {
                var entry = row.Selected!;
                if (entry.Path.Contains('/')) throw new InvalidOperationException("Historical fixture only supports flat entries.");
                if (entry.Kind == "directory")
                {
                    using var empty = root.CreateDirectory(entry.Path, token);
                    empty.SealWithMetadata(entry.UnixMode, metadata[entry.Path].Desired, token, metadata.Authority);
                }
                else if (entry.Kind == "file")
                {
                    using var bytes = new MemoryStream(); readers[row.Selection!].CopyEntry(entry.Path, bytes, token);
                    var content = entry.Path == "SKILL.md"
                        ? Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(Encoding.UTF8.GetString(bytes.ToArray()), plan.Revision!.DesiredMetadata)) : bytes.ToArray();
                    using var output = root.CreateFile(entry.Path, token);
                    output.Write(content); output.Flush(true);
                    PayloadNativeMetadata.Apply(output.SafeFileHandle, metadata[entry.Path].Desired, entry.UnixMode, token, metadata.Authority);
                    output.Flush(true);
                }
                else throw new InvalidOperationException("Historical fixture only supports regular files and empty directories.");
            }
            root.SealWithMetadata(review.Paths.RootMode.Selected, metadata[""].Desired, token, metadata.Authority);
            authored = root.FlushAndCapture(token, item => metadata.Verify(item, token));
        }
        using var merged = NativeDirectoryMoveLease.Open(operation.CanonicalPath, Path.Combine(operation.CanonicalPath, "merged-00"), token);
        var snapshot = merged.Capture(token);
        plan = plan with
        {
            SchemaVersion = 3,
            Candidate = ReplacementEvidence.From(snapshot),
            ChangedPaths = PayloadSnapshot.Differences(review.Installed.Snapshot, authored, false)
                .AddRange(review.Installed.Snapshot.RootUnixMode != authored.RootUnixMode ? new[] { "/" } : []),
            LocalDisposition = new("merge-reviewed", result.PlanFingerprint, review.HeaderFingerprint, review.SelectionFingerprint,
                plan.Candidate, metadata.PolicyName, "merged-00", authored.FrontMatter!.Version),
            ReviewFingerprint = ""
        };
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(plan, ReplacementJsonContext.Default.ReplacementPlan));
        using var canonical = new MemoryStream();
        using (var output = new Utf8JsonWriter(canonical)) Canonical(json.RootElement, output);
        plan = plan with { ReviewFingerprint = Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray())) };
        Write(operation, "merge-plan-00.json", plan, ReplacementJsonContext.Default.ReplacementPlan, token);
    }

    private static (CapturedResolutionArchive Archive, string Hash) CaptureResolution(NativeStateDirectory draft, string input, CancellationToken token)
    {
        using var archive = draft.CreateStagingDirectory("resolutions-00", token);
        using var authority = PayloadMetadataAuthority.Open(archive, create: true, token);
        ArchivedPayloadEntry? entry = null;
        RegularFileInspection.Capture(input, item =>
        {
            if (item.Content!.Length > 8192) throw new InvalidOperationException("Historical fixture resolution exceeds 8 KiB.");
            var metadata = PayloadNativeMetadata.Capture(item, token, authority);
            item.Content.Position = 0;
            using var bytes = new MemoryStream(); item.Content.CopyTo(bytes);
            var content = bytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(content));
            using var blob = archive.OpenFile("entry-00000", false, token); blob.Write(content); blob.Flush(true);
            entry = new(new("resource", "file", hash, hash, content.Length, metadata.UnixMode, null), "entry-00000", content.Length, metadata);
            return new(hash, content.Length);
        }, token);
        var captured = new CapturedResolutionArchive([entry!]); captured.Validate();
        var fingerprint = Write(archive, "manifest.json", new ResolutionArchiveManifest(1, captured.Entries),
            ResolutionJsonContext.Default.ResolutionArchiveManifest, token);
        archive.SealStagingDirectory(448, token);
        return (captured, fingerprint);
    }

    private static string Write<T>(NativeStateDirectory directory, string name, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var file = directory.OpenFile(name, false, token);
        var hash = BoundedReviewJson.Write(file, value, type, 16 * 1024 * 1024, token);
        file.Flush(true); directory.FlushNamespace(token); directory.FlushPublication(token);
        return hash;
    }

    private static void Canonical(JsonElement value, Utf8JsonWriter output)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            output.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { output.WritePropertyName(property.Name); Canonical(property.Value, output); }
            output.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { output.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(item, output); output.WriteEndArray(); }
        else value.WriteTo(output);
    }
}
