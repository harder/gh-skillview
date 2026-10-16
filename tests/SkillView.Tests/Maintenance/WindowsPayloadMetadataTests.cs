using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class WindowsPayloadMetadataTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-windows-payload-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData(":$DATA")]
    [InlineData(":..:$DATA")]
    [InlineData(":file.:$DATA")]
    [InlineData(":file :$DATA")]
    [InlineData(":a:b:$DATA")]
    [InlineData(":a\\b:$DATA")]
    [InlineData(":a/b:$DATA")]
    [InlineData(":a:$INDEX_ALLOCATION")]
    [InlineData(":é:$DATA")]
    public void UnsupportedStreamNamesCannotReachNativePublication(string name)
    {
        var metadata = new WindowsPayloadMetadata(1, new byte[20], 32, [new(name, [])]);
        Assert.Throws<IOException>(() => metadata.Validate());
    }

    [Fact]
    public void AlternateStreamsRetainEmptyAndBinaryBytesAndRejectCaseAliasesAndOversize()
    {
        var metadata = new WindowsPayloadMetadata(1, new byte[20], 32,
            [new(":Zone.Identifier:$DATA", [0, 255]), new(":empty:$DATA", [])]);
        Assert.Equal(2, metadata.StreamBytes); Assert.True(metadata.Validate() > metadata.StreamBytes);
        Assert.True(WindowsPayloadMetadata.Same(metadata, metadata with { Streams = metadata.Streams.Reverse().ToImmutableArray() }));
        Assert.Throws<IOException>(() => (metadata with { Streams = metadata.Streams.Add(new(":ZONE.Identifier:$DATA", [])) }).Validate());
        Assert.Throws<IOException>(() => (metadata with { Streams = [new(":large:$DATA", new byte[PayloadNativeMetadata.MaxAttributeBytes + 1])] }).Validate());
        Assert.Throws<IOException>(() => (metadata with { SchemaVersion = 2 }).Validate());
    }

    [Fact]
    public void LegacyNativeEvidenceSerializationDoesNotAcquireWindowsFields()
    {
        var metadata = new PayloadNativeMetadata("macos", default, "file", 384, 1, 1, 1, false, [], "macos-native-owner", []);
        var json = JsonSerializer.Serialize(metadata, PayloadArchiveJsonContext.Default.PayloadNativeMetadata);
        Assert.DoesNotContain("\"windows\":", json);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, PayloadArchiveJsonContext.Default.PayloadNativeMetadata), PayloadArchiveJsonContext.Default.PayloadNativeMetadata));
        var upgraded = metadata with
        {
            Platform = "windows",
            UnixMode = null,
            UserId = null,
            GroupId = null,
            EnumerationCoverage = "windows-full-security-and-backup-streams-v1",
            Windows = new(1, new byte[20], 32, [])
        };
        Assert.Contains("\"windows\": {", JsonSerializer.Serialize(upgraded, PayloadArchiveJsonContext.Default.PayloadNativeMetadata));
        Assert.False((upgraded with { EnumerationCoverage = "unavailable" }).Supported);
        Assert.False((upgraded with { Windows = null }).Supported);
    }

    [Fact]
    public void InlineAlternateBytesShareTheRegularResolutionRawBudget()
    {
        var identity = default(SecureFileIdentity) with { CanonicalPath = Path.GetTempPath() };
        var native = new PayloadNativeMetadata("windows", identity, "file", null, null, null, 1, false, [],
            "windows-full-security-and-backup-streams-v1", [], new(1, new byte[20], 32, [new(":Zone.Identifier:$DATA", [1])]));
        var entry = new PayloadEntry("resource", "file", new('a', 64), new('b', 64), PayloadSnapshot.MaxBytes, null, null);
        var archive = new CapturedResolutionArchive([new(entry, "entry-00000", entry.Bytes, native)]);
        Assert.Throws<IOException>(archive.Validate);
        (archive with { Entries = [new(entry with { Bytes = entry.Bytes - 1 }, "entry-00000", entry.Bytes - 1, native)] }).Validate();
    }

    [Theory]
    [InlineData(0xA0000003u)] // junction/mount point
    [InlineData(0x8000001Bu)] // application execution link
    [InlineData(0u)]
    public void OpaqueAndMountReparseTagsCannotEnterTheSymbolicLinkPublicationAdapter(uint tag)
    {
        var bytes = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 12);
        Assert.False(WindowsSecureRemovalBackend.IsSupportedMetadataReparse(bytes));
        Assert.False(WindowsSecureRemovalBackend.IsSupportedMetadataReparse(bytes.AsSpan(0, 8)));
    }

    [Fact]
    public void WindowsNativeMergeCopiesExactDescriptorAndRootFileStreamsOrRefusesWithoutMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var initialImpersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Assert.Null(initialImpersonation);
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "skills"));
        var target = Directory.CreateDirectory(Path.Combine(root.FullName, "demo"));
        File.WriteAllText(Path.Combine(target.FullName, "SKILL.md"), "---\nname: demo\ndescription: Windows metadata fixture\n---\nInstructions\n");
        File.WriteAllBytes(Path.Combine(target.FullName, "resource"), [0, 255, 9]);
        File.WriteAllBytes(Path.Combine(target.FullName, "resource") + ":Zone.Identifier", [7, 0, 255]);
        File.WriteAllBytes(Path.Combine(target.FullName, "resource") + ":empty", []);
        File.WriteAllBytes(target.FullName + ":rootInfo", [8, 9]);
        // Elevated runners can default new objects to Administrators ownership.
        // Prove the explicitly supported current-user-owned case on every lane.
        SetFixtureOwner(target.FullName);
        SetFixtureOwner(Path.Combine(target.FullName, "SKILL.md"));
        SetFixtureOwner(Path.Combine(target.FullName, "resource"));
        var auditInstalled = TryInstallAudit(Path.Combine(target.FullName, "resource"));
        var before = PayloadSnapshot.Capture(target.FullName, Token);
        using var source = NativeDirectoryMoveLease.Open(root.FullName, target.FullName, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "state"), [target.FullName], Token);
        var captured = CapturedPayloadArchive.Capture(state, "capture", source, Token);
        if (!captured.RootMetadata!.Supported || captured.Entries.Any(entry => !entry.Metadata!.Supported))
        {
            Assert.NotEmpty(captured.RootMetadata.Refusals.Concat(captured.Entries.SelectMany(entry => entry.Metadata!.Refusals)));
            Assert.Throws<IOException>(() => Build());
            Assert.False(Directory.Exists(Path.Combine(state.CanonicalPath, "merged")));
            Assert.Equal(before.ObjectStateFingerprint, source.Capture(Token).ObjectStateFingerprint);
            Assert.NotEqual("1", System.Environment.GetEnvironmentVariable("SKILLVIEW_WINDOWS_METADATA_PROOF"));
            return;
        }
        var resource = captured.Entries.Single(entry => entry.Entry.Path == "resource").Metadata!.Windows!;
        Assert.True(auditInstalled);
        Assert.Equal(1, new RawSecurityDescriptor(resource.SecurityDescriptor, 0).SystemAcl!.Count);
        Assert.Equal(new byte[] { 7, 0, 255 }, resource.Streams.Single(stream => stream.Name == ":Zone.Identifier:$DATA").Bytes);
        Assert.Empty(resource.Streams.Single(stream => stream.Name == ":empty:$DATA").Bytes);
        using var beforePublicationImpersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Assert.Null(beforePublicationImpersonation);
        var built = Build();
        using var candidate = NativeDirectoryMoveLease.Open(state.CanonicalPath, built.Snapshot.Path, Token);
        var actual = CapturedPayloadArchive.Capture(state, "verified", candidate, Token);
        Assert.True(WindowsPayloadMetadata.Same(captured.RootMetadata.Windows, actual.RootMetadata!.Windows));
        foreach (var expected in captured.Entries)
            Assert.True(WindowsPayloadMetadata.Same(expected.Metadata!.Windows, actual.Entries.Single(entry => entry.Entry.Path == expected.Entry.Path).Metadata!.Windows));
        Assert.Equal(before.ObjectStateFingerprint, source.Capture(Token).ObjectStateFingerprint);
        Assert.Equal(new byte[] { 7, 0, 255 }, File.ReadAllBytes(Path.Combine(target.FullName, "resource") + ":Zone.Identifier"));

        VerifiedMergedPayload Build()
        {
            using var files = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "capture"), [target.FullName], Token, allowCreate: false);
            return ReviewedPayloadMaterializer.StagePreservingMetadata(state, "merged",
                ThreeWayPayloadReview.Analyze(captured.Snapshot, captured.Snapshot, captured.Snapshot),
                new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = captured.OpenReader(files) }, captured.Snapshot.FrontMatter!, Token);
        }
    }

    [Fact]
    public void WindowsResolutionObservesOriginalFileThroughItsInitialHandleAndFreezesAlternateStreams()
    {
        if (!OperatingSystem.IsWindows()) return;
        var input = Path.Combine(temporary.FullName, "input"); File.WriteAllBytes(input, [1, 2, 3]);
        File.WriteAllBytes(input + ":Zone.Identifier", [9, 0, 8]);
        SetFixtureOwner(input);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "state"), [], Token);
        var hash = CapturedResolutionArchive.Capture(state, "resolution", [new("resource", input)],
            PayloadSnapshot.MaxBytes, CapturedResolutionArchive.MaxRetainedManifestBytes, Token);
        using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "resolution"), [], Token, allowCreate: false);
        var saved = Assert.Single(CapturedResolutionArchive.Load(archive, hash, Token).Entries);
        if (!saved.Metadata!.Supported)
        {
            Assert.NotEmpty(saved.Metadata.Refusals);
            Assert.NotEqual("1", System.Environment.GetEnvironmentVariable("SKILLVIEW_WINDOWS_METADATA_PROOF")); return;
        }
        Assert.Equal(new byte[] { 9, 0, 8 }, saved.Metadata.Windows!.Streams.Single().Bytes);
        File.WriteAllBytes(input + ":Zone.Identifier", [1]); File.Delete(input);
        Assert.Equal(new byte[] { 9, 0, 8 }, saved.Metadata.Windows.Streams.Single().Bytes);
        using var bytes = new MemoryStream(); new CapturedResolutionArchive([saved]).OpenReader(archive).CopyEntry("resource", bytes, Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetFixtureOwner(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        using var handle = Native.CreateFileW(path, 0x00080000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Assert.Equal(0u, Native.SetSecurityInfo(handle, 1, 1, memory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryInstallAudit(string file)
    {
        WindowsMetadataPrivilege privilege;
        try { privilege = WindowsMetadataPrivilege.Enter(); }
        catch (IOException) { return false; }
        using (privilege)
        using (var identity = WindowsIdentity.GetCurrent())
        using (var handle = Native.CreateFileW(file, 0x01020000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
        {
            Assert.False(handle.IsInvalid);
            var acl = new RawAcl(GenericAcl.AclRevision, 1);
            acl.InsertAce(0, new CommonAce(AceFlags.SuccessfulAccess, AceQualifier.SystemAudit, 1, identity.User!, false, null));
            var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0);
            var memory = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                Assert.Equal(0u, Native.SetSecurityInfo(handle, 1, 8, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, memory));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return true;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("advapi32.dll")] internal static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    }
}
