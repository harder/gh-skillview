using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Maintenance;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Inventory;

internal sealed record ExposureLinkEvidence(SecureFileIdentity Identity, ImmutableArray<byte> RawTarget,
    PayloadNativeMetadata Metadata, string MetadataFingerprint, LinkPreservationObservation? Preservation = null);

internal sealed record ExposureDirectoryEvidence(SecureFileIdentity Root, SecureFileIdentity Parent,
    ImmutableSortedDictionary<string, string> Entries);

/// A primitive only: callers must supply placement/owner policy and a durable
/// intent before calling either move. There is no unlink/recreate fallback.
internal sealed class NativeExposureLinkLease : IDisposable
{
    private readonly NativeExposureDirectory source;
    private readonly NativeDirectoryMoveLease target;
    private readonly string name;
    private readonly NativeExposureLinkPin linkPin;
    private readonly LinuxLinkProcAuthority? linuxAuthority;
    private readonly Logger logger;
    private readonly Action<AggregateException>? ownerCallbackReporter;
    private ExposureDirectoryEvidence sourceEvidence;
    private ExposureLinkEvidence current;
    private NativeRetainedLinkSlot? retained;
    private ExposureDirectoryEvidence? retainedEvidence;
    private bool uncertain;
    private bool restored;
    internal ExposureLinkEvidence Original { get; }
    internal ExposureLinkEvidence Current => current;
    internal ExposureDirectoryEvidence OriginalDirectory { get; }
    internal SecureFileIdentity TargetIdentity { get; }
    internal bool MutationAttempted { get; private set; }
    internal bool MutationCompleted { get; private set; }
    internal int MutationSequence { get; private set; }
    internal bool IsUncertain => uncertain;
    internal LinuxExposureLinkPosition Position => restored ? LinuxExposureLinkPosition.Restored
        : retained is not null ? LinuxExposureLinkPosition.Retained : LinuxExposureLinkPosition.Original;

    private NativeExposureLinkLease(NativeExposureDirectory source, string name, NativeDirectoryMoveLease target,
        ExposureDirectoryEvidence directory, ExposureLinkEvidence link, NativeExposureLinkPin linkPin, LinuxLinkProcAuthority? linuxAuthority, Logger? logger,
        Action<AggregateException>? ownerCallbackReporter)
    {
        this.source = source; this.name = name; this.target = target;
        this.linkPin = linkPin; this.linuxAuthority = linuxAuthority; this.logger = logger ?? new Logger();
        this.ownerCallbackReporter = ownerCallbackReporter;
        sourceEvidence = OriginalDirectory = directory; Original = current = link;
        TargetIdentity = target.TargetIdentity;
    }

    internal static NativeExposureLinkLease Open(string root, string entry, NativeDirectoryMoveLease target, CancellationToken token)
        => Open(root, entry, target, ExposureLinkRetentionMode.FullMetadataVerified, token);

    internal static NativeExposureLinkLease Open(string root, string entry, NativeDirectoryMoveLease target,
        ExposureLinkRetentionMode mode, CancellationToken token, Logger? logger = null)
    {
        if (mode == ExposureLinkRetentionMode.LinuxSameOriginalObjectPreservation)
            throw new IOException("Linux original-object preservation requires the dedicated native owner context; borrowed target admission is refused.");
        return OpenCore(root, entry, target, mode, token, logger, null);
    }

    internal static NativeExposureLinkLease OpenOwnedLinux(LinuxExposureOwnerContext.NativeAuthority owner,
        string root, string entry, NativeDirectoryMoveLease target, CancellationToken token, Action<AggregateException> callbackReporter)
    {
        owner.Verify();
        return OpenCore(root, entry, target, ExposureLinkRetentionMode.LinuxSameOriginalObjectPreservation, token, null, callbackReporter);
    }

    private static NativeExposureLinkLease OpenCore(string root, string entry, NativeDirectoryMoveLease target,
        ExposureLinkRetentionMode mode, CancellationToken token, Logger? logger, Action<AggregateException>? ownerCallbackReporter)
    {
        if (!Enum.IsDefined(mode)) throw new IOException("Unknown link retention mode.");
        LinuxLinkProcAuthority? linux = mode == ExposureLinkRetentionMode.LinuxSameOriginalObjectPreservation
            ? LinuxLinkProcAuthority.Open(token) : null;
        NativeExposureDirectory? authority = null;
        NativeExposureLinkPin? pin = null;
        try
        {
            linux?.Check(token); target.Revalidate(token);
            authority = NativeExposureDirectory.Open(root, entry, token);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
            var directory = authority.Capture(token);
            var link = authority.ReadLink(name, token);
            pin = authority.PinLink(name, link, token);
            link = authority.ReadLink(name, pin, linux, token);
            RequireTarget(authority.ResolveLink(name, token), target.TargetIdentity);
            RequireTarget(authority.ResolveRawTarget(link.RawTarget.AsSpan(), token), target.TargetIdentity);
            target.Revalidate(token); SameDirectory(directory, authority.Capture(token));
            SameLink(link, authority.ReadLink(name, pin, linux, token), sameGeneration: true);
            NativeExposureDirectory.RequireEntry(directory, name, NativeExposureDirectory.EntryHash(link));
            if (PathIdentity.IsInside(link.Identity.CanonicalPath, target.CurrentPath))
                throw new IOException("An exposure inside its target payload cannot be retained.");
            var result = new NativeExposureLinkLease(authority, name, target, directory, link, pin, linux, logger, ownerCallbackReporter);
            result.ValidateLinuxAuthorities(null, token);
            pin = null; linux = null; return result;
        }
        catch { pin?.Dispose(); authority?.Dispose(); linux?.Dispose(); throw; }
    }

    internal ExposureLinkEvidence Retain(NativeRetainedLinkSlot slot, CancellationToken token)
    {
        RequireAvailable(token);
        if (retained is not null || restored) throw new IOException("This live link was already retained.");
        RequireMetadata(current);
        source.Validate(sourceEvidence, token); target.Revalidate(token);
        SameLink(current, ReadLink(source, name, token), true);
        RequireTarget(source.ResolveLink(name, token), TargetIdentity);
        ValidateLinuxAuthorities(slot.Directory, token);
        var destination = slot.Directory.Capture(token);
        if (!destination.Entries.IsEmpty) throw new IOException("The retained-link slot is not empty.");
        if (PathIdentity.IsInside(slot.CanonicalPath, sourceEvidence.Root.CanonicalPath)
            || PathIdentity.IsInside(slot.CanonicalPath, target.RootIdentity.CanonicalPath))
            throw new IOException("Retained links must be outside exposure and target scan roots.");
        // These strict target leases cannot advance generations caused by this
        // primitive. Refuse shared parents instead of refreshing their witness.
        foreach (var parent in new[] { sourceEvidence.Parent, destination.Parent })
            if (NativeDirectoryMoveLease.SameObject(parent, target.RootIdentity)
                || NativeDirectoryMoveLease.SameObject(parent, target.ParentIdentity))
                throw new IOException("A shared target/exposure parent requires an explicit coordinated generation transition.");
        return Move(source, name, sourceEvidence, slot.Directory, NativeRetainedLinkSlot.EntryName, destination, slot, false, token);
    }

    internal void Revalidate(CancellationToken token)
    {
        RequireAvailable(token); ValidateLinuxAuthorities(retained?.Directory, token); target.Revalidate(token); source.Validate(sourceEvidence, token);
        if (retained is null || restored)
        {
            SameLink(current, ReadLink(source, name, token), true);
            RequireTarget(source.ResolveLink(name, token), TargetIdentity);
        }
        else
        {
            retained.Directory.Validate(retainedEvidence!, token);
            SameLink(current, ReadLink(retained.Directory, NativeRetainedLinkSlot.EntryName, token), true);
            RequireTarget(source.ResolveRawTarget(current.RawTarget.AsSpan(), token), TargetIdentity);
        }
    }

    internal ExposureLinkEvidence Restore(CancellationToken token)
    {
        RequireAvailable(token);
        if (retained is null || retainedEvidence is null || restored) throw new IOException("There is no retained link to restore.");
        ValidateLinuxAuthorities(retained.Directory, token);
        RequireMetadata(current); target.Revalidate(token);
        retained.Directory.Validate(retainedEvidence, token); source.Validate(sourceEvidence, token);
        if (sourceEvidence.Entries.ContainsKey(name)) throw new IOException("The original exposure slot is occupied.");
        SameLink(current, ReadLink(retained.Directory, NativeRetainedLinkSlot.EntryName, token), true);
        // Relative targets are interpreted at the ORIGINAL held parent.
        RequireTarget(source.ResolveRawTarget(current.RawTarget.AsSpan(), token), TargetIdentity);
        return Move(retained.Directory, NativeRetainedLinkSlot.EntryName, retainedEvidence,
            source, name, sourceEvidence, retained, true, token);
    }

    private ExposureLinkEvidence Move(NativeExposureDirectory from, string fromName, ExposureDirectoryEvidence fromBefore,
        NativeExposureDirectory to, string toName, ExposureDirectoryEvidence toBefore, NativeRetainedLinkSlot slot, bool undo, CancellationToken token)
    {
        using var fromReferences = linuxAuthority is null ? null : from.HoldLinuxLinkAuthorities();
        using var toReferences = linuxAuthority is null ? null : to.HoldLinuxLinkAuthorities();
        from.Validate(fromBefore, token); to.Validate(toBefore, token); target.Revalidate(token);
        NativeExposureDirectory.RequireEntry(fromBefore, fromName, NativeExposureDirectory.EntryHash(current));
        if (toBefore.Entries.ContainsKey(toName)) throw new IOException("The destination entry is occupied.");
        token.ThrowIfCancellationRequested();
        uncertain = true; MutationAttempted = true; MutationCompleted = false; MutationSequence++;
        from.MoveExclusive(fromName, current, fromBefore, to, toName, toBefore,
            linuxAuthority is null ? null : ct =>
            {
                ValidateLinuxAuthorities(to, ct); ValidateLinuxAuthorities(from, ct);
                SameLink(current, ReadLink(from, fromName, ct), true);
                RequireTarget(source.ResolveRawTarget(current.RawTarget.AsSpan(), ct), TargetIdentity);
            }, token);
        using var deadline = linuxAuthority is null ? null : new CancellationSource(TimeSpan.FromSeconds(30),
            ownerCallbackReporter ?? CancellationCallbackReporter.For(logger, "Linux link post-rename reconciliation"));
        var reconcile = deadline?.Token ?? CancellationToken.None;
        // Cancellation after a native move must not hide its observation. The
        // caller's durable journal owns reconciliation if any check fails.
        var afterLink = ReadLink(to, toName, reconcile);
        SameLink(current, afterLink, false); RequireMetadata(afterLink);
        var fromAfter = from.Capture(reconcile);
        var toAfter = to.Capture(reconcile);
        NativeExposureDirectory.VerifyTransition(fromBefore, fromAfter, fromName, added: false, NativeExposureDirectory.EntryHash(current));
        NativeExposureDirectory.VerifyTransition(toBefore, toAfter, toName, added: true, NativeExposureDirectory.EntryHash(afterLink));
        target.Revalidate(reconcile); ValidateLinuxAuthorities(to, reconcile); ValidateLinuxAuthorities(from, reconcile);
        RequireTarget(source.ResolveRawTarget(afterLink.RawTarget.AsSpan(), reconcile), TargetIdentity);
        if (undo) RequireTarget(source.ResolveLink(name, reconcile), TargetIdentity);
        from.Flush(reconcile); to.Flush(reconcile);
        from.Validate(fromAfter, reconcile); to.Validate(toAfter, reconcile);
        SameLink(afterLink, ReadLink(to, toName, reconcile), true);
        to.Validate(toAfter, reconcile);
        if (undo) { sourceEvidence = toAfter; retainedEvidence = fromAfter; restored = true; }
        else { sourceEvidence = fromAfter; retainedEvidence = toAfter; retained = slot; }
        current = afterLink; uncertain = false; MutationCompleted = true;
        token.ThrowIfCancellationRequested();
        return current;
    }

    private void RequireAvailable(CancellationToken token)
    { token.ThrowIfCancellationRequested(); linuxAuthority?.Check(token); if (uncertain) throw new IOException("A prior native link move needs external journal reconciliation; no retry is admitted."); }
    private void RequireMetadata(ExposureLinkEvidence evidence)
    { if (linuxAuthority is not null) { if (evidence.Preservation is null) throw new IOException("Missing typed Linux preservation evidence."); return; } if (!evidence.Metadata.Supported) throw new IOException("Link metadata cannot be fully verified: " + string.Join(" ", evidence.Metadata.Refusals)); }
    private static void RequireTarget(SecureFileIdentity actual, SecureFileIdentity expected)
    { if (actual != expected) throw new IOException("The exposure does not resolve to the exact held target identity/generation/address."); }
    private static void SameDirectory(ExposureDirectoryEvidence expected, ExposureDirectoryEvidence actual)
    { if (expected.Root != actual.Root || expected.Parent != actual.Parent || !expected.Entries.SequenceEqual(actual.Entries)) throw new IOException("Exposure namespace changed."); }
    internal static void SameLink(ExposureLinkEvidence expected, ExposureLinkEvidence actual, bool sameGeneration)
    {
        if (!(sameGeneration ? expected.Identity == actual.Identity : NativeDirectoryMoveLease.SameObject(expected.Identity, actual.Identity))
            || !expected.RawTarget.AsSpan().SequenceEqual(actual.RawTarget.AsSpan())
            || (expected.Preservation is null && actual.Preservation is null ? expected.MetadataFingerprint != actual.MetadataFingerprint
                : !SamePreservation(expected.Preservation, actual.Preservation)))
            throw new IOException("Link object, raw target or supported metadata changed.");
    }
    private static bool SamePreservation(LinkPreservationObservation? a, LinkPreservationObservation? b) =>
        a is not null && b is not null && a.UnixMode == b.UnixMode && a.UserId == b.UserId && a.GroupId == b.GroupId
        && a.LinkCount == b.LinkCount && a.VisibleAttributesFingerprint == b.VisibleAttributesFingerprint && a.Mount == b.Mount;
    private ExposureLinkEvidence ReadLink(NativeExposureDirectory directory, string entry, CancellationToken token) =>
        directory.ReadLink(entry, linkPin, linuxAuthority, token);
    private void ValidateLinuxAuthorities(NativeExposureDirectory? other, CancellationToken token)
    {
        if (linuxAuthority is null) return;
        linuxAuthority.Check(token); target.Revalidate(token);
        var expected = Original.Preservation!.Mount;
        foreach (var observation in source.CaptureLinuxLinkAuthorities(linuxAuthority, token)
            .Concat(target.CaptureLinuxLinkAuthorities(linuxAuthority, token))
            .Concat(other?.CaptureLinuxLinkAuthorities(linuxAuthority, token) ?? []))
            LinuxLinkProcAuthority.RequireSameMount(expected, observation);
        LinuxLinkProcAuthority.RequireSameMount(expected, linkPin.ObserveLinux(linuxAuthority, token).Mount);
        linuxAuthority.Check(token);
    }
    public void Dispose() { linkPin.Dispose(); source.Dispose(); linuxAuthority?.Dispose(); }
}

/// Fixed leaf storage beneath an owned private state child. Generic state
/// enumeration/file APIs continue rejecting links; this surface never follows it.
internal sealed class NativeRetainedLinkSlot : IDisposable
{
    internal const string EntryName = "entry";
    private readonly NativeStateDirectory state;
    internal NativeExposureDirectory Directory { get; }
    internal string CanonicalPath => state.CanonicalPath;
    private NativeRetainedLinkSlot(NativeStateDirectory state, NativeExposureDirectory directory)
    { this.state = state; Directory = directory; }
    internal static NativeRetainedLinkSlot Create(NativeStateDirectory operation, string name, CancellationToken token)
    {
        var state = operation.CreateStagingDirectory(name, token);
        try { return new(state, OperatingSystem.IsWindows() ? WindowsSecureRemovalBackend.BindExposureSlot(state, token) : UnixSecureRemovalBackend.BindExposureSlot(state, token)); }
        catch { state.Dispose(); throw; }
    }
    public void Dispose() { Directory.Dispose(); state.Dispose(); }
}

internal abstract class NativeExposureDirectory : IDisposable
{
    internal abstract ExposureDirectoryEvidence Capture(CancellationToken token);
    internal abstract ExposureLinkEvidence ReadLink(string name, CancellationToken token);
    internal abstract NativeExposureLinkPin PinLink(string name, ExposureLinkEvidence expected, CancellationToken token);
    internal abstract SecureFileIdentity ResolveLink(string name, CancellationToken token);
    internal abstract SecureFileIdentity ResolveRawTarget(ReadOnlySpan<byte> raw, CancellationToken token);
    internal abstract void MoveExclusive(string name, ExposureLinkEvidence expected, ExposureDirectoryEvidence sourceBefore,
        NativeExposureDirectory destination, string destinationName, ExposureDirectoryEvidence destinationBefore, Action<CancellationToken>? beforeNativeMove, CancellationToken token);
    internal virtual IDisposable HoldLinuxLinkAuthorities() => throw new IOException("Linux held link authorities unavailable.");
    internal virtual ImmutableArray<LinuxLinkMountObservation> CaptureLinuxLinkAuthorities(LinuxLinkProcAuthority authority, CancellationToken token) =>
        throw new IOException("Linux preservation authorities unavailable.");
    internal virtual (LinuxLinkPinObservation Pin, LinkPreservationObservation Observation) CaptureLinuxPreservation(
        string name, NativeExposureLinkPin pin, LinuxLinkProcAuthority authority, CancellationToken token) => throw new IOException("Linux preservation unavailable.");
    internal ExposureLinkEvidence ReadLink(string name, NativeExposureLinkPin pin, LinuxLinkProcAuthority? authority, CancellationToken token)
    {
        using var references = authority is null ? null : HoldLinuxLinkAuthorities();
        authority?.Check(token);
        var link = ReadLink(name, token);
        if (authority is null) return link;
        var observed = CaptureLinuxPreservation(name, pin, authority, token);
        if (observed.Pin.Identity with { CanonicalPath = link.Identity.CanonicalPath } != link.Identity
            || !observed.Pin.RawTarget.AsSpan().SequenceEqual(link.RawTarget.AsSpan())) throw new IOException("Named link contradicts original pin.");
        return link with { Preservation = observed.Observation };
    }
    internal abstract void Flush(CancellationToken token);
    public abstract void Dispose();
    internal void Validate(ExposureDirectoryEvidence expected, CancellationToken token)
    {
        var actual = Capture(token);
        if (expected.Root != actual.Root || expected.Parent != actual.Parent || !expected.Entries.SequenceEqual(actual.Entries))
            throw new IOException("Held exposure directory identity/generation/namespace changed.");
    }
    internal static NativeExposureDirectory Open(string root, string entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported) throw new IOException("Native link authority unavailable: " + SecureRemovalBackend.UnsupportedReason);
        return OperatingSystem.IsWindows() ? WindowsSecureRemovalBackend.OpenExposureDirectory(root, entry, token) : UnixSecureRemovalBackend.OpenExposureDirectory(root, entry, token);
    }
    internal static void RequireEntry(ExposureDirectoryEvidence directory, string name, string expectedHash)
    {
        if (!directory.Entries.TryGetValue(name, out var actual) || actual != expectedHash)
            throw new IOException("The observed moved entry contradicts its captured namespace identity/raw target.");
    }
    internal static void VerifyTransition(ExposureDirectoryEvidence before, ExposureDirectoryEvidence after, string changedName, bool added, string expectedHash)
    {
        RequireEntry(added ? after : before, changedName, expectedHash);
        if (!NativeDirectoryMoveLease.SameObject(before.Parent, after.Parent)
            || before.Parent.CanonicalPath != after.Parent.CanonicalPath
            || (before.Root.CanonicalPath == before.Parent.CanonicalPath
                ? !NativeDirectoryMoveLease.SameObject(before.Root, after.Root) : before.Root != after.Root)
            || before.Root.CanonicalPath != after.Root.CanonicalPath
            || added && (before.Entries.ContainsKey(changedName) || !after.Entries.ContainsKey(changedName))
            || !added && (!before.Entries.ContainsKey(changedName) || after.Entries.ContainsKey(changedName))
            || !before.Entries.Remove(changedName).SequenceEqual(after.Entries.Remove(changedName)))
            throw new IOException("Native rename did not produce exactly its expected namespace transition; retain evidence.");
    }
    internal static string MetadataHash(PayloadNativeMetadata metadata) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(metadata with { Identity = default }, ExposureLinkJsonContext.Default.PayloadNativeMetadata)));
    internal static string EntryHash(SecureFileIdentity identity, byte[]? raw = null) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new ExposureNamespaceEntry(identity, raw), ExposureLinkJsonContext.Default.ExposureNamespaceEntry)));
    internal static string EntryHash(ExposureLinkEvidence link) => EntryHash(link.Identity with { CanonicalPath = "" }, link.RawTarget.ToArray());
}

internal sealed record ExposureNamespaceEntry(SecureFileIdentity Identity, byte[]? Raw);
[JsonSerializable(typeof(PayloadNativeMetadata))]
[JsonSerializable(typeof(ExposureNamespaceEntry))]
internal partial class ExposureLinkJsonContext : JsonSerializerContext;
