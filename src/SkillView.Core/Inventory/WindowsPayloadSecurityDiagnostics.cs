using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    // These are read-only probes on the already-retained object after the full
    // descriptor query failed. Their output is diagnostic, never an alternate
    // descriptor or evidence of complete SACL coverage.
    [SupportedOSPlatform("windows")]
    private static string DescribeMetadataSecurityQueryFailure(SafeFileHandle handle)
    {
        var fields = new List<string>();
        var basic = new byte[56];
        var status = MetadataNative.NtQueryObject(handle, 0, basic, (uint)basic.Length, out var returned);
        fields.Add($"granted(status=0x{status:X8},length={returned},mask={(status >= 0 && returned is >= 8 and <= 56 ? $"0x{BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(4)):X8}" : "unknown")})");
        fields.Add(WindowsMetadataPrivilege.DescribeEffectiveSecurityPrivilege());
        foreach (var flags in new uint[] { 0x7, 0x8, 0xF, 0x1FF, BackupSecurityInformation })
        {
            SecurityDiagnosticNative.QuerySecurityAccessMask(flags, out var required);
            // Unlike the original query, these probes request no individual
            // output pointers. The complete requested SD alone is sufficient.
            var error = SecurityDiagnosticNative.GetSecurityInfo(handle, 1, flags,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
            try
            {
                var length = error == 0 && descriptor != IntPtr.Zero ? MetadataNative.GetSecurityDescriptorLength(descriptor) : 0;
                fields.Add($"get-security(flags=0x{flags:X8},required=0x{required:X8},error={error},length={length})");
            }
            finally { if (descriptor != IntPtr.Zero) MetadataNative.LocalFree(descriptor); }
        }
        // Direct native query distinguishes a Win32 wrapper failure from the
        // kernel's full-query access decision. Allocate only after a bounded
        // size result; never retain or publish the resulting descriptor.
        var sizing = SecurityDiagnosticNative.NtQuerySecurityObject(handle, BackupSecurityInformation, null, 0, out var needed);
        fields.Add($"nt-backup-size(status=0x{sizing:X8},length={needed})");
        if (sizing == unchecked((int)0xC0000023) && needed is >= 20 and <= PayloadNativeMetadata.MaxEntryMetadataBytes)
        {
            var bytes = new byte[needed];
            var query = SecurityDiagnosticNative.NtQuerySecurityObject(handle, BackupSecurityInformation, bytes, (uint)bytes.Length, out var actual);
            fields.Add($"nt-backup-read(status=0x{query:X8},length={actual})");
        }
        return string.Join("; ", fields);
    }

    private static class SecurityDiagnosticNative
    {
        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySecurityObject(SafeFileHandle handle, uint information, [Out] byte[]? descriptor, uint length, out uint needed);
        [DllImport("advapi32.dll")]
        internal static extern void QuerySecurityAccessMask(uint information, out uint desired);
    }
}
