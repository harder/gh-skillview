using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private const int MaxResolutionArchives = 16;
    private static bool IsResolutionArchive(string name) => Enumerable.Range(0, MaxResolutionArchives).Any(i => name == $"resolutions-{i:D2}");
    private sealed record ResolvedFile(ResolutionEntryReference Reference, CapturedResolutionArchive Archive, ArchivedPayloadEntry Entry);
    private sealed record ResolutionCapture(string Hash, CapturedResolutionArchive Archive, IReadOnlyDictionary<string, ArchivedPayloadEntry> Entries);

    private LocalReviewSelection CaptureResolutionSelection(Session session, NativeStateDirectory draft, LocalReviewEvidence evidence,
        IReadOnlyList<LocalPathChoice> choices, IReadOnlyList<LocalResolutionInput> inputs, InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = evidence.Recorded.Snapshot.Entries.Concat(evidence.Installed.Snapshot.Entries).Concat(evidence.Source.Snapshot.Entries)
            .Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        if (choices.Count > PayloadSnapshot.MaxEntries + 1 || choices.Any(choice => choice is null || string.IsNullOrEmpty(choice.Path)
                || choice.Side is not ("installed" or "source" or "resolved") || choice.Path != "/" && !paths.Contains(choice.Path)
                || choice.Path == "/" && choice.Side == "resolved")
            || choices.Select(choice => choice.Path).Distinct(StringComparer.Ordinal).Count() != choices.Count)
            throw new IOException("Resolution choices need distinct captured target paths.");
        var selected = choices.ToDictionary(choice => choice.Path, choice => choice.Side, StringComparer.Ordinal);
        if (inputs.Count > PayloadSnapshot.MaxEntries || inputs.Any(input => input is null || string.IsNullOrEmpty(input.Path)
                || string.IsNullOrWhiteSpace(input.SourceFile) || selected.GetValueOrDefault(input.Path) != "resolved")
            || inputs.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
            throw new IOException("Resolution inputs need distinct captured target paths, each bound to one explicit resolved choice.");
        var byInput = inputs.ToDictionary(input => input.Path, StringComparer.Ordinal);
        var references = new Dictionary<string, ResolutionEntryReference>(StringComparer.Ordinal);
        foreach (var choice in choices.Where(choice => choice.Side == "resolved" && !byInput.ContainsKey(choice.Path)))
        {
            if (!evidence.Resolutions.TryGetValue(choice.Path, out var prior))
                throw new IOException("There is no previously reviewed frozen resolution to reuse: " + choice.Path);
            references.Add(choice.Path, prior.Reference);
        }
        if (inputs.Count != 0)
        {
            var (usedBytes, usedManifestBytes, attempts) = ResolutionBudget(draft, token);
            var name = Enumerable.Range(0, MaxResolutionArchives).Select(i => $"resolutions-{i:D2}")
                .FirstOrDefault(candidate => !attempts.Contains(candidate))
                ?? throw new IOException("Resolution capture history is full; retained partial evidence needs inspection or a new revision plan.");
            var hash = CapturedResolutionArchive.Capture(draft, name, inputs,
                CapturedResolutionArchive.MaxRetainedBytes - usedBytes, CapturedResolutionArchive.MaxRetainedManifestBytes - usedManifestBytes, token);
            // Persist the new archive's name and contents before any choice can
            // reference it. Reopened draft has no writer of its own; the held
            // operation supplies the device barrier after this namespace flush.
            draft.FlushNamespace(token); session.Operation.FlushPublication(token);
            foreach (var input in inputs) references.Add(input.Path, new(input.Path, name, hash));
            RequireLocalReviewReady(session, session.Plan.ReviewFingerprint, inventory, token);
        }
        return new(references.Count == 0 ? 1 : 2, evidence.HeaderFingerprint, choices.ToImmutableArray(),
            references.Count == 0 ? null : new(references.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray()));
    }

    private static (long Bytes, long ManifestBytes, HashSet<string> Attempts) ResolutionBudget(NativeStateDirectory draft, CancellationToken token)
    {
        const int maxDraftEntries = MaxLocalSelections + MaxResolutionArchives + 4;
        var outer = draft.EnumerateEntries(token).Take(maxDraftEntries + 1).ToArray();
        if (outer.Length > maxDraftEntries) throw new IOException("Local review entry count exceeds its bounded history budget.");
        var attempts = outer.Where(item => IsResolutionArchive(item.Name)).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        long bytes = 0; long metadata = 0;
        foreach (var name in attempts)
        {
            token.ThrowIfCancellationRequested();
            using var archive = OpenArchive(draft, name, token);
            var entries = archive.EnumerateEntries(token).Take(PayloadSnapshot.MaxEntries + 2).ToArray();
            if (entries.Length > PayloadSnapshot.MaxEntries + 1 || entries.Any(item => item.Error is not null || item.Bytes < 0
                || item.Name != "manifest.json" && (item.Name.Length != 11 || !item.Name.StartsWith("entry-", StringComparison.Ordinal)
                    || item.Name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') >= 0)))
                throw new IOException("Retained resolution attempt has unknown or unsafe entries.");
            foreach (var entry in entries)
            {
                if (entry.Name == "manifest.json") metadata = checked(metadata + entry.Bytes);
                else bytes = checked(bytes + entry.Bytes);
            }
            if (bytes > CapturedResolutionArchive.MaxRetainedBytes || metadata > CapturedResolutionArchive.MaxRetainedManifestBytes)
                throw new IOException("Retained resolution attempts exceed their cumulative byte/manifest budget.");
        }
        return (bytes, metadata, attempts);
    }

    private static ImmutableDictionary<string, ResolvedFile> LoadResolutions(NativeStateDirectory draft, LocalReviewSelection selection,
        Dictionary<string, ResolutionCapture> archives, CancellationToken token)
    {
        if (!(selection.SchemaVersion == 1 && selection.Resolutions is null || selection.SchemaVersion == 2 && selection.Resolutions is not null)
            || selection.Choices.IsDefault || selection.Choices.Any(choice => choice is null))
            throw new IOException("Local choice schema does not match its resolution evidence.");
        if (selection.Resolutions is null)
        {
            if (selection.Choices.Any(choice => choice.Side == "resolved")) throw new IOException("Resolved choices require captured file evidence.");
            return ImmutableDictionary<string, ResolvedFile>.Empty;
        }
        var references = selection.Resolutions.Entries;
        if (references.IsDefaultOrEmpty || references.Length > PayloadSnapshot.MaxEntries
            || references.Any(item => item is null || string.IsNullOrEmpty(item.Path) || !IsResolutionArchive(item.Archive)
                || !QuarantineStore.ValidHash(item.ManifestFingerprint)))
            throw new IOException("Resolution references are malformed or exceed their budget.");
        var selected = selection.Choices.Where(choice => choice.Side == "resolved").Select(choice => choice.Path).ToHashSet(StringComparer.Ordinal);
        var result = ImmutableDictionary.CreateBuilder<string, ResolvedFile>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (!selected.Contains(reference.Path) || result.ContainsKey(reference.Path))
                throw new IOException("Every captured resolution must be bound once to its exact selected target path.");
            if (!archives.TryGetValue(reference.Archive, out var captured))
            {
                using var archive = OpenArchive(draft, reference.Archive, token);
                var content = CapturedResolutionArchive.Load(archive, reference.ManifestFingerprint, token);
                captured = new(reference.ManifestFingerprint, content, content.Entries.ToDictionary(item => item.Entry.Path, StringComparer.Ordinal));
                archives.Add(reference.Archive, captured);
            }
            if (captured.Hash != reference.ManifestFingerprint) throw new IOException("A resolution archive has conflicting reviewed identities.");
            if (!captured.Entries.TryGetValue(reference.Path, out var entry)) throw new IOException("A referenced resolution target was not captured.");
            result.Add(reference.Path, new(reference, captured.Archive, entry));
        }
        if (!selected.SetEquals(result.Keys)) throw new IOException("One or more resolved choices have no exact captured file.");
        return result.ToImmutable();
    }

    private static ResolutionReaders OpenResolutionReaders(NativeStateDirectory draft, ImmutableDictionary<string, ResolvedFile> files,
        CancellationToken token) => new(draft, files, token);

    private sealed class ResolutionReaders : IPayloadEvidenceReader, IDisposable
    {
        private readonly List<NativeStateDirectory> held = [];
        private readonly Dictionary<string, IPayloadEvidenceReader> byPath = new(StringComparer.Ordinal);
        internal ResolutionReaders(NativeStateDirectory draft, ImmutableDictionary<string, ResolvedFile> files, CancellationToken token)
        {
            try
            {
                foreach (var group in files.GroupBy(item => item.Value.Reference.Archive, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var directory = OpenArchive(draft, group.Key, token); held.Add(directory);
                    var reader = group.First().Value.Archive.OpenReader(directory);
                    foreach (var file in group) byPath.Add(file.Key, reader);
                }
            }
            catch { Dispose(); throw; }
        }
        private IPayloadEvidenceReader Reader(string path) => byPath.TryGetValue(path, out var reader) ? reader
            : throw new IOException("The selected resolution has no captured reader.");
        public PayloadEntry Entry(string path) => Reader(path).Entry(path);
        public PayloadNativeMetadata? Metadata(string path) => Reader(path).Metadata(path);
        public void CopyEntry(string path, Stream destination, CancellationToken token) => Reader(path).CopyEntry(path, destination, token);
        public void Dispose() { foreach (var directory in held.AsEnumerable().Reverse()) directory.Dispose(); held.Clear(); }
    }
}
