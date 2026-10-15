using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal static NativeRegistryFileLease OpenRegistryFile(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parent = OpenCanonicalDirectory(Path.GetDirectoryName(path)!);
        FileStream? stream = null;
        SafeFileHandle? file = null;
        try
        {
            var name = Path.GetFileName(path);
            file = OpenEntryAt(parent, name, directory: false, enumerateDirectory: false,
                requestDeleteAccess: false, requestReadAccess: true, requestWriteAccess: true);
            var identity = ReadIdentity(file);
            if (identity.IsDirectory || identity.IsReparsePoint || WindowsNative.GetFileType(file) != 1)
                throw new IOException("Registry must be a regular non-link disk file.");
            var overlapped = new RegistryNative.Overlapped();
            token.ThrowIfCancellationRequested();
            if (!RegistryNative.LockFileEx(file, 3, 0, 1, 0, ref overlapped))
                throw new IOException("gh registry is already locked or inaccessible: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            stream = new FileStream(file, FileAccess.ReadWrite, bufferSize: 1);
            file = null;
            var canonical = Path.Combine(ReadFinalPath(parent), name);
            var lease = new WindowsRegistryFile(parent, stream, name, canonical, ReadIdentity(parent));
            lease.ValidateLocation(token);
            return lease;
        }
        catch { try { stream?.Dispose(); file?.Dispose(); } finally { parent.Dispose(); } throw; }
    }

    private sealed class WindowsRegistryFile(SafeFileHandle parent, FileStream stream, string name,
        string canonical, NativeIdentity parentIdentity) : NativeRegistryFileLease
    {
        protected override FileStream File => stream;
        internal override string CanonicalPath => canonical;
        protected override SecureFileIdentity CurrentIdentity(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ToSecureIdentity(ReadIdentity(stream.SafeFileHandle), ReadFinalPath(stream.SafeFileHandle));
        }
        protected override void ValidateNative(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var heldParent = ReadIdentity(parent);
            var heldFile = ReadIdentity(stream.SafeFileHandle);
            using var named = OpenEntryAt(parent, name, directory: false, enumerateDirectory: false, requestDeleteAccess: false);
            if (!NativeDirectoryMoveLease.SameObject(ToSecureIdentity(parentIdentity, ""), ToSecureIdentity(heldParent, ""))
                || ReadFinalPath(parent) != Path.GetDirectoryName(canonical) || ReadFinalPath(stream.SafeFileHandle) != canonical
                || heldFile.IsDirectory || heldFile.IsReparsePoint || !Matches(heldFile, ReadIdentity(named)))
                throw new IOException("Registry parent, address or native file identity changed.");
        }
        protected override void FlushNative(CancellationToken token) => token.ThrowIfCancellationRequested();
        protected override void DisposeNative() { try { stream.Dispose(); } finally { parent.Dispose(); } }
    }

    private static class RegistryNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal nuint Internal;
            internal nuint InternalHigh;
            internal uint Offset;
            internal uint OffsetHigh;
            internal IntPtr Event;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved,
            uint countLow, uint countHigh, ref Overlapped overlapped);
    }
}
