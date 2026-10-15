using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed record RegistryWriteIntent(int SchemaVersion, string RegistryPath, string Selector,
    RegistryFileSnapshot Before, byte[] DesiredBytes, string DesiredHash, string? BeforeEntryFingerprint, string DesiredEntryFingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegistryWriteBinding? Binding = null);
internal sealed record RegistryWriteBinding(string PlanFingerprint, string Direction, string WriteId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentWriteId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecoveryDirection = null);
internal sealed record RegistryWriteCompletion(int SchemaVersion, RegistryFileSnapshot Current,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegistryWriteBinding? Binding = null);
internal sealed record RegistryWriteResult(bool Succeeded, bool MutationAttempted, bool PreimageRestored,
    bool Canceled, RegistryFileSnapshot? Current, string? Error);

/// Holds the existing data-file lock for an entire owner's payload transition.
/// Write intents retain exact raw before/desired bytes before any truncation.
internal sealed class GhRegistryWriteSession : IDisposable
{
    private readonly NativeRegistryFileLease lease;
    private readonly object gate = new();
    private readonly Logger logger;
    private readonly Action<string>? checkpoint;
    private readonly string selector;
    internal string CanonicalPath => lease.CanonicalPath;
    internal SecureFileIdentity ParentIdentity => lease.ParentIdentity;
    internal GhRegistryWriteSession(string path, string selector, Logger logger, CancellationToken token, Action<string>? checkpoint = null, bool rawRecovery = false)
    {
        this.logger = logger; this.selector = selector; this.checkpoint = checkpoint;
        lease = NativeRegistryFileLease.Open(path, token);
        try { var snapshot = lease.Read(token); if (!rawRecovery) _ = GhRegistryEditor.CaptureEntry(snapshot.Bytes, selector, token); }
        catch { lease.Dispose(); throw; }
    }
    internal RegistryFileSnapshot Read(CancellationToken token) { lock (gate) return lease.Read(token); }

    // Only the plan owner can authorize this whole-image repair. Normal writes
    // still require a valid selected entry and preserve all unrelated bytes.
    internal RegistryWriteResult RepairImage(RegistryFileSnapshot reviewed, byte[] image,
        NativeStateDirectory journal, RegistryWriteBinding binding, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            image = image.ToArray();
            if (binding.Direction != "repair" || binding.RecoveryDirection is not ("forward" or "undo")
                || !QuarantineStore.ValidId(binding.WriteId) || !QuarantineStore.ValidId(binding.ParentWriteId!)
                || !QuarantineStore.ValidHash(binding.PlanFingerprint)) throw new IOException("Invalid reviewed repair binding.");
            var current = lease.Read(token);
            if (current.Identity != reviewed.Identity || current.ContentHash != reviewed.ContentHash)
                throw new IOException("Registry changed after repair review; inspect again.");
            if (!MalformedJson(current.Bytes)) throw new IOException("Whole-image repair is limited to malformed JSON; valid manager changes are preserved.");
            var desired = GhRegistryEditor.CaptureEntry(image, selector, token);
            var intent = new RegistryWriteIntent(1, lease.CanonicalPath, selector, current, image,
                NativeRegistryFileLease.Hash(image), null, desired.Fingerprint, binding);
            Publish(journal, $"registry-{binding.WriteId}-intent.json", intent, RegistryWriteJsonContext.Default.RegistryWriteIntent, token);
            var attempts = lease.WriteAttempts;
            try
            {
                var after = lease.Write(current, image, token, checkpoint);
                Publish(journal, $"registry-{binding.WriteId}-complete.json", new RegistryWriteCompletion(1, after, binding),
                    RegistryWriteJsonContext.Default.RegistryWriteCompletion, token);
                return new(true, true, false, false, after, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Never restore an invalid preimage. The complete valid repair
                // image remains durably bound to this reviewed attempt.
                return new(false, lease.WriteAttempts != attempts, false, ex is OperationCanceledException, null, ex.Message);
            }
        }
    }

    internal static bool MalformedJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var any = false;
        try
        {
            while (reader.Read())
            {
                any = true;
                // Excessive nesting is unsupported evidence, not permission
                // to overwrite a syntactically valid foreign document.
                if (reader.CurrentDepth >= 32) return false;
            }
            return !any;
        }
        catch (JsonException) { return true; }
    }

    internal RegistryWriteResult WriteSelection(string expectedEntryFingerprint, byte[] desiredEntry,
        NativeStateDirectory journal, string writeId, CancellationToken token, RegistryFileSnapshot? expectedCurrent = null,
        RegistryWriteBinding? binding = null)
    {
        lock (gate) return WriteCore(expectedEntryFingerprint, desiredEntry, journal, writeId, token, expectedCurrent, binding);
    }
    private RegistryWriteResult WriteCore(string expectedEntryFingerprint, byte[] desiredEntry,
        NativeStateDirectory journal, string writeId, CancellationToken token, RegistryFileSnapshot? expectedCurrent, RegistryWriteBinding? binding)
    {
        token.ThrowIfCancellationRequested();
        if (!QuarantineStore.ValidId(writeId)) throw new IOException("Registry write ID must be a lowercase 32-character operation ID.");
        if (binding is not null && (binding.WriteId != writeId || !QuarantineStore.ValidHash(binding.PlanFingerprint)
            || binding.Direction is not ("forward" or "undo" or "repair"))) throw new IOException("Registry write plan binding is invalid.");
        var before = lease.Read(token);
        if (expectedCurrent is not null && (before.Identity != expectedCurrent.Identity || before.ContentHash != expectedCurrent.ContentHash))
            throw new IOException("Registry bytes or native evidence changed after the recovery review; inspect again.");
        var patch = GhRegistryEditor.ReplaceEntry(before.Bytes, selector, expectedEntryFingerprint, desiredEntry, token);
        if (patch.DesiredBytes.AsSpan().SequenceEqual(before.Bytes)) return new(true, false, false, false, before, null);
        var intent = new RegistryWriteIntent(1, lease.CanonicalPath, selector, before, patch.DesiredBytes,
            NativeRegistryFileLease.Hash(patch.DesiredBytes), patch.BeforeEntryFingerprint, patch.DesiredEntryFingerprint, binding);
        Publish(journal, $"registry-{writeId}-intent.json", intent, RegistryWriteJsonContext.Default.RegistryWriteIntent, token);
        var attempts = lease.WriteAttempts;
        try
        {
            var after = lease.Write(before, patch.DesiredBytes, token, checkpoint);
            Publish(journal, $"registry-{writeId}-complete.json", new RegistryWriteCompletion(1, after, binding),
                RegistryWriteJsonContext.Default.RegistryWriteCompletion, token);
            return new(true, true, false, false, after, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (lease.WriteAttempts == attempts) return new(false, false, false, ex is OperationCanceledException, null, ex.Message);
            // Only this live lease can compensate automatically. After process
            // death, recovery must review fresh bytes and other managers' work.
            // Even while alive, refuse bytes outside our possible write prefix.
            using var recovery = new CancellationSource(TimeSpan.FromSeconds(15), CancellationCallbackReporter.For(logger, "Registry preimage restoration"));
            try
            {
                var current = lease.Read(recovery.Token);
                if (!NativeDirectoryMoveLease.SameObject(before.Identity, current.Identity)
                    || current.Bytes.Length > patch.DesiredBytes.Length
                    || !patch.DesiredBytes.AsSpan(0, current.Bytes.Length).SequenceEqual(current.Bytes))
                    throw new IOException("Current bytes cannot be attributed to the interrupted write; retained evidence requires explicit review.");
                var restore = new RegistryWriteIntent(1, lease.CanonicalPath, selector, current, before.Bytes,
                    before.ContentHash, null, patch.BeforeEntryFingerprint, binding);
                Publish(journal, $"registry-{writeId}-restore-intent.json", restore, RegistryWriteJsonContext.Default.RegistryWriteIntent, recovery.Token);
                var restored = lease.Write(current, before.Bytes, recovery.Token, step => checkpoint?.Invoke("compensation-" + step));
                Publish(journal, $"registry-{writeId}-restore-complete.json", new RegistryWriteCompletion(1, restored, binding),
                    RegistryWriteJsonContext.Default.RegistryWriteCompletion, recovery.Token);
                return new(false, true, true, ex is OperationCanceledException, restored, ex.Message);
            }
            catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                logger.Warn("maintenance.registry", $"Registry write and preimage restoration need review: {ex.Message}; {restoreError.Message}");
                return new(false, true, false, ex is OperationCanceledException, null, ex.Message + "; preimage restoration: " + restoreError.Message);
            }
        }
    }

    private void Publish<T>(NativeStateDirectory journal, string name, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > 4 * SkillLockFileReader.MaxFileBytes) throw new IOException("Registry intent evidence limit exceeded.");
        using (var file = journal.OpenFile(name, false, token)) { file.Write(bytes); file.Flush(true); }
        journal.FlushPublication(token);
        checkpoint?.Invoke(name);
    }
    public void Dispose() { lock (gate) lease.Dispose(); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, WriteIndented = true)]
[JsonSerializable(typeof(RegistryWriteIntent))]
[JsonSerializable(typeof(RegistryWriteCompletion))]
internal partial class RegistryWriteJsonContext : JsonSerializerContext;
