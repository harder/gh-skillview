using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

/// Synchronous OS-thread scope. Duplicate the effective token, never change the
/// process token or an existing caller impersonation token, and restore exactly.
internal sealed class WindowsMetadataPrivilege : IDisposable
{
    private SafeAccessTokenHandle? previous;
    private SafeAccessTokenHandle? active;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private WindowsMetadataPrivilege() { }

    internal static WindowsMetadataPrivilege Enter()
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Windows metadata privilege is unavailable.");
        var scope = new WindowsMetadataPrivilege();
        SafeAccessTokenHandle? basis = null;
        try
        {
            if (Native.OpenThreadToken(Native.GetCurrentThread(), 0x000E, true, out var existing))
                scope.previous = basis = existing;
            else
            {
                var error = Marshal.GetLastPInvokeError();
                existing.Dispose();
                if (error != 1008) throw new IOException("Inspect effective Windows token: " + new Win32Exception(error).Message);
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x000A, out basis)) throw Failure("Open process token for duplication");
            }
            if (!Native.DuplicateTokenEx(basis, 0x002E, IntPtr.Zero, 2, 2, out var duplicate))
            {
                var error = Marshal.GetLastPInvokeError(); duplicate.Dispose();
                throw new IOException("Duplicate effective metadata token: " + new Win32Exception(error).Message);
            }
            scope.active = duplicate;
            if (!Native.LookupPrivilegeValueW(null, "SeSecurityPrivilege", out var luid)) throw Failure("Find security privilege");
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!Native.AdjustTokenPrivileges(duplicate, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                || Marshal.GetLastPInvokeError() != 0) throw Failure("Complete SACL visibility requires SeSecurityPrivilege");
            if (!Native.SetThreadToken(IntPtr.Zero, duplicate.DangerousGetHandle())) throw Failure("Enter metadata thread token");
            if (!ReferenceEquals(basis, scope.previous)) basis.Dispose();
            return scope;
        }
        catch
        {
            if (!ReferenceEquals(basis, scope.previous)) basis?.Dispose();
            scope.active?.Dispose(); scope.previous?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (active is null) return;
        // Losing the original security context cannot be treated as a recoverable
        // metadata refusal: no application work may run with the elevated token.
        if (thread != Environment.CurrentManagedThreadId || !Native.SetThreadToken(IntPtr.Zero, previous?.DangerousGetHandle() ?? IntPtr.Zero))
            Environment.FailFast("Cannot restore Windows metadata thread security context.");
        active.Dispose(); active = null; previous?.Dispose(); previous = null;
    }

    internal static string DescribeEffectiveSecurityPrivilege()
    {
        if (!OperatingSystem.IsWindows()) return "SeSecurityPrivilege=unavailable";
        // Inspect the effective thread token without changing it or enabling
        // any privilege. PrivilegeCheck requires an impersonation token.
        var opened = Native.OpenThreadToken(Native.GetCurrentThread(), 0x0008, true, out var token);
        var error = Marshal.GetLastPInvokeError();
        using (token)
        {
            if (!opened) return $"SeSecurityPrivilege(thread-token-error={error})";
            if (!Native.LookupPrivilegeValueW(null, "SeSecurityPrivilege", out var luid))
                return $"SeSecurityPrivilege(lookup-error={Marshal.GetLastPInvokeError()})";
            var required = new PrivilegeSet { Count = 1, Control = 1, Luid = luid, Attributes = 2 };
            if (!Native.PrivilegeCheck(token, ref required, out var enabled))
                return $"SeSecurityPrivilege(check-error={Marshal.GetLastPInvokeError()})";
            return $"SeSecurityPrivilege(enabled={enabled},attributes=0x{required.Attributes:X8})";
        }
    }

    private static IOException Failure(string operation) => new(operation + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { internal uint Count; internal Luid Luid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct PrivilegeSet { internal uint Count, Control; internal Luid Luid; internal uint Attributes; }
    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenThreadToken(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool asSelf, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetThreadToken(IntPtr thread, IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PrivilegeCheck(SafeAccessTokenHandle token, ref PrivilegeSet privileges, [MarshalAs(UnmanagedType.Bool)] out bool enabled);
    }
}
