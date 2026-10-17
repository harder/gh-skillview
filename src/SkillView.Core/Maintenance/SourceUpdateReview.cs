using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.PackageChecking;
using PackageObservation = SkillView.PackageChecking.PackageObservation;

namespace SkillView.Maintenance;

internal sealed record SourceReviewFile(string Path, PayloadEntry? Before, PayloadEntry? After,
    string TextDiff, string Presentation, int OmittedLines);
internal sealed record SourceReviewDeclarations(string Coverage, ImmutableArray<PackageObservation> Observations,
    int ObservationCount, ImmutableArray<string> Reasons, int ReasonCount)
{
    public int OmittedReasonCount => ReasonCount - Reasons.Length;
}
internal sealed record SourceReviewDelta(PackageObservation Observation, string Evidence);
internal sealed record SourceReviewReport(ImmutableArray<SourceReviewFile> Files, int ChangedFileCount,
    int OmittedFiles, SourceReviewDeclarations Before, SourceReviewDeclarations After,
    ImmutableArray<SourceReviewDelta> NewlyObserved, int OmittedDeltas, string Scope);
internal sealed record SourceReviewEvidence(int Version, string InstalledArchive, string CandidateArchive,
    string InstalledManifest, string CandidateManifest, string Fingerprint, SourceReviewReport Report);
internal sealed record SourceReviewReceipt(int Version, string Id, string PlanFingerprint, string ReviewFingerprint,
    string InstalledManifest, string CandidateManifest, string OriginalPayload, string CandidatePayload,
    string ContextFingerprint, string Declaration);

/// Bounded presentation over immutable, hash-verified archive streams. Full raw
/// manifests and blobs remain available independently of presentation omissions.
internal static class SourceUpdateReview
{
    internal const string Declaration = "I reviewed the frozen source changes and reported omissions. This is my review acknowledgement, not authenticity, compatibility or behavioral safety evidence.";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static SourceReviewReport Analyze(PayloadSnapshot before, IPayloadEvidenceReader oldReader,
        PayloadSnapshot after, IPayloadEvidenceReader newReader, CancellationToken token)
    {
        var oldText = Texts(before, oldReader, token);
        var newText = Texts(after, newReader, token);
        var oldEntries = before.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var newEntries = after.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var files = ImmutableArray.CreateBuilder<SourceReviewFile>();
        var count = 0; var linesRemaining = 1024;
        foreach (var path in oldEntries.Keys.Union(newEntries.Keys).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            oldEntries.TryGetValue(path, out var oldEntry); newEntries.TryGetValue(path, out var newEntry);
            if (oldEntry == newEntry) continue;
            count++;
            if (files.Count >= 128) continue;
            var selected = Selected(path);
            var left = oldEntry is null ? "" : oldText.Text.GetValueOrDefault(path);
            var right = newEntry is null ? "" : newText.Text.GetValueOrDefault(path);
            var diff = ""; var omitted = 0;
            var presentation = selected ? "binary, nonregular, unsupported UTF-8 or bounded text; use raw archive evidence" : "type/mode/bytes/raw SHA256 summary; text not selected";
            if (selected && left is not null && right is not null)
            {
                (diff, omitted) = Diff(left, right, Math.Min(128, linesRemaining), token);
                linesRemaining -= diff.Count(c => c == '\n');
                presentation = "literal line replacement hunk; authored bytes retained in archives";
            }
            files.Add(new(path, oldEntry, newEntry, diff, presentation, omitted));
        }
        var previous = Declarations(oldText, token); var next = Declarations(newText, token);
        var keys = previous.Observations.Select(Key).ToHashSet(StringComparer.Ordinal);
        var allDeltas = next.Observations.Where(item => !keys.Contains(Key(item))).ToArray();
        var deltas = allDeltas.Take(128).Select(item => new SourceReviewDelta(item,
            previous.Coverage == "limited" ? "newly-introduced-within-supported-literal-scope" : "newly-observed; previous coverage partial, introduction unproven")).ToImmutableArray();
        return new(files.ToImmutable(), count, count - files.Count, previous, next, deltas,
            allDeltas.Length - deltas.Length + Math.Max(0, next.ObservationCount - next.Observations.Length),
            "Instructions (*.md), selected scripts/manifests and authored tool declarations; literal URLs may be examples. Limited declaration grammar, no runtime connections, installed-dependency, effective-permission, authenticity or safety claims. Root modes are separate plan evidence. Complete raw archives remain accessible; no execution or edits.");
    }
    internal static string Hash(SourceReviewReport report) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(report, SourceReviewJsonContext.Default.SourceReviewReport))).ToLowerInvariant();
    private static string Key(PackageObservation item) => item.Category + "\n" + item.Kind + "\n" + item.Path + "\n" + item.Value;
    private static bool Selected(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).ToLowerInvariant() is ".json" or ".yaml" or ".yml" or ".toml" or ".sh" or ".py" or ".js" or ".ts" or ".ps1" or ".cmd" or ".bat"
        || PackageDeclarationScanner.Select(path);
    private sealed record TextSet(Dictionary<string, string> Text, HashSet<string> Reasons);
    private static TextSet Texts(PayloadSnapshot snapshot, IPayloadEvidenceReader reader, CancellationToken token)
    {
        var result = new TextSet(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        long bytes = 0; var count = 0;
        foreach (var entry in snapshot.Entries.Where(e => Selected(e.Path)).OrderBy(e => e.Path == "SKILL.md" ? 0 : 1).ThenBy(e => e.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var cap = entry.Path == "SKILL.md" ? PayloadSnapshot.MaxSkillBytes : 1024 * 1024;
            if (entry.Kind != "file" || entry.Bytes > cap || ++count > 128 || entry.Bytes > 8 * 1024 * 1024 - bytes)
            { result.Reasons.Add("Selected nonregular or bounded documents omitted; no links followed."); continue; }
            using var stream = new MemoryStream(checked((int)entry.Bytes));
            reader.CopyEntry(entry.Path, stream, token);
            var raw = stream.ToArray(); bytes += raw.Length;
            if (raw.LongLength != entry.Bytes || Convert.ToHexString(SHA256.HashData(raw)) != entry.ContentHash)
                throw new IOException("Review bytes differ from the captured raw file fingerprint.");
            try
            {
                var text = Utf8.GetString(raw);
                if (text.Contains('\0')) result.Reasons.Add("Binary text omitted.");
                else result.Text.Add(entry.Path, text);
            }
            catch (DecoderFallbackException) { result.Reasons.Add("Invalid UTF-8 omitted from text review."); }
        }
        return result;
    }
    private static SourceReviewDeclarations Declarations(TextSet texts, CancellationToken token)
    {
        var observations = ImmutableArray.CreateBuilder<PackageObservation>(); var count = 0;
        var reasons = ImmutableArray.CreateBuilder<string>(); var reasonCount = 0;
        void Unknown(string reason)
        {
            reasonCount++;
            if (reasons.Count < 64) reasons.Add(reason.Length <= 512 ? reason : reason[..512] + " [truncated]");
        }
        foreach (var reason in texts.Reasons.Order(StringComparer.Ordinal)) Unknown(reason);
        void Observe(PackageObservation item)
        {
            count++;
            if (item.Value.Length > 4096) { Unknown("Oversized declaration omitted."); return; }
            if (observations.Count < 256) observations.Add(item);
        }
        foreach (var (path, text) in texts.Text.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            PackageDeclarationScanner.Scan(path, text, Observe, Unknown, _ => { }, token);
            if (Path.GetExtension(path).ToLowerInvariant() is ".json" or ".yaml" or ".yml" or ".toml"
                && !PackageDeclarationScanner.Select(path)) Unknown("Configuration declaration grammar is not modeled; literal text diff remains available.");
            if (path != "SKILL.md") continue;
            var (_, metadata, parsed) = FrontMatterParser.Parse(text);
            if (!parsed || !metadata.Diagnostics.IsEmpty) Unknown("Root frontmatter is not completely supported.");
            if (metadata.Compatibility is { } compatibility)
                Observe(new("dependency", "authored-compatibility", path, null, compatibility, "availability-unknown", "Review prerequisites in the selected environment; nothing was probed."));
            foreach (var tool in metadata.AllowedTools)
                Observe(new("declaration", "allowed-tools", path, null, tool, "text-only", "Authored declaration, not an effective permission grant."));
            PackageDeclarationScanner.HeaderShapes(text, Unknown, token);
        }
        if (count > observations.Count) Unknown("Declaration observation bound omitted values.");
        return new(reasonCount == 0 ? "limited" : "partial", observations.ToImmutable(), count, reasons.ToImmutable(), reasonCount);
    }
    private static (string Text, int Omitted) Diff(string left, string right, int cap, CancellationToken token)
    {
        var a = left.Split('\n'); var b = right.Split('\n'); var first = 0;
        while (first < a.Length && first < b.Length && a[first] == b[first]) { token.ThrowIfCancellationRequested(); first++; }
        var endA = a.Length; var endB = b.Length;
        while (endA > first && endB > first && a[endA - 1] == b[endB - 1]) { token.ThrowIfCancellationRequested(); endA--; endB--; }
        var output = new StringBuilder(); var shown = 0; var omitted = 0;
        if (cap > 0 && (first < endA || first < endB)) output.Append("@@ old line ").Append(first + 1).Append(" / new line ").Append(first + 1).Append(" @@\n");
        Add(a, endA, '-'); Add(b, endB, '+');
        return (output.ToString(), omitted);
        void Add(string[] input, int end, char marker)
        {
            for (var i = first; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                if (shown++ >= Math.Max(0, cap - 1)) { omitted++; continue; }
                var line = input[i];
                output.Append(marker).Append(line.AsSpan(0, Math.Min(1024, line.Length)));
                if (line.Length > 1024) { output.Append(" [line truncated; raw archive retained]"); omitted++; }
                output.Append('\n');
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SourceReviewReport))]
[JsonSerializable(typeof(SourceReviewReceipt))]
internal partial class SourceReviewJsonContext : JsonSerializerContext;
