using System.Collections.Immutable;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class NativeExposureLinkTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-exposure-link-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TargetBackedRoundTripRetainsExactNativeLinkOrExplicitlyRefusesUnsupportedMetadata(bool relative, bool ancestorAlias)
    {
        using var fixture = Create(relative);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        var before = target.Capture(Token);
        var exposureRoot = fixture.ExposureRoot;
        if (ancestorAlias)
        {
            exposureRoot += "-alias";
            Directory.CreateSymbolicLink(exposureRoot, fixture.ExposureRoot);
        }
        using var lease = NativeExposureLinkLease.Open(exposureRoot, Path.Combine(exposureRoot, "demo"), target, Token);
        RequirePositiveProofMetadata(lease);
        Assert.Equal(target.TargetIdentity, lease.TargetIdentity);
        Assert.NotEmpty(lease.Original.RawTarget);
        Assert.True(lease.Original.Identity.IsReparsePoint);
        lease.Revalidate(Token);
        if (!lease.Original.Metadata.Supported)
        {
            var failure = Assert.Throws<IOException>(() => lease.Retain(fixture.Slot, Token));
            Assert.Contains("Link metadata cannot be fully verified", failure.Message);
            Assert.NotEmpty(lease.Original.Metadata.Refusals);
            if (OperatingSystem.IsLinux()) Assert.Contains(lease.Original.Metadata.Refusals, value => value.Contains("symlink inode flags", StringComparison.Ordinal));
            Assert.False(lease.MutationAttempted);
            Assert.True(PathResolver.IsSymlink(fixture.Link));
            Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
        }
        else
        {
            Assert.False(OperatingSystem.IsLinux());
            var retained = lease.Retain(fixture.Slot, Token);
            fixture.State.FlushPublication(Token);
            Assert.True(lease.MutationAttempted); Assert.True(lease.MutationCompleted);
            Assert.True(NativeDirectoryMoveLease.SameObject(lease.Original.Identity, retained.Identity));
            Assert.Equal(lease.Original.MetadataFingerprint, retained.MetadataFingerprint);
            Assert.False(PathResolver.IsSymlink(fixture.Link));
            Assert.True(PathResolver.IsSymlink(Path.Combine(fixture.Slot.CanonicalPath, "entry")));
            lease.Revalidate(Token);
            // Existing generic state policy still rejects the retained link.
            using (var generic = NativeStateDirectory.OpenOrCreate(fixture.Slot.CanonicalPath, [fixture.TargetRoot, fixture.ExposureRoot], Token, allowCreate: false))
                Assert.NotNull(Assert.Single(generic.EnumerateEntries(Token)).Error);
            var restored = lease.Restore(Token);
            fixture.State.FlushPublication(Token);
            Assert.True(NativeDirectoryMoveLease.SameObject(lease.Original.Identity, restored.Identity));
            Assert.True(lease.Original.RawTarget.AsSpan().SequenceEqual(restored.RawTarget.AsSpan()));
            Assert.Equal(lease.Original.MetadataFingerprint, restored.MetadataFingerprint);
            Assert.True(PathResolver.IsSymlink(fixture.Link));
            Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
            lease.Revalidate(Token);
        }
        Assert.Equal(before.Fingerprint, target.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, target.Capture(Token).ObjectStateFingerprint);
        Assert.Equal(fixture.SkillBytes, File.ReadAllBytes(Path.Combine(fixture.Link, "SKILL.md")));
    }

    [Fact]
    public void ALinkToAnotherDirectoryDoesNotAcquireTheHeldTargetsAuthority()
    {
        using var fixture = Create(true);
        var other = Path.Combine(fixture.TargetRoot, "other"); WriteSkill(other);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, other, Token);
        Assert.Throws<IOException>(() => NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token));
        Assert.True(PathResolver.IsSymlink(fixture.Link)); Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
    }

    [Fact]
    public void LinkToLinkFinalTargetIsRefusedEvenWhenBothCurrentlyReachTheHeldPayload()
    {
        using var fixture = Create(false);
        var indirect = Path.Combine(fixture.ExposureRoot, "indirect");
        Directory.CreateSymbolicLink(indirect, fixture.Payload);
        Directory.Delete(fixture.Link); Directory.CreateSymbolicLink(fixture.Link, "indirect");
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        Assert.Throws<IOException>(() => NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token));
        Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
        Assert.Equal(fixture.SkillBytes, File.ReadAllBytes(Path.Combine(fixture.Payload, "SKILL.md")));
    }

    [Fact]
    public void OrdinaryDirectoryExposureDoesNotBecomeARetainableLink()
    {
        using var fixture = Create(false); Directory.Delete(fixture.Link); WriteSkill(fixture.Link);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        Assert.Throws<IOException>(() => NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token));
        Assert.True(Directory.Exists(fixture.Link)); Assert.False(PathResolver.IsSymlink(fixture.Link));
        Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
    }

    [Fact]
    public void ReplacingLinkWithIdenticalRawTargetRequiresFreshNativeReview()
    {
        using var fixture = Create(true);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        Directory.Delete(fixture.Link); Directory.CreateSymbolicLink(fixture.Link, Path.GetRelativePath(fixture.ExposureRoot, fixture.Payload));
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
        Assert.False(lease.MutationAttempted); Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
        Assert.Equal(fixture.SkillBytes, File.ReadAllBytes(Path.Combine(fixture.Payload, "SKILL.md")));
    }

    [Fact]
    public void RenamedExposureParentCannotRedirectValidationToAReplacement()
    {
        using var fixture = Create(false);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        Directory.Move(fixture.ExposureRoot, fixture.ExposureRoot + "-moved");
        Directory.CreateDirectory(fixture.ExposureRoot); File.WriteAllText(fixture.Link, "foreign");
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
        Assert.Equal("foreign", File.ReadAllText(fixture.Link)); Assert.False(lease.MutationAttempted);
    }

    [Fact]
    public void PreCanceledRetainDoesNotMoveOrAllocateAnything()
    {
        using var fixture = Create(true);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var failure = Assert.Throws<OperationCanceledException>(() => lease.Retain(fixture.Slot, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.False(lease.MutationAttempted);
        lease.Revalidate(Token); Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
    }

    [Fact]
    public void RestoreCollisionRetainsTheOriginalLinkAndNeverOverwritesForeignEntry()
    {
        using var fixture = Create(true);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        var before = target.Capture(Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        RequirePositiveProofMetadata(lease);
        if (!lease.Original.Metadata.Supported)
        {
            Assert.Throws<IOException>(() => lease.Retain(fixture.Slot, Token));
            Assert.False(lease.MutationAttempted); return; // No post-retain proof on this host.
        }
        var retained = lease.Retain(fixture.Slot, Token);
        fixture.State.FlushPublication(Token);
        File.WriteAllText(fixture.Link, "foreign occupant");
        Assert.Throws<IOException>(() => lease.Restore(Token));
        Assert.Equal("foreign occupant", File.ReadAllText(fixture.Link));
        var actual = fixture.Slot.Directory.ReadLink("entry", Token);
        NativeExposureLinkLease.SameLink(retained, actual, true);
        Assert.Equal(before.Fingerprint, target.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, target.Capture(Token).ObjectStateFingerprint);
    }

    [Fact]
    public void ReplacingTargetWithSameAuthoredBytesCannotPreserveTargetAuthority()
    {
        using var fixture = Create(true);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        Directory.Move(fixture.Payload, fixture.Payload + "-original"); WriteSkill(fixture.Payload);
        Assert.Throws<IOException>(() => lease.Revalidate(Token));
        Assert.False(lease.MutationAttempted); Assert.Empty(fixture.Slot.Directory.Capture(Token).Entries);
        Assert.Equal(fixture.SkillBytes, File.ReadAllBytes(Path.Combine(fixture.Payload, "SKILL.md")));
        Assert.Equal(fixture.SkillBytes, File.ReadAllBytes(Path.Combine(fixture.Payload + "-original", "SKILL.md")));
    }

    [Fact]
    public void AForeignPrivateSlotEntryIsNeverOverwrittenOrFollowed()
    {
        using var fixture = Create(true);
        File.WriteAllText(Path.Combine(fixture.Slot.CanonicalPath, "entry"), "foreign");
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        RequirePositiveProofMetadata(lease);
        Assert.Throws<IOException>(() => lease.Retain(fixture.Slot, Token));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(fixture.Slot.CanonicalPath, "entry")));
        Assert.True(PathResolver.IsSymlink(fixture.Link)); Assert.False(lease.MutationAttempted);
    }

    [Fact]
    public void SharedTargetAndExposureParentRefusesRatherThanRefreshingTargetGeneration()
    {
        var common = Directory.CreateDirectory(Path.Combine(temporary.FullName, "shared-parent")).FullName;
        var payload = Path.Combine(common, "payload"); WriteSkill(payload);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "private-shared"), [common], Token);
        using var writer = state.OpenFile("writer.lock", true, Token);
        using var slot = NativeRetainedLinkSlot.Create(state, "retained-link", Token);
        var link = Path.Combine(common, "exposure"); Directory.CreateSymbolicLink(link, "payload");
        using var target = NativeDirectoryMoveLease.Open(common, payload, Token);
        var before = target.Capture(Token);
        using var lease = NativeExposureLinkLease.Open(common, link, target, Token);
        RequirePositiveProofMetadata(lease);
        var failure = Assert.Throws<IOException>(() => lease.Retain(slot, Token));
        Assert.Contains(lease.Original.Metadata.Supported ? "coordinated generation transition" : "Link metadata cannot be fully verified", failure.Message);
        Assert.False(lease.MutationAttempted); lease.Revalidate(Token);
        Assert.Empty(slot.Directory.Capture(Token).Entries);
        Assert.Equal(before.Fingerprint, target.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, target.Capture(Token).ObjectStateFingerprint);
    }

    [Fact]
    public void NamespaceReconciliationDoesNotRefreshAnUnrelatedSiblingChange()
    {
        var parent = new SecureFileIdentity(1, 2, 0, "/root", true, false, 1, 1);
        var before = new ExposureDirectoryEvidence(parent, parent, ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            new Dictionary<string, string> { ["entry"] = "old-link", ["sibling"] = "before" }));
        var after = before with { Root = parent with { ChangeTimeNanoseconds = 2 }, Parent = parent with { ChangeTimeNanoseconds = 2 }, Entries = before.Entries.Remove("entry").SetItem("sibling", "foreign") };
        Assert.Throws<IOException>(() => NativeExposureDirectory.VerifyTransition(before, after, "entry", false, "old-link"));
    }

    [Fact]
    public void MovedEntryObservationCannotBeCombinedWithAReplacementNamespaceEntry()
    {
        using var fixture = Create(true);
        using var target = NativeDirectoryMoveLease.Open(fixture.TargetRoot, fixture.Payload, Token);
        using var lease = NativeExposureLinkLease.Open(fixture.ExposureRoot, fixture.Link, target, Token);
        var observedA = lease.Original;
        var before = fixture.Slot.Directory.Capture(Token);
        var replacementB = observedA with { Identity = observedA.Identity with { FileIdLow = observedA.Identity.FileIdLow + 1 } };
        var after = before with
        {
            Root = before.Root with { ChangeTimeNanoseconds = before.Root.ChangeTimeNanoseconds + 1 },
            Parent = before.Parent with { ChangeTimeNanoseconds = before.Parent.ChangeTimeNanoseconds + 1 },
            Entries = before.Entries.Add("entry", NativeExposureDirectory.EntryHash(replacementB))
        };
        // All untouched entries and the expected one-name addition are valid;
        // only binding the moved name to actual A rejects this contradiction.
        Assert.Throws<IOException>(() => NativeExposureDirectory.VerifyTransition(before, after, "entry", true,
            NativeExposureDirectory.EntryHash(observedA)));
        Assert.False(lease.MutationAttempted); lease.Revalidate(Token);
    }

    private Fixture Create(bool relative)
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, Guid.NewGuid().ToString("N"))).FullName;
        var targets = Directory.CreateDirectory(Path.Combine(root, "targets")).FullName;
        var exposures = Directory.CreateDirectory(Path.Combine(root, "exposures")).FullName;
        var payload = Path.Combine(targets, "payload"); WriteSkill(payload);
        // All private namespaces precede the target and exposure parent freeze.
        var state = NativeStateDirectory.OpenOrCreate(Path.Combine(root, "private"), [targets, exposures], Token);
        FileStream? writer = null;
        NativeRetainedLinkSlot? slot = null;
        try
        {
            writer = state.OpenFile("writer.lock", true, Token);
            slot = NativeRetainedLinkSlot.Create(state, "retained-link", Token);
            var link = Path.Combine(exposures, "demo");
            Directory.CreateSymbolicLink(link, relative ? Path.GetRelativePath(exposures, payload) : payload);
            return new(state, writer, slot, targets, payload, exposures, link, File.ReadAllBytes(Path.Combine(payload, "SKILL.md")));
        }
        catch { slot?.Dispose(); writer?.Dispose(); state.Dispose(); throw; }
    }
    private static void WriteSkill(string path)
    { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\ndescription: native link fixture\nmetadata:\n  github-pinned: frozen\n---\nunchanged\n"); }
    private static void RequirePositiveProofMetadata(NativeExposureLinkLease lease)
    {
        if (!OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("SKILLVIEW_EXPOSURE_LINK_PROOF") == "1")
            Assert.True(lease.Original.Metadata.Supported, "Native proof requires complete supported link metadata: " + string.Join(" ", lease.Original.Metadata.Refusals));
    }
    private sealed record Fixture(NativeStateDirectory State, FileStream Writer, NativeRetainedLinkSlot Slot, string TargetRoot, string Payload,
        string ExposureRoot, string Link, byte[] SkillBytes) : IDisposable
    { public void Dispose() { Slot.Dispose(); Writer.Dispose(); State.Dispose(); } }
}
