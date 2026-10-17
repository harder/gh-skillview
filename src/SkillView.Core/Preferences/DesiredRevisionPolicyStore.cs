using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Subprocess;

namespace SkillView.Preferences;

/// Intent outside payloads and runtime metadata. Writer leases are shared with
/// first-apply admission; a missing/unreadable store is never the same outcome.
internal sealed class DesiredRevisionPolicyStore(string root, ProcessRunner? runner = null)
{
    internal const int MaxRecords = 256;
    internal const int MaxRecordBytes = 64 * 1024;
    internal const long MaxBytes = 8 * 1024 * 1024;
    internal string Root { get; } = Path.GetFullPath(root);
    internal static DesiredRevisionPolicyStore For(ProcessRunner runner) => new(Path.Combine(
        Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "desired-state"), runner);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string BindingHash(DesiredPolicyBinding binding) => Hash(JsonSerializer.SerializeToUtf8Bytes(binding,
        DesiredPolicyJsonContext.Default.DesiredPolicyBinding));
    internal static string RecordHash(DesiredPolicyRecord record) => Hash(JsonSerializer.SerializeToUtf8Bytes(record,
        DesiredPolicyJsonContext.Default.DesiredPolicyRecord));

    internal static DesiredPolicyBinding Bind(InstalledSkill target) => new(target.InstallationId,
        target.ResolvedPath, target.SourceIdentity.Key ?? SkillDistributorIdentity.Digest(
            target.SourceIdentity.Provider, target.SourceIdentity.Origin, target.SourceIdentity.Subpath,
            target.SourceIdentity.Evidence, target.SourceIdentity.Conflicted.ToString()), CleanupSuppressionStore.OwnerNamespace(target));

    internal static DesiredPolicyRead ReadFor(ProcessRunner runner, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        try { return For(runner).CaptureRead(exclusions, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new([], "Desired-state policy unavailable: " + ex.Message); }
    }

    internal static DesiredPolicyObservation Match(InstalledSkill target, DesiredPolicyRead read) => Select(read, Bind(target));

    internal DesiredPolicyObservation Observe(InstalledSkill target, IReadOnlyList<string> exclusions, CancellationToken token)
        => Match(target, CaptureRead(exclusions, token));

    private DesiredPolicyRead CaptureRead(IReadOnlyList<string> exclusions, CancellationToken token)
    {
        try
        {
            if (runner is not null) ManagedSkillDiscovery.RefusePaths([Root], token, runner);
            NativeStateDirectory.CheckExcluded(OwnershipPath.CanonicalCandidate(Root) ?? Root,
                exclusions.Select(path => OwnershipPath.CanonicalCandidate(path) ?? Path.GetFullPath(path)).ToArray());
            if (NativeStatePresence.IsMissing(Root, token)) return new([], null);
            using var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token, allowCreate: false);
            using var writer = state.OpenExistingFile("writer.lock", true, token);
            RequireWriter(writer);
            var read = Read(state, token);
            CheckBinding(state, Root, exclusions, token);
            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new([], "Desired-state policy unavailable: " + ex.Message); }
    }

    internal WriterLease Acquire(IReadOnlyList<string> exclusions, CancellationToken token)
    {
        if (runner is not null) ManagedSkillDiscovery.RefusePaths([Root], token, runner);
        var state = NativeStateDirectory.OpenOrCreate(Root, exclusions, token);
        FileStream? writer = null;
        try
        {
            writer = state.OpenFile("writer.lock", true, token);
            RequireWriter(writer);
            var read = Read(state, token);
            CheckBinding(state, Root, exclusions, token);
            return new(state, writer, read, Root, exclusions.ToArray());
        }
        catch { writer?.Dispose(); state.Dispose(); throw; }
    }

    private static void RequireWriter(FileStream writer)
    { if (writer.Length != 0) throw new IOException("Desired-state writer must be empty."); }

    private static void CheckBinding(NativeStateDirectory held, string originalPath,
        IReadOnlyList<string> exclusions, CancellationToken token)
    {
        // Reopen the original configured address, including its ancestor aliases.
        // A canonical-path string alone cannot prove it still selects this store.
        using var current = NativeStateDirectory.OpenOrCreate(originalPath, exclusions, token, allowCreate: false);
        using var heldObject = held.CaptureOwnership(token);
        using var currentObject = current.CaptureOwnership(token);
        var before = heldObject.Capture(token).Identity;
        if (currentObject.Capture(token).Identity != before || heldObject.Capture(token).Identity != before)
            throw new IOException("Desired-state original address no longer identifies its held native policy store.");
    }

    private static DesiredPolicyRead Read(NativeStateDirectory state, CancellationToken token)
    {
        var records = ImmutableArray.CreateBuilder<DesiredPolicyRecord>();
        var entries = state.EnumerateEntries(token).Take(MaxRecords + 2).ToArray();
        if (entries.Length > MaxRecords + 1) throw new IOException("Desired-state record quota exceeded.");
        long bytes = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name == "writer.lock") continue;
            if (entry.Error is not null) throw new IOException(entry.Error);
            bytes = checked(bytes + entry.Bytes);
            if (entry.Bytes is <= 0 or > MaxRecordBytes || bytes > MaxBytes) throw new IOException("Desired-state record budget exceeded.");
            using var stream = state.OpenExistingFile(entry.Name, false, token);
            var envelope = BoundedReviewJson.Read(stream, DesiredPolicyJsonContext.Default.DesiredPolicyEnvelope, MaxRecordBytes, token).Value;
            if (envelope.Body is not { Length: > 0 and <= MaxRecordBytes } || envelope.Sha256 != Hash(envelope.Body))
                throw new IOException("Desired-state record is incomplete or corrupt.");
            using var body = new MemoryStream(envelope.Body, false);
            var record = BoundedReviewJson.Read(body, DesiredPolicyJsonContext.Default.DesiredPolicyRecord, MaxRecordBytes, token).Value;
            Validate(record);
            if (entry.Name != record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json")
                throw new IOException("Desired-state sequence/path mismatch.");
            records.Add(record);
        }
        if (records.Select(record => record.Sequence).Distinct().Count() != records.Count)
            throw new IOException("Desired-state sequence is ambiguous.");
        state.ValidateLocation(token);
        return new(records.OrderBy(record => record.Sequence).ToImmutableArray(), null);
    }

    private static void Validate(DesiredPolicyRecord record)
    {
        if (record.SchemaVersion != 1 || record.Sequence is < 1 or > 999999999999 || record.Binding is null
            || !QuarantineStore.ValidHash(record.Binding.InstallationId) || !Path.IsPathFullyQualified(record.Binding.Path)
            || record.Binding.Source is null || record.Binding.Owners is null || record.Action is not ("set" or "clear")
            || record.Action == "clear" && record.Policy is not null || record.Action == "set" && record.Policy is null)
            throw new IOException("Desired-state record schema or binding is invalid.");
        if (record.Policy is { } policy) ValidatePolicy(policy);
    }

    internal static void ValidatePolicy(DesiredRevisionPolicy policy)
    {
        if (policy.Mode is not ("follow-gh-default" or "follow-chosen-track" or "approved-immutable" or "exact-pin" or "manual-fork" or "native-manager")
            || policy.RequestedRef is { } reference && (string.IsNullOrWhiteSpace(reference) || reference.Length > 1024 || reference.Any(char.IsControl))
            || policy.Mode is "exact-pin" or "follow-chosen-track" && policy.RequestedRef is null
            || (policy.ApprovalId is null) != (policy.ApprovalFingerprint is null)
            || policy.ApprovalId is not null && (!QuarantineStore.ValidId(policy.ApprovalId) || !QuarantineStore.ValidHash(policy.ApprovalFingerprint!))
            || policy.Mode == "approved-immutable" && policy.ApprovalId is null)
            throw new IOException("Desired-state mode, ref or immutable approval is incomplete.");
    }

    private static DesiredPolicyObservation Select(DesiredPolicyRead read, DesiredPolicyBinding binding)
    {
        if (read.Diagnostic is { } error) return new("unknown", error, null, null);
        var current = read.Records.LastOrDefault(record => record.Binding.InstallationId == binding.InstallationId
            || PathIdentity.Equals(record.Binding.Path, binding.Path));
        if (current is null)
            return new("absent", "No desired-state policy is recorded at this exact installation address.", null,
                Hash(Encoding.UTF8.GetBytes("SkillView.desired-policy.default.v1\n" + BindingHash(binding))));
        if (current.Action == "clear")
            return new("absent", "Desired-state intent was explicitly cleared; actual pins and runtime metadata remain independent.", null,
                Hash(Encoding.UTF8.GetBytes("SkillView.desired-policy.cleared.v1\n" + RecordHash(current) + "\n" + BindingHash(binding))), current.Sequence);
        if (current.Binding != binding)
            return new("stale-binding", "Desired-state policy belongs to a different source, owner context or installation binding; review explicitly.", current.Policy, RecordHash(current), current.Sequence);
        return new("active", "Recorded desired-state intent; runtime activation is unobserved.", current.Policy, RecordHash(current), current.Sequence);
    }

    // A clear or rebind closes the earlier address even when its locally
    // derived installation ID changed. The bounded log is the only input.
    private static IEnumerable<DesiredPolicyRecord> LatestAddresses(DesiredPolicyRead read)
    {
        var seen = new List<DesiredPolicyBinding>();
        foreach (var record in read.Records.Reverse())
        {
            if (seen.Any(binding => binding.InstallationId == record.Binding.InstallationId
                || PathIdentity.Equals(binding.Path, record.Binding.Path))) continue;
            seen.Add(record.Binding);
            yield return record;
        }
    }

    internal sealed class WriterLease(NativeStateDirectory state, FileStream writer, DesiredPolicyRead read,
        string originalPath, IReadOnlyList<string> exclusions) : IDisposable
    {
        private DesiredPolicyRead current = read;
        internal string Address => state.CanonicalPath;
        internal void Revalidate(CancellationToken token) => CheckBinding(state, originalPath, exclusions, token);
        private DesiredPolicyRead ReadCurrent(CancellationToken token)
        {
            var result = Read(state, token);
            Revalidate(token);
            return result;
        }
        internal void RefuseProtectedInstallDestinations(IReadOnlyList<string> destinations, CancellationToken token)
        {
            current = ReadCurrent(token);
            foreach (var record in LatestAddresses(current).Where(item => item.Action == "set"))
            {
                token.ThrowIfCancellationRequested();
                if (destinations.Any(path => PathIdentity.IsInside(record.Binding.Path, OwnershipPath.CanonicalCandidate(path) ?? path)))
                    throw new IOException("An admitted install root contains explicit desired-state policy. Use a saved exact-target workflow; selector-to-native placement is not proven for protected siblings.");
            }
        }
        internal DesiredPolicyObservation Observe(InstalledSkill target, CancellationToken token)
        {
            current = ReadCurrent(token);
            return Select(current, Bind(target));
        }

        internal DesiredPolicyObservation Set(InstalledSkill target, DesiredRevisionPolicy? policy, string expected, CancellationToken token)
        {
            if (policy is not null) ValidatePolicy(policy);
            var before = Observe(target, token);
            if (before.Fingerprint != expected) throw new IOException("Desired-state review changed; inspect again before publication.");
            if (!target.OwnershipDiagnostics.IsEmpty || !target.MetadataConflicts.IsEmpty)
                throw new IOException("Desired-state publication requires complete unambiguous source/owner observations.");
            GhSharedLockGuard.ValidateCapturedOwners([target], token);
            var payload = PayloadSnapshot.Capture(target.ResolvedPath, token);
            if (!PathIdentity.Equals(payload.Path, target.ResolvedPath)
                || payload.FrontMatter is not { } metadata || !metadata.Diagnostics.IsEmpty
                || Bind(target with { FrontMatter = metadata }) != Bind(target))
                throw new IOException("Selected installation source changed before desired-state publication.");
            var binding = Bind(target);
            var record = new DesiredPolicyRecord(1, current.Records.IsEmpty ? 1 : checked(current.Records[^1].Sequence + 1),
                policy is null ? "clear" : "set", binding, policy);
            Validate(record);
            var active = LatestAddresses(current).Count(item => item.Action == "set"
                && item.Binding.InstallationId != binding.InstallationId && !PathIdentity.Equals(item.Binding.Path, binding.Path))
                + (policy is null ? 0 : 1);
            if (current.Records.Length + 1 + active > MaxRecords)
                throw new IOException("Desired-state record quota exhausted; clear capacity is reserved for each active installation.");
            var body = JsonSerializer.SerializeToUtf8Bytes(record, DesiredPolicyJsonContext.Default.DesiredPolicyRecord);
            var envelope = new DesiredPolicyEnvelope(body, Hash(body));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, DesiredPolicyJsonContext.Default.DesiredPolicyEnvelope);
            if (bytes.Length > MaxRecordBytes || state.EnumerateFiles(token).Sum(entry => entry.Bytes) + bytes.Length + (long)active * MaxRecordBytes > MaxBytes)
                throw new IOException("Desired-state publication budget exceeded.");
            using (var file = state.OpenFile(record.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".json", false, token))
            { token.ThrowIfCancellationRequested(); file.Write(bytes); file.Flush(true); }
            state.FlushPublication(token);
            return Observe(target, token);
        }

        public void Dispose() { writer.Dispose(); state.Dispose(); }
    }
}
