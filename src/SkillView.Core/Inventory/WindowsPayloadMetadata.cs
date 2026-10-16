using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    private const uint MetadataReadAccess = 0x01020009; // ACCESS_SYSTEM_SECURITY | READ_CONTROL | READ_DATA/LIST | READ_EA
    private const uint MetadataWriteAccess = 0x010C0000; // ACCESS_SYSTEM_SECURITY | WRITE_DAC | WRITE_OWNER
    private const uint BackupSecurityInformation = 0x00010000;
    // Restrictive read-only, compression, encryption, sparse, offline, integrity,
    // cloud and unknown attributes need dedicated publication/recovery policy.
    private const uint SupportedMetadataAttributes = 0x000025B6; // HIDDEN,SYSTEM,DIRECTORY,ARCHIVE,NORMAL,TEMPORARY,REPARSE,NOT_CONTENT_INDEXED

    [SupportedOSPlatform("windows")]
    internal static PayloadNativeMetadata CapturePayloadMetadata(NativePayloadItem item, CancellationToken token,
        long maxAlternateBytes = PayloadNativeMetadata.MaxEntryMetadataBytes)
    {
        token.ThrowIfCancellationRequested();
        var refusals = ImmutableArray.CreateBuilder<string>();
        WindowsPayloadMetadata? windows = null; ulong? links = null;
        var retained = false;
        SafeFileHandle? opened = null;
        var borrowed = item.NativeHandle as SafeFileHandle;
        try
        {
            if (item.Kind == "link" && item.LinkBytes is { } raw && !IsSupportedMetadataReparse(raw))
                throw new IOException("Windows junction/mount/unknown reparse types require a dedicated metadata transition.");
            if (borrowed is null) throw new IOException("Windows metadata requires the captured native object handle.");
            borrowed.DangerousAddRef(ref retained);
            using var privilege = WindowsMetadataPrivilege.Enter();
            var handle = borrowed;
            if (item.NativeParentHandle is SafeFileHandle parent && item.NativeName is { } name)
                handle = opened = OpenEntryAt(parent, name, null, false, requestDeleteAccess: false, additionalAccess: MetadataReadAccess);
            if (!Matches(item.Identity, ReadIdentity(handle))) throw new IOException("Windows metadata source native object changed before inspection.");
            var descriptor = ReadMetadataSecurity(handle);
            var info = ReadMetadataInformation(handle);
            links = info.Links;
            if ((info.Attributes & ~SupportedMetadataAttributes) != 0)
                throw new IOException($"Unsupported restrictive/storage Windows attributes 0x{info.Attributes:X8} require a dedicated transition.");
            if (item.Kind == "link")
            {
                var reparse = ReadReparseData(handle);
                if (!IsSupportedMetadataReparse(reparse))
                    throw new IOException("Windows junction/mount/unknown reparse types require a dedicated metadata transition.");
                if (item.LinkBytes is not null && !item.LinkBytes.AsSpan().SequenceEqual(reparse))
                    throw new IOException("Windows reparse bytes differ from the captured link.");
            }
            var streams = ReadMetadataStreams(handle, item.Kind, token, maxAlternateBytes, PayloadNativeMetadata.MaxEntryMetadataBytes - descriptor.Length);
            windows = new(1, descriptor, info.Attributes, streams);
            windows.Validate();
            if (item.Kind != "directory" && links != 1) refusals.Add("Hard-linked Windows entries require a topology-preserving transition.");
            if (info.DeletePending || info.Directory != item.Identity.IsDirectory)
                refusals.Add("Windows metadata entry has an unsupported deletion/type state.");
            ValidateRecoverableSecurity(descriptor);
            if (!Matches(item.Identity, ReadIdentity(handle)) || !Matches(item.Identity, ReadIdentity(borrowed)))
                throw new IOException("Windows metadata source changed during inspection.");
            // Windows can retain ChangeTime across writes while a handle is
            // held. Independently repeat full descriptor and backup streams.
            if (!descriptor.AsSpan().SequenceEqual(ReadMetadataSecurity(handle))
                || info != ReadMetadataInformation(handle)
                || !WindowsPayloadMetadata.Same(windows, new(1, descriptor, info.Attributes, ReadMetadataStreams(handle, item.Kind, token, maxAlternateBytes, PayloadNativeMetadata.MaxEntryMetadataBytes - descriptor.Length))))
                throw new IOException("Windows security/stream evidence changed during capture.");
            if (!Matches(item.Identity, ReadIdentity(handle))) throw new IOException("Windows metadata generation changed during verification.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or ObjectDisposedException)
        { refusals.Add(ex.Message); }
        finally { opened?.Dispose(); if (retained) borrowed!.DangerousRelease(); }
        return new("windows", item.Identity, item.Kind, null, null, null, links,
            item.Kind == "link" && item.Identity.IsDirectory, [], windows is null ? "unavailable" : "windows-full-security-and-backup-streams-v1", refusals.ToImmutable(), windows);
    }

    [SupportedOSPlatform("windows")]
    internal static void ApplyPayloadMetadata(SafeFileHandle target, PayloadNativeMetadata metadata, int? mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); metadata.Validate();
        if (!metadata.Supported || metadata.Platform != "windows" || metadata.Windows is not { } expected || mode is not null)
            throw new IOException("Windows metadata publication requires complete reviewed native evidence.");
        if ((expected.FileAttributes & ~SupportedMetadataAttributes) != 0) throw new IOException("Windows attributes cannot be safely published.");
        var retained = false;
        try
        {
            target.DangerousAddRef(ref retained);
            using var privilege = WindowsMetadataPrivilege.Enter();
            // AccessCheck requires an impersonation token. The ordinary CLI
            // caller has a primary token until this synchronous thread scope.
            ValidateRecoverableSecurity(expected.SecurityDescriptor);
            RequireMetadataAccess(target, MetadataReadAccess | MetadataWriteAccess | 0x102);
            var before = ReadIdentity(target); var info = ReadMetadataInformation(target);
            if (metadata.Kind switch
            {
                "directory" => !before.IsDirectory || before.IsReparsePoint,
                "file" => before.IsDirectory || before.IsReparsePoint,
                "link" => !before.IsReparsePoint || before.IsDirectory != metadata.DirectoryLink,
                _ => true
            }
                || info.DeletePending || metadata.Kind != "directory" && info.Links != 1)
                throw new IOException("Windows staged metadata destination kind/link topology changed.");
            if (metadata.Kind == "link" && !IsSupportedMetadataReparse(ReadReparseData(target)))
                throw new IOException("Windows staged junction/mount/unknown reparse publication is unsupported.");
            // Creation supplies an explicit current-user private descriptor.
            // Never mutate an opened foreign-owner destination.
            var current = ReadMetadataSecurity(target);
            using var identity = WindowsIdentity.GetCurrent();
            if (new RawSecurityDescriptor(current, 0).Owner != identity.User)
                throw new IOException("Windows staged metadata destination is not owned by the effective user.");
            if (!ReadMetadataStreams(target, metadata.Kind, token).IsEmpty)
                throw new IOException("A new Windows metadata destination unexpectedly contains alternate streams.");
            WriteMetadataStreams(target, expected, token);
            var basic = new FileBasicInfoData { FileAttributes = expected.FileAttributes & ~(FileAttributeDirectory | FileAttributeReparsePoint) };
            if (basic.FileAttributes == 0) basic.FileAttributes = 0x80;
            token.ThrowIfCancellationRequested();
            if (!MetadataNative.SetFileInformationByHandle(target, FileBasicInfo, ref basic, Marshal.SizeOf<FileBasicInfoData>()))
                throw MetadataFailure("Publish held Windows attributes");
            var actual = CapturePayloadMetadata(new("", metadata.Kind, null, null, null, null,
                ToSecureIdentity(ReadIdentity(target), ReadFinalPath(target)), target), token);
            if (!actual.Supported || !WindowsPayloadMetadata.Same(expected, actual.Windows) || actual.DirectoryLink != metadata.DirectoryLink)
                throw new IOException("Published Windows native metadata differs from its exact reviewed descriptor/streams/attributes: " + string.Join(" ", actual.Refusals));
            var after = ReadIdentity(target);
            if (after.Volume != before.Volume || after.FileIdLow != before.FileIdLow || after.FileIdHigh != before.FileIdHigh || after.CreationTime != before.CreationTime)
                throw new IOException("Windows staged destination native identity changed during publication.");
        }
        catch (ObjectDisposedException ex) { throw new IOException("Windows staged metadata handle is no longer active.", ex); }
        finally { if (retained) target.DangerousRelease(); }
    }

    internal static bool IsSupportedMetadataReparse(ReadOnlySpan<byte> bytes) => bytes.Length is >= 20 and <= 16 * 1024
        && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0xA000000C
        && BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) + 8 == bytes.Length;

    [SupportedOSPlatform("windows")]
    private static byte[] ReadMetadataSecurity(SafeFileHandle handle)
    {
        // BACKUP_SECURITY_INFORMATION must never be accepted from a handle
        // lacking SACL rights, even if an API/provider returns partial data.
        RequireMetadataAccess(handle, 0x01020000);
        var result = MetadataNative.GetSecurityInfo(handle, 1, BackupSecurityInformation, out _, out _, out _, out _, out var descriptor);
        try
        {
            if (result != 0) throw new IOException("Complete Windows OWNER/GROUP/DACL/SACL visibility is unavailable: " + new Win32Exception((int)result).Message);
            var length = MetadataNative.GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > PayloadNativeMetadata.MaxEntryMetadataBytes) throw new IOException("Windows security descriptor exceeds its bounded budget.");
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); return bytes;
        }
        finally { if (descriptor != IntPtr.Zero) MetadataNative.LocalFree(descriptor); }
    }

    private static void RequireMetadataAccess(SafeFileHandle handle, uint required)
    {
        // PUBLIC_OBJECT_BASIC_INFORMATION has GrantedAccess at DWORD1 and ten
        // reserved DWORDs after its four public DWORDs (56 bytes on both RIDs).
        var information = new byte[56];
        var status = MetadataNative.NtQueryObject(handle, 0, information, (uint)information.Length, out var returned);
        if (status < 0 || returned < 8 || returned > information.Length
            || (BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(4)) & required) != required)
            throw new IOException("Windows native handle lacks complete requested security/publication access rights.");
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateRecoverableSecurity(byte[] descriptor)
    {
        var security = new RawSecurityDescriptor(descriptor, 0);
        using var current = WindowsIdentity.GetCurrent();
        if (security.Owner is null || security.Group is null || security.Owner != current.User || security.DiscretionaryAcl is null)
            throw new IOException("Windows merge requires effective-user ownership and a non-null recoverable DACL.");
        // Refuse deny, conditional/object and callback ACEs: they need their own
        // access/recovery policy rather than assuming a copied DACL stays usable.
        foreach (GenericAce ace in security.DiscretionaryAcl)
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false })
                throw new IOException("Windows restrictive or nonordinary DACL ACE requires a dedicated metadata transition.");
        if (security.SystemAcl is { } sacl)
            foreach (GenericAce ace in sacl)
                if (ace is not CommonAce { AceQualifier: AceQualifier.SystemAudit, IsCallback: false } && (byte)ace.AceType != 0x11) // SYSTEM_MANDATORY_LABEL_ACE_TYPE
                    throw new IOException("Windows nonordinary SACL ACE requires a dedicated metadata transition.");
        // AccessCheck accounts for deny-only groups and restricted tokens; merely
        // finding a matching SID in WindowsIdentity.Groups is not authority.
        var mapping = new MetadataGenericMapping { Read = 0x00120089, Write = 0x00120116, Execute = 0x001200A0, All = 0x001F01FF };
        var privileges = new byte[4096]; uint length = (uint)privileges.Length;
        if (!MetadataNative.AccessCheck(descriptor, current.AccessToken, 0x00130089, ref mapping, privileges, ref length, out _, out var allowed) || !allowed)
            throw new IOException("Windows DACL does not prove read/delete access needed for verified recovery.");
    }

    private readonly record struct MetadataInformation(uint Attributes, uint Links, bool Directory, bool DeletePending);
    private static MetadataInformation ReadMetadataInformation(SafeFileHandle handle)
    {
        if (!WindowsNative.GetFileInformationByHandleEx(handle, FileBasicInfo, out FileBasicInfoData basic, Marshal.SizeOf<FileBasicInfoData>())
            || !MetadataNative.GetFileInformationByHandleEx(handle, 1, out var standard, Marshal.SizeOf<MetadataStandardInfo>()))
            throw MetadataFailure("Read Windows native metadata information");
        return new(basic.FileAttributes, standard.NumberOfLinks, standard.Directory != 0, standard.DeletePending != 0);
    }

    private static ImmutableArray<WindowsPayloadStream> ReadMetadataStreams(SafeFileHandle handle, string kind, CancellationToken token,
        long maxAlternateBytes = PayloadNativeMetadata.MaxEntryMetadataBytes, long metadataBytes = PayloadNativeMetadata.MaxEntryMetadataBytes)
    {
        IntPtr context = IntPtr.Zero;
        using var reader = new WindowsBackupReadStream(
            (buffer, count) =>
            {
                if (!MetadataNative.BackupRead(handle, buffer, (uint)count, out var read, false, false, ref context))
                    throw MetadataFailure("Read held Windows backup stream");
                return checked((int)read);
            },
            count =>
            {
                if (!MetadataNative.BackupSeek(handle, (uint)count, (uint)((ulong)count >> 32), out var low, out var high, ref context)
                    || ((ulong)high << 32 | low) != (ulong)count) throw MetadataFailure("Skip held Windows unnamed/reparse data");
            },
            () =>
            {
                if (context != IntPtr.Zero) MetadataNative.BackupRead(handle, [], 0, out _, true, false, ref context);
            }, token);
        var result = ImmutableArray.CreateBuilder<WindowsPayloadStream>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long retained = 0, raw = 0; var streams = 0;
        var standardDataSeen = false; var reparseSeen = false;
        var header = new byte[20];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var headerBytes = reader.Read(header, 0, 20);
            if (headerBytes == 0) break;
            if (headerBytes != 20 || ++streams > PayloadNativeMetadata.MaxAttributes + 4) throw new IOException("Windows backup stream header/count is unsupported.");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var size = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            if (size < 0 || size > PayloadSnapshot.MaxBytes || nameBytes > 520 || (nameBytes & 1) != 0)
                throw new IOException("Windows backup stream length/name exceeds its budget.");
            var name = new byte[nameBytes]; reader.ReadExactly(name);
            if (id == 1 && nameBytes == 0 && attributes == 0)
            {
                if (standardDataSeen || kind != "file" && size != 0) throw new IOException("Windows unnamed data stream topology is unsupported.");
                standardDataSeen = true; reader.Skip(size); continue;
            }
            if (id == 8 && kind == "link" && nameBytes == 0 && size <= 16 * 1024)
            {
                if (reparseSeen) throw new IOException("Windows duplicate reparse stream is unsupported.");
                reparseSeen = true; reader.Skip(size); continue;
            } // Reparse bytes are independently bound by the payload walker.
            if (id != 4 || attributes != 0 || size > PayloadNativeMetadata.MaxAttributeBytes)
                throw new IOException("Unsupported Windows backup stream type/attributes/size: " + id);
            var streamName = new UnicodeEncoding(false, false, true).GetString(name);
            if (!WindowsPayloadMetadata.ValidStreamName(streamName) || !names.Add(streamName)
                || result.Count >= PayloadNativeMetadata.MaxAttributes || (retained += size + nameBytes) > metadataBytes || (raw += size) > maxAlternateBytes)
                throw new IOException("Windows alternate stream name/count/aggregate budget is unsupported.");
            var bytes = new byte[size]; reader.ReadExactly(bytes); result.Add(new(streamName, bytes));
        }
        return result.OrderBy(stream => stream.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void WriteMetadataStreams(SafeFileHandle handle, WindowsPayloadMetadata metadata, CancellationToken token)
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            // BACKUP_SECURITY_DATA includes the entire explicitly captured SD;
            // unlike BackupRead, its completeness is not inferred from a stream.
            Write(3, "", metadata.SecurityDescriptor, 2);
            foreach (var stream in metadata.Streams) Write(4, stream.Name, stream.Bytes, 0);
        }
        finally { if (context != IntPtr.Zero) MetadataNative.BackupWrite(handle, [], 0, out _, true, true, ref context); }
        void Write(uint id, string name, byte[] bytes, uint attributes)
        {
            var encoded = Encoding.Unicode.GetBytes(name);
            // BackupWrite requires buffers larger than WIN32_STREAM_ID. Send
            // each admitted bounded stream as one header/name/data frame,
            // including empty ADS, instead of tiny header or data calls.
            var header = new byte[checked(20 + encoded.Length + bytes.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(header, id); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), attributes);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)encoded.Length);
            encoded.CopyTo(header, 20); bytes.CopyTo(header, 20 + encoded.Length); Send(header);
        }
        void Send(byte[] bytes)
        {
            token.ThrowIfCancellationRequested();
            if (!MetadataNative.BackupWrite(handle, bytes, (uint)bytes.Length, out var written, false, true, ref context) || written != bytes.Length)
                throw MetadataFailure("Publish held Windows metadata stream");
        }
    }

    private static IOException MetadataFailure(string action) => new(action + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    [StructLayout(LayoutKind.Sequential)] private struct MetadataGenericMapping { internal uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MetadataStandardInfo
    { internal long AllocationSize; internal long EndOfFile; internal uint NumberOfLinks; internal byte DeletePending; internal byte Directory; }
    private static class MetadataNative
    {
        [DllImport("ntdll.dll")] internal static extern int NtQueryObject(SafeFileHandle handle, int type, [Out] byte[] information, uint size, out uint returned);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint desired, ref MetadataGenericMapping mapping, [Out] byte[] privileges, ref uint length, out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool allowed);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr value);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int type, out MetadataStandardInfo information, int size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, ref FileBasicInfoData information, int size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BackupRead(SafeFileHandle file, [Out] byte[] buffer, uint size, out uint read, [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool security, ref IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BackupSeek(SafeFileHandle file, uint low, uint high, out uint lowSeeked, out uint highSeeked, ref IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BackupWrite(SafeFileHandle file, byte[] buffer, uint size, out uint written, [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool security, ref IntPtr context);
    }
}
