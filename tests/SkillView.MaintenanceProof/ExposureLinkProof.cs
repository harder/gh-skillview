using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Threading;

internal static class ExposureLinkProof
{
    internal static int Run(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException("This explicit preservation proof requires Linux.");
        if (!SecureRemovalBackend.TryCanonicalizePath(Path.GetFullPath(path), out var root, out var error))
            throw new IOException("Cannot bind disposable exposure proof root: " + error);
        if (!SecureRemovalBackend.TryCanonicalizePath(Environment.CurrentDirectory, out var cwd, out _)
            || !PathIdentity.Equals(root, cwd)
            || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable exposure link proof\n"
            || !PrivateDirectory(Environment.GetEnvironmentVariable("HOME"))
            || !PrivateDirectory(Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME")))
            throw new IOException("A marked disposable HOME/CWD/state workspace is required.");
        var logger = new Logger();
        using var lifetime = new CancellationSource(TimeSpan.FromMinutes(2), CancellationCallbackReporter.For(logger, "Exposure link proof"));
        var token = lifetime.Token;
        var targets = Path.Combine(root, "targets");
        var exposures = Path.Combine(root, "exposures");
        var payload = Path.Combine(targets, "payload");
        var link = Path.Combine(exposures, "demo");
        var statePath = Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME")!;
        LinuxExposureOwnerRequest request;
        using (var target = NativeDirectoryMoveLease.Open(targets, payload, token))
        using (var reviewed = NativeExposureLinkLease.Open(exposures, link, target, token))
        {
            var captured = target.Capture(token);
            request = new(exposures, link, targets, payload, reviewed.OriginalDirectory, reviewed.Original.Identity,
                reviewed.Original.RawTarget, reviewed.Original.MetadataFingerprint, target.RootIdentity, target.ParentIdentity,
                target.TargetIdentity, captured.Fingerprint,
                captured.ObjectStateFingerprint ?? throw new IOException("Complete native target evidence is required."),
                statePath, "retained-link", TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        }
        // This is an isolated primitive fixture intent, not a saved detach plan.
        // Every caller native object is closed before the owner starts.
        using (var state = NativeStateDirectory.OpenOrCreate(statePath, [targets, exposures], token, allowCreate: false))
        using (var writer = state.OpenFile("writer.lock", true, token))
        {
            var intentBytes = JsonSerializer.SerializeToUtf8Bytes(request, ExposureLinkProofJsonContext.Default.LinuxExposureOwnerRequest);
            if (intentBytes.Length > 64 * 1024) throw new IOException("Disposable primitive intent exceeds its bound.");
            using (var intent = state.OpenFile("roundtrip-intent.json", false, token))
            {
                intent.SetLength(0);
                intent.Write(intentBytes);
                intent.Flush(flushToDisk: true);
            }
            state.FlushPublication(token);
        }
        using var owner = LinuxExposureOwnerContext.Start(request);
        var ready = RequireObservation(owner.Ready.GetAwaiter().GetResult(), LinuxExposureLinkPosition.Original, moved: false);
        var original = ready.Original;
        if (original.Identity != request.ExpectedLink || !original.RawTarget.AsSpan().SequenceEqual(request.ExpectedRawTarget.AsSpan()))
            throw new IOException("Owned authority differs from the reviewed original link.");
        var observation = original.Preservation ?? throw new IOException("No typed preservation observation was captured.");
        var retained = RequireObservation(owner.RetainAsync(token).GetAwaiter().GetResult(), LinuxExposureLinkPosition.Retained, moved: true);
        RequireSameObject(retained.Current);
        var restored = RequireObservation(owner.RestoreAsync(token).GetAwaiter().GetResult(), LinuxExposureLinkPosition.Restored, moved: true);
        RequireSameObject(restored.Current);
        var after = RequireObservation(owner.InspectAsync(token).GetAwaiter().GetResult(), LinuxExposureLinkPosition.Restored, moved: false);
        NativeExposureLinkLease.SameLink(restored.Current, after.Current, sameGeneration: true);
        owner.Dispose();
        var cleanup = owner.Completion.GetAwaiter().GetResult();
        if (cleanup.ResourcesOpened != cleanup.ResourcesReleased || !cleanup.Diagnostics.IsEmpty || cleanup.OmittedDiagnostics != 0
            || cleanup.MoveState is not { LiveContinuity: false, Uncertain: false, Position: LinuxExposureLinkPosition.Restored })
            throw new IOException("Owned native cleanup did not release the restored operation cleanly.");
        var report = new ExposureLinkProofReport(1, "managed-maintenance-proof", "LinuxSameOriginalObjectPreservation",
            false, observation.Coverage, observation.Continuity, observation.VisibleAttributesFingerprint,
            Convert.ToHexStringLower(SHA256.HashData(original.RawTarget.AsSpan())), true, true, true,
            original.Identity.Volume, original.Identity.FileIdLow, request.TargetFingerprint, after.TargetFingerprint);
        Console.WriteLine(JsonSerializer.Serialize(report, ExposureLinkProofJsonContext.Default.ExposureLinkProofReport));
        return 0;

        LinuxExposureOwnerObservation RequireObservation(LinuxExposureOwnerOutcome outcome, LinuxExposureLinkPosition position, bool moved)
        {
            if (!outcome.Succeeded || outcome.Canceled || outcome.Observation is not { } value
                || outcome.MoveState is not { LiveContinuity: true, Uncertain: false } move
                || move.Position != position || move.MutationAttempted != moved || move.MutationCompleted != moved
                || value.RetainedSlotEmpty != (position != LinuxExposureLinkPosition.Retained)
                || value.TargetFingerprint != request.TargetFingerprint || value.TargetObjectFingerprint != request.TargetObjectFingerprint
                || value.Original.Metadata.Supported || value.Current.Metadata.Supported
                || value.Original.Preservation is null || value.Current.Preservation is null)
                throw new IOException("Owned native observation or mutation result failed the primitive proof: " + outcome.Error);
            return value;
        }
        void RequireSameObject(ExposureLinkEvidence current)
        {
            if (!NativeDirectoryMoveLease.SameObject(original.Identity, current.Identity)
                || !original.RawTarget.AsSpan().SequenceEqual(current.RawTarget.AsSpan()))
                throw new IOException("Owned retention/restore did not preserve the exact original link.");
        }
        bool PrivateDirectory(string? candidate) => candidate is not null && Directory.Exists(candidate)
            && SecureRemovalBackend.TryCanonicalizePath(candidate, out var canonical, out _)
            && PathIdentity.IsInside(canonical, root) && !PathIdentity.Equals(canonical, root);
    }
}

internal sealed record ExposureLinkProofReport(int SchemaVersion, string HostKind, string Mode,
    bool FullMetadataSupported, string Coverage, string Continuity, string VisibleAttributesFingerprint,
    string RawTargetSha256, bool RetainedSameNativeObject, bool RestoredSameNativeObject, bool TargetUnchanged,
    ulong VolumeId, ulong FileIdLow, string TargetBeforeFingerprint, string TargetAfterFingerprint);

[JsonSerializable(typeof(ExposureLinkProofReport))]
[JsonSerializable(typeof(LinuxExposureOwnerRequest))]
internal partial class ExposureLinkProofJsonContext : JsonSerializerContext;
