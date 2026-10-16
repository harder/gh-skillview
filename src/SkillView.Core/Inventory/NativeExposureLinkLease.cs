using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed record ExposureLinkEvidence(SecureFileIdentity Identity, ImmutableArray<byte> RawTarget,
    PayloadNativeMetadata Metadata, string MetadataFingerprint);

internal sealed record ExposureDirectoryEvidence(SecureFileIdentity Root, SecureFileIdentity Parent,
    ImmutableSortedDictionary<string, string> Entries);

/// A primitive only: callers must supply placement/owner policy and a durable
/// intent before calling either move. There is no unlink/recreate fallback.
internal sealed class NativeExposureLinkLease : IDisposable
{
    private readonly NativeExposureDirectory source;
    private readonly NativeDirectoryMoveLease target;
    private readonly string name;
    private readonly IDisposable linkPin;
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

    private NativeExposureLinkLease(NativeExposureDirectory source, string name, NativeDirectoryMoveLease target,
        ExposureDirectoryEvidence directory, ExposureLinkEvidence link, IDisposable linkPin)
    {
        this.source = source; this.name = name; this.target = target;
        this.linkPin = linkPin;
        sourceEvidence = OriginalDirectory = directory; Original = current = link;
        TargetIdentity = target.TargetIdentity;
    }

    internal static NativeExposureLinkLease Open(string root, string entry, NativeDirectoryMoveLease target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); target.Revalidate(token);
        var authority = NativeExposureDirectory.Open(root, entry, token);
        IDisposable? pin = null;
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
            var directory = authority.Capture(token);
            var link = authority.ReadLink(name, token);
            pin = authority.PinLink(name, link, token);
            RequireTarget(authority.ResolveLink(name, token), target.TargetIdentity);
            RequireTarget(authority.ResolveRawTarget(link.RawTarget.AsSpan(), token), target.TargetIdentity);
            target.Revalidate(token); SameDirectory(directory, authority.Capture(token));
            SameLink(link, authority.ReadLink(name, token), sameGeneration: true);
            NativeExposureDirectory.RequireEntry(directory, name, NativeExposureDirectory.EntryHash(link));
            if (PathIdentity.IsInside(link.Identity.CanonicalPath, target.CurrentPath))
                throw new IOException("An exposure inside its target payload cannot be retained.");
            var result = new NativeExposureLinkLease(authority, name, target, directory, link, pin);
            pin = null; return result;
        }
        catch { pin?.Dispose(); authority.Dispose(); throw; }
    }

    internal ExposureLinkEvidence Retain(NativeRetainedLinkSlot slot, CancellationToken token)
    {
        RequireAvailable(token);
        if (retained is not null || restored) throw new IOException("This live link was already retained.");
        RequireMetadata(current);
        source.Validate(sourceEvidence, token); target.Revalidate(token);
        SameLink(current, source.ReadLink(name, token), true);
        RequireTarget(source.ResolveLink(name, token), TargetIdentity);
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
        RequireAvailable(token); target.Revalidate(token); source.Validate(sourceEvidence, token);
        if (retained is null || restored)
        {
            SameLink(current, source.ReadLink(name, token), true);
            RequireTarget(source.ResolveLink(name, token), TargetIdentity);
        }
        else
        {
            retained.Directory.Validate(retainedEvidence!, token);
            SameLink(current, retained.Directory.ReadLink(NativeRetainedLinkSlot.EntryName, token), true);
            RequireTarget(source.ResolveRawTarget(current.RawTarget.AsSpan(), token), TargetIdentity);
        }
    }

    internal ExposureLinkEvidence Restore(CancellationToken token)
    {
        RequireAvailable(token);
        if (retained is null || retainedEvidence is null || restored) throw new IOException("There is no retained link to restore.");
        RequireMetadata(current); target.Revalidate(token);
        retained.Directory.Validate(retainedEvidence, token); source.Validate(sourceEvidence, token);
        if (sourceEvidence.Entries.ContainsKey(name)) throw new IOException("The original exposure slot is occupied.");
        SameLink(current, retained.Directory.ReadLink(NativeRetainedLinkSlot.EntryName, token), true);
        // Relative targets are interpreted at the ORIGINAL held parent.
        RequireTarget(source.ResolveRawTarget(current.RawTarget.AsSpan(), token), TargetIdentity);
        return Move(retained.Directory, NativeRetainedLinkSlot.EntryName, retainedEvidence,
            source, name, sourceEvidence, retained, true, token);
    }

    private ExposureLinkEvidence Move(NativeExposureDirectory from, string fromName, ExposureDirectoryEvidence fromBefore,
        NativeExposureDirectory to, string toName, ExposureDirectoryEvidence toBefore, NativeRetainedLinkSlot slot, bool undo, CancellationToken token)
    {
        from.Validate(fromBefore, token); to.Validate(toBefore, token); target.Revalidate(token);
        NativeExposureDirectory.RequireEntry(fromBefore, fromName, NativeExposureDirectory.EntryHash(current));
        if (toBefore.Entries.ContainsKey(toName)) throw new IOException("The destination entry is occupied.");
        token.ThrowIfCancellationRequested();
        uncertain = true; MutationAttempted = true; MutationCompleted = false;
        from.MoveExclusive(fromName, current, fromBefore, to, toName, toBefore, token);
        // Cancellation after a native move must not hide its observation. The
        // caller's durable journal owns reconciliation if any check fails.
        var afterLink = to.ReadLink(toName, CancellationToken.None);
        SameLink(current, afterLink, false); RequireMetadata(afterLink);
        var fromAfter = from.Capture(CancellationToken.None);
        var toAfter = to.Capture(CancellationToken.None);
        NativeExposureDirectory.VerifyTransition(fromBefore, fromAfter, fromName, added: false, NativeExposureDirectory.EntryHash(current));
        NativeExposureDirectory.VerifyTransition(toBefore, toAfter, toName, added: true, NativeExposureDirectory.EntryHash(afterLink));
        target.Revalidate(CancellationToken.None);
        RequireTarget(source.ResolveRawTarget(afterLink.RawTarget.AsSpan(), CancellationToken.None), TargetIdentity);
        if (undo) RequireTarget(source.ResolveLink(name, CancellationToken.None), TargetIdentity);
        from.Flush(CancellationToken.None); to.Flush(CancellationToken.None);
        from.Validate(fromAfter, CancellationToken.None); to.Validate(toAfter, CancellationToken.None);
        SameLink(afterLink, to.ReadLink(toName, CancellationToken.None), true);
        to.Validate(toAfter, CancellationToken.None);
        if (undo) { sourceEvidence = toAfter; retainedEvidence = fromAfter; restored = true; }
        else { sourceEvidence = fromAfter; retainedEvidence = toAfter; retained = slot; }
        current = afterLink; uncertain = false; MutationCompleted = true;
        token.ThrowIfCancellationRequested();
        return current;
    }

    private void RequireAvailable(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (uncertain) throw new IOException("A prior native link move needs external journal reconciliation; no retry is admitted."); }
    private static void RequireMetadata(ExposureLinkEvidence evidence)
    { if (!evidence.Metadata.Supported) throw new IOException("Link metadata cannot be fully verified: " + string.Join(" ", evidence.Metadata.Refusals)); }
    private static void RequireTarget(SecureFileIdentity actual, SecureFileIdentity expected)
    { if (actual != expected) throw new IOException("The exposure does not resolve to the exact held target identity/generation/address."); }
    private static void SameDirectory(ExposureDirectoryEvidence expected, ExposureDirectoryEvidence actual)
    { if (expected.Root != actual.Root || expected.Parent != actual.Parent || !expected.Entries.SequenceEqual(actual.Entries)) throw new IOException("Exposure namespace changed."); }
    internal static void SameLink(ExposureLinkEvidence expected, ExposureLinkEvidence actual, bool sameGeneration)
    {
        if (!(sameGeneration ? expected.Identity == actual.Identity : NativeDirectoryMoveLease.SameObject(expected.Identity, actual.Identity))
            || !expected.RawTarget.AsSpan().SequenceEqual(actual.RawTarget.AsSpan()) || expected.MetadataFingerprint != actual.MetadataFingerprint)
            throw new IOException("Link object, raw target or supported metadata changed.");
    }
    public void Dispose() { linkPin.Dispose(); source.Dispose(); }
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
    internal abstract IDisposable PinLink(string name, ExposureLinkEvidence expected, CancellationToken token);
    internal abstract SecureFileIdentity ResolveLink(string name, CancellationToken token);
    internal abstract SecureFileIdentity ResolveRawTarget(ReadOnlySpan<byte> raw, CancellationToken token);
    internal abstract void MoveExclusive(string name, ExposureLinkEvidence expected, ExposureDirectoryEvidence sourceBefore,
        NativeExposureDirectory destination, string destinationName, ExposureDirectoryEvidence destinationBefore, CancellationToken token);
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
