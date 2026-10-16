using System.Buffers.Binary;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    // Read-only, bounded context for a failed full native descriptor query.
    [SupportedOSPlatform("windows")]
    private static string DescribeMetadataSecurityQueryFailure(SafeFileHandle handle)
    {
        var basic = new byte[56];
        var status = MetadataNative.NtQueryObject(handle, 0, basic, (uint)basic.Length, out var returned);
        return $"granted(status=0x{status:X8},length={returned},mask={(status >= 0 && returned is >= 8 and <= 56 ? $"0x{BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(4)):X8}" : "unknown")}); "
            + WindowsMetadataPrivilege.DescribeEffectiveSecurityPrivilege();
    }
}
