using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static NativeRegistryFileLease OpenRegistryFile(string path, CancellationToken token, Action<int>? afterLock = null)
    {
        token.ThrowIfCancellationRequested();
        var canonicalParent = RealPath(Path.GetDirectoryName(path)!);
        var parent = OpenDirectory(canonicalParent);
        FileStream? stream = null;
        SafeUnixHandle? native = null;
        SafeFileHandle? fileHandle = null;
        var locked = false;
        try
        {
            var name = Path.GetFileName(path);
            var flags = 2 | (OperatingSystem.IsMacOS() ? 0x100 | 0x1000000 | 4 : 0x20000 | 0x80000 | 0x800);
            int descriptor;
            if (OperatingSystem.IsLinux())
            {
                var how = new OpenHow
                {
                    Flags = (ulong)flags,
                    Resolve = UnixConstants.ResolveBeneath | UnixConstants.ResolveNoSymlinks | UnixConstants.ResolveNoCrossDevice
                };
                descriptor = RetryOnInterrupted(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return checked((int)UnixNative.syscall(UnixConstants.OpenAt2SystemCall, parent.FileDescriptor,
                        name, ref how, (nuint)Marshal.SizeOf<OpenHow>()));
                });
            }
            else descriptor = RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return UnixNative.openat(parent.FileDescriptor, name, flags); });
            native = CreateHandle(descriptor, "open existing registry failed");
            using var buffer = new StatBuffer();
            if (ReadStat(native, buffer.Pointer).FileType != 0x8000) throw new IOException("Registry must be a regular non-link file.");
            if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return StateNative.flock(native.FileDescriptor, 2 | 4); }) != 0)
                throw new IOException(LastError("gh registry is already locked"));
            locked = true;
            afterLock?.Invoke(descriptor);
            fileHandle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            native.SetHandleAsInvalid();
            native.Dispose(); native = null;
            stream = new FileStream(fileHandle, FileAccess.ReadWrite, bufferSize: 1);
            fileHandle = null;
            var canonical = Path.Combine(ReadFinalPath(parent), name);
            var lease = new UnixRegistryFile(parent, stream, name, canonical,
                ToSecureIdentity(ReadStat(parent, buffer.Pointer), ReadFinalPath(parent)));
            lease.ValidateLocation(token);
            return lease;
        }
        catch (Exception failure)
        {
            // Unlock before releasing whichever object currently owns the fd.
            // A caller canceled after flock must not leave a fork-retained OFD
            // locked, and cleanup may not replace that original failure.
            Cleanup(() =>
            {
                if (!locked) return;
                var owner = stream?.SafeFileHandle ?? fileHandle;
                var descriptor = owner is { IsClosed: false, IsInvalid: false }
                    ? owner.DangerousGetHandle().ToInt32() : native is { IsClosed: false, IsInvalid: false } ? native.FileDescriptor : -1;
                if (descriptor >= 0 && RetryOnInterrupted(() => StateNative.flock(descriptor, 8)) != 0)
                    throw new IOException(LastError("registry unlock during failed open failed"));
            });
            Cleanup(() => stream?.Dispose());
            Cleanup(() => fileHandle?.Dispose());
            Cleanup(() => native?.Dispose());
            Cleanup(parent.Dispose);
            throw;

            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception cleanupError) { failure.Data["Registry cleanup " + failure.Data.Count] = cleanupError; }
            }
        }
    }

    private sealed class UnixRegistryFile(SafeUnixHandle parent, FileStream stream, string name,
        string canonical, SecureFileIdentity parentIdentity) : NativeRegistryFileLease
    {
        protected override FileStream File => stream;
        internal override string CanonicalPath => canonical;
        private SafeUnixHandle BorrowFile() => new(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), ownsHandle: false);
        protected override SecureFileIdentity CurrentIdentity(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var borrowed = BorrowFile();
            using var buffer = new StatBuffer();
            return ToSecureIdentity(ReadStat(borrowed, buffer.Pointer), ReadFinalPath(borrowed));
        }
        protected override void ValidateNative(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var buffer = new StatBuffer();
            var heldParent = ToSecureIdentity(ReadStat(parent, buffer.Pointer), ReadFinalPath(parent));
            var heldFile = CurrentIdentity(token);
            if (!NativeDirectoryMoveLease.SameObject(parentIdentity, heldParent)
                || heldParent.CanonicalPath != Path.GetDirectoryName(canonical) || heldFile.CanonicalPath != canonical
                || heldFile.IsDirectory || heldFile.IsReparsePoint
                || !TryReadStatAt(parent, name, buffer.Pointer, out var named, out _)
                || ToSecureIdentity(named, canonical) != heldFile)
                throw new IOException("Registry parent, address or native file identity changed.");
        }
        protected override void FlushNative(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsMacOS() && RetryOnInterrupted(() =>
                { token.ThrowIfCancellationRequested(); return StateNative.fcntl_fullsync(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 51); }) != 0)
                throw new IOException(LastError("registry device durability barrier failed"));
        }
        protected override void DisposeNative()
        {
            // flock belongs to the open file description. A concurrent fork
            // can temporarily retain a duplicate even with CLOEXEC; unlock
            // explicitly before closing, just as gh's unlock function does.
            try
            {
                if (RetryOnInterrupted(() => StateNative.flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 8)) != 0)
                    throw new IOException(LastError("registry unlock failed"));
            }
            finally { try { stream.Dispose(); } finally { parent.Dispose(); } }
        }
    }
}
