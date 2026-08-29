using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed class UnixSecureRemovalBackend : ISecureRemovalBackend
{
    private const int StatBufferSize = 512;
    private const int InitialLinuxFinalPathCapacity = 512;
    private const int MaxLinuxFinalPathCapacity = 32_768;
    private const int MacAttributeBufferCapacity = 8_192;
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint SymlinkType = 0xA000;

    internal static bool IsSupportedOnCurrentPlatform =>
        OperatingSystem.IsMacOS()
        || (OperatingSystem.IsLinux() && IsCurrentLinuxPlatformSupported());

    public bool TryCaptureIdentity(
        string path,
        out SecureFileIdentity identity,
        out string? error)
    {
        identity = default;
        error = null;
        try
        {
            using var statBuffer = new StatBuffer();
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            var parentPath = Path.GetDirectoryName(fullPath)
                ?? throw new IOException($"path '{path}' has no parent directory");
            if (string.IsNullOrEmpty(name))
            {
                throw new IOException($"path '{path}' has no final entry name");
            }

            var resolvedParent = RealPath(parentPath);
            using var handle = OpenAbsoluteDirectory(
                Path.Combine(resolvedParent, name),
                out var parent,
                out _);
            using (parent)
            {
                var stat = ReadStat(handle, statBuffer.Pointer);
                var canonicalPath = ReadFinalPath(handle);
                identity = new SecureFileIdentity(
                    stat.Device,
                    stat.Inode,
                    0,
                    canonicalPath,
                    IsDirectory: stat.IsDirectory,
                    IsReparsePoint: stat.IsSymlink,
                    stat.ChangeTimeSeconds,
                    stat.ChangeTimeNanoseconds);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryCaptureLinkIdentity(
        string path,
        out SecureLinkIdentity identity,
        out string? error)
    {
        identity = default;
        error = null;
        try
        {
            using var statBuffer = new StatBuffer();
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            var parentPath = Path.GetDirectoryName(fullPath)
                ?? throw new IOException($"path '{path}' has no parent directory");
            if (string.IsNullOrEmpty(name))
            {
                throw new IOException($"path '{path}' has no final entry name");
            }

            var resolvedParent = RealPath(parentPath);
            using var parent = OpenDirectory(resolvedParent);
            var parentStat = ReadStat(parent, statBuffer.Pointer);
            var canonicalParent = ReadFinalPath(parent);
            if (!TryReadStatAt(parent, name, statBuffer.Pointer,
                    out var linkStat, out var statError))
            {
                throw new IOException(statError);
            }
            if (!linkStat.IsSymlink)
            {
                throw new IOException($"'{path}' is no longer a symlink");
            }

            var canonicalLink = Path.Combine(canonicalParent, name);
            identity = new SecureLinkIdentity(
                ToSecureIdentity(parentStat, canonicalParent),
                ToSecureIdentity(linkStat, canonicalLink),
                name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryCanonicalizePath(
        string path,
        out string canonicalPath,
        out string? error)
    {
        try
        {
            var resolvedPath = RealPath(path);
            using var handle = OpenAbsoluteDirectory(resolvedPath, out var parent, out _);
            using (parent)
            {
                canonicalPath = ReadFinalPath(handle);
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            canonicalPath = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    public void RemoveTree(
        string path,
        SecureFileIdentity expectedIdentity,
        bool requireEmptyDirectory,
        int maxDepth,
        Action<string> entryObserved,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = expectedIdentity.CanonicalPath;

        SafeUnixHandle? target = null;
        SafeUnixHandle? parent = null;
        var frames = new Stack<DirectoryFrame>();
        using var statBuffer = new StatBuffer();
        try
        {
            target = OpenAbsoluteDirectory(canonicalPath, out parent, out var targetName);
            var targetStat = ReadStat(target, statBuffer.Pointer);
            if (!Matches(expectedIdentity, targetStat))
            {
                failure(path, "selected target identity changed after validation");
                return;
            }
            if (!targetStat.IsDirectory || targetStat.IsSymlink)
            {
                failure(path, "selected target is no longer a non-link directory");
                return;
            }

            frames.Push(new DirectoryFrame(
                target,
                parent,
                targetName,
                canonicalPath,
                depth: 0,
                ownsParent: true));
            target = null;
            parent = null;

            while (frames.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.TryReadNext(out var child, out var enumerationError))
                {
                    if (child is null) continue;
                    if (requireEmptyDirectory && frame.Depth == 0)
                    {
                        failure(frame.DisplayPath,
                            "validated empty directory is no longer empty");
                        return;
                    }
                    var childPath = Path.Combine(frame.DisplayPath, child.Value.Name);
                    entryObserved(childPath);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryReadStatAt(frame.Directory, child.Value.Name, statBuffer.Pointer,
                            out var childStat, out var statError))
                    {
                        failure(childPath, statError!);
                        frame.PreventDelete();
                        continue;
                    }
                    if (child.Value.Inode != 0 && child.Value.Inode != childStat.Inode)
                    {
                        failure(childPath, "entry identity changed during removal");
                        frame.PreventDelete();
                        continue;
                    }

                    if (childStat.IsDirectory && !childStat.IsSymlink)
                    {
                        if (frame.Depth >= maxDepth)
                        {
                            failure(childPath,
                                $"directory nesting exceeds the safety limit of {maxDepth}");
                            frame.PreventDelete();
                            continue;
                        }
                        if (childStat.Device != targetStat.Device)
                        {
                            failure(childPath, "cross-filesystem mount traversal refused");
                            frame.PreventDelete();
                            continue;
                        }

                        SafeUnixHandle? childHandle = null;
                        try
                        {
                            childHandle = OpenDirectoryAt(
                                frame.Directory,
                                child.Value.Name,
                                refuseNestedMount: true);
                            var openedStat = ReadStat(childHandle, statBuffer.Pointer);
                            if (!Matches(childStat, openedStat))
                            {
                                failure(childPath, "directory identity changed before it could be opened");
                                frame.PreventDelete();
                                continue;
                            }

                            frames.Push(new DirectoryFrame(
                                childHandle,
                                frame.Directory,
                                child.Value.Name,
                                childPath,
                                frame.Depth + 1,
                                ownsParent: false));
                            childHandle = null;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failure(childPath, ex.Message);
                            frame.PreventDelete();
                        }
                        finally
                        {
                            childHandle?.Dispose();
                        }
                        continue;
                    }

                    if (!TryReadStatAt(frame.Directory, child.Value.Name, statBuffer.Pointer,
                            out var beforeDelete, out statError)
                        || !Matches(childStat, beforeDelete))
                    {
                        failure(childPath, statError ?? "entry identity changed before deletion");
                        frame.PreventDelete();
                        continue;
                    }

                    entryDeleting(childPath, false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (UnixNative.unlinkat(
                            frame.Directory.FileDescriptor,
                            child.Value.Name,
                            flags: 0) != 0)
                    {
                        failure(childPath, LastError("delete failed"));
                        frame.PreventDelete();
                        continue;
                    }
                    entryDeleted(childPath, false);
                    continue;
                }

                if (enumerationError is not null)
                {
                    failure(frame.DisplayPath, enumerationError);
                    frame.PreventDelete();
                }

                frames.Pop();
                try
                {
                    if (frame.CanDelete)
                    {
                        var openedBeforeDelete = ReadStat(
                            frame.Directory,
                            statBuffer.Pointer);
                        if (!TryReadStatAt(frame.Parent, frame.Name, statBuffer.Pointer,
                                out var beforeDelete, out var statError)
                            || !Matches(openedBeforeDelete, beforeDelete))
                        {
                            failure(frame.DisplayPath,
                                statError ?? "directory identity changed before deletion");
                        }
                        else
                        {
                            entryDeleting(frame.DisplayPath, true);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (UnixNative.unlinkat(
                                    frame.Parent.FileDescriptor,
                                    frame.Name,
                                    UnixConstants.RemoveDirectoryFlag) != 0)
                            {
                                failure(frame.DisplayPath, LastError("delete directory failed"));
                            }
                            else
                            {
                                entryDeleted(frame.DisplayPath, true);
                            }
                        }
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure(path, ex.Message);
        }
        finally
        {
            while (frames.Count > 0) frames.Pop().Dispose();
            target?.Dispose();
            parent?.Dispose();
        }
    }

    public void RemoveLink(
        string path,
        SecureLinkIdentity expectedIdentity,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeUnixHandle? parent = null;
        using var statBuffer = new StatBuffer();
        try
        {
            parent = OpenDirectory(expectedIdentity.ParentIdentity.CanonicalPath);
            var parentStat = ReadStat(parent, statBuffer.Pointer);
            if (!Matches(expectedIdentity.ParentIdentity, parentStat))
            {
                failure(path, "link parent identity changed after validation");
                return;
            }
            if (!TryReadStatAt(parent, expectedIdentity.Name, statBuffer.Pointer,
                    out var identity, out var statError))
            {
                failure(path, statError!);
                return;
            }
            if (!Matches(expectedIdentity.LinkIdentity, identity)
                || !identity.IsSymlink)
            {
                failure(path, "link identity changed after validation");
                return;
            }
            entryDeleting(path, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadStatAt(parent, expectedIdentity.Name, statBuffer.Pointer,
                    out var beforeDelete, out statError)
                || !Matches(expectedIdentity.LinkIdentity, beforeDelete))
            {
                failure(path, statError ?? "link identity changed before deletion");
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (UnixNative.unlinkat(
                    parent.FileDescriptor,
                    expectedIdentity.Name,
                    flags: 0) != 0)
            {
                failure(path, LastError("unlink failed"));
                return;
            }
            entryDeleted(path, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure(path, ex.Message);
        }
        finally
        {
            parent?.Dispose();
        }
    }

    private static SecureFileIdentity ToSecureIdentity(
        NativeStat stat,
        string canonicalPath) => new(
            stat.Device,
            stat.Inode,
            0,
            canonicalPath,
            stat.IsDirectory,
            stat.IsSymlink,
            stat.ChangeTimeSeconds,
            stat.ChangeTimeNanoseconds);

    private static SafeUnixHandle OpenAbsoluteDirectory(
        string path,
        out SafeUnixHandle parent,
        out string name)
    {
        parent = OpenAbsoluteParent(path, out name);
        try
        {
            return OpenDirectoryAt(parent, name, refuseNestedMount: false);
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private static SafeUnixHandle OpenAbsoluteParent(string path, out string name)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"path '{path}' has no filesystem root");
        var relative = Path.GetRelativePath(root, fullPath);
        var components = relative.Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            throw new IOException("refusing to remove a filesystem root");
        }

        var current = OpenDirectory(root);
        try
        {
            for (var i = 0; i < components.Length - 1; i++)
            {
                var next = OpenDirectoryAt(
                    current,
                    components[i],
                    refuseNestedMount: false);
                current.Dispose();
                current = next;
            }
            name = components[^1];
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeUnixHandle OpenDirectory(string path)
    {
        var fd = UnixNative.open(path, UnixConstants.DirectoryOpenFlags);
        return CreateHandle(fd, $"open directory '{path}' failed");
    }

    private static SafeUnixHandle OpenDirectoryAt(
        SafeUnixHandle parent,
        string name,
        bool refuseNestedMount)
    {
        int fd;
        if (OperatingSystem.IsLinux() && refuseNestedMount)
        {
            var how = new OpenHow
            {
                Flags = unchecked((ulong)UnixConstants.DirectoryOpenFlags),
                Resolve = UnixConstants.ResolveBeneath
                    | UnixConstants.ResolveNoSymlinks
                    | UnixConstants.ResolveNoCrossDevice,
            };
            fd = checked((int)UnixNative.syscall(
                UnixConstants.OpenAt2SystemCall,
                parent.FileDescriptor,
                name,
                ref how,
                (nuint)Marshal.SizeOf<OpenHow>()));
        }
        else
        {
            fd = UnixNative.openat(
                parent.FileDescriptor,
                name,
                UnixConstants.DirectoryOpenFlags);
        }
        return CreateHandle(fd, $"open directory entry '{name}' failed");
    }

    private static SafeUnixHandle CreateHandle(int fd, string detail)
    {
        if (fd < 0) throw new IOException($"{detail}: {LastErrorMessage()}");
        return new SafeUnixHandle(fd);
    }

    private static string RealPath(string path)
    {
        var result = UnixNative.realpath(path, IntPtr.Zero);
        if (result == IntPtr.Zero)
        {
            throw new IOException($"resolve '{path}' failed: {LastErrorMessage()}");
        }
        try
        {
            return Marshal.PtrToStringUTF8(result)
                ?? throw new IOException($"resolve '{path}' returned an invalid path");
        }
        finally
        {
            UnixNative.free(result);
        }
    }

    private static string ReadFinalPath(SafeUnixHandle handle)
    {
        if (OperatingSystem.IsMacOS())
        {
            var attributes = new MacAttributeList
            {
                BitmapCount = UnixConstants.AttributeBitmapCount,
                CommonAttributes = UnixConstants.AttributeCommonFullPath,
            };
            var buffer = new byte[MacAttributeBufferCapacity];
            if (UnixNative.fgetattrlist(
                    handle.FileDescriptor,
                    ref attributes,
                    buffer,
                    checked((nuint)buffer.Length),
                    options: 0) != 0)
            {
                throw new IOException(
                    $"resolve opened entry path failed: {LastErrorMessage()}");
            }

            const int lengthFieldOffset = 0;
            const int referenceOffset = sizeof(uint);
            const int referenceSize = sizeof(int) + sizeof(uint);
            var returnedLength = BitConverter.ToUInt32(buffer, lengthFieldOffset);
            var dataOffset = BitConverter.ToInt32(buffer, referenceOffset);
            var dataLength = BitConverter.ToUInt32(
                buffer,
                referenceOffset + sizeof(int));
            var dataStart = referenceOffset + (long)dataOffset;
            var dataEnd = dataStart + dataLength;
            if (returnedLength > buffer.Length
                || returnedLength < referenceOffset + referenceSize
                || dataStart < referenceOffset + referenceSize
                || dataLength <= 1
                || dataEnd > returnedLength
                || dataEnd > buffer.Length
                || buffer[checked((int)dataEnd - 1)] != 0)
            {
                throw new IOException("resolve opened entry returned an invalid path");
            }

            var path = Encoding.UTF8.GetString(
                buffer,
                checked((int)dataStart),
                checked((int)dataLength - 1));
            return Path.GetFullPath(path);
        }

        var descriptorPath = $"/proc/self/fd/{handle.FileDescriptor}";
        var capacity = InitialLinuxFinalPathCapacity;
        while (capacity <= MaxLinuxFinalPathCapacity)
        {
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var length = UnixNative.readlink(
                    descriptorPath,
                    buffer,
                    checked((nuint)capacity));
                if (length < 0)
                {
                    throw new IOException(
                        $"resolve opened entry path failed: {LastErrorMessage()}");
                }
                if (length < capacity)
                {
                    var path = Marshal.PtrToStringUTF8(buffer, checked((int)length))
                        ?? throw new IOException(
                            "resolve opened entry returned an invalid path");
                    if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "opened entry was unlinked while its identity was captured");
                    }
                    return Path.GetFullPath(path);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            capacity = checked(capacity * 2);
        }

        throw new IOException(
            $"resolved opened entry path exceeds {MaxLinuxFinalPathCapacity} bytes");
    }

    private static NativeStat ReadStat(SafeUnixHandle handle, IntPtr buffer)
    {
        if (UnixNative.fstat(handle.FileDescriptor, buffer) != 0)
        {
            throw new IOException($"inspect opened entry failed: {LastErrorMessage()}");
        }
        return ParseStat(buffer);
    }

    private static bool TryReadStatAt(
        SafeUnixHandle parent,
        string name,
        IntPtr buffer,
        out NativeStat stat,
        out string? error)
    {
        if (UnixNative.fstatat(
                parent.FileDescriptor,
                name,
                buffer,
                UnixConstants.NoFollowFlag) != 0)
        {
            stat = default;
            error = $"inspect entry failed: {LastErrorMessage()}";
            return false;
        }
        stat = ParseStat(buffer);
        error = null;
        return true;
    }

    private static NativeStat ParseStat(IntPtr buffer)
    {
        if (OperatingSystem.IsMacOS())
        {
            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            var changeTimeSeconds = Marshal.ReadInt64(buffer, 64);
            var changeTimeNanoseconds = Marshal.ReadInt64(buffer, 72);
            return new NativeStat(
                device,
                inode,
                mode,
                changeTimeSeconds,
                changeTimeNanoseconds);
        }

        if (!OperatingSystem.IsLinux()
            || !TryGetLinuxStatLayout(
                RuntimeInformation.ProcessArchitecture,
                BitConverter.IsLittleEndian,
                out var layout))
        {
            throw new PlatformNotSupportedException(
                $"The native stat layout for {RuntimeInformation.OSDescription} "
                + $"({RuntimeInformation.ProcessArchitecture}) is not supported.");
        }

        return new NativeStat(
            unchecked((ulong)Marshal.ReadInt64(buffer, layout.DeviceOffset)),
            unchecked((ulong)Marshal.ReadInt64(buffer, layout.InodeOffset)),
            unchecked((uint)Marshal.ReadInt32(buffer, layout.ModeOffset)),
            Marshal.ReadInt64(buffer, layout.ChangeTimeSecondsOffset),
            Marshal.ReadInt64(buffer, layout.ChangeTimeNanosecondsOffset));
    }

    internal static bool TryGetLinuxStatLayout(
        Architecture architecture,
        bool isLittleEndian,
        out LinuxStatLayout layout)
    {
        // .NET's supported Linux x64 and ARM64 targets use the libc ABI layouts
        // below. Refuse unknown architectures and endianness rather than reading
        // an unverified field from the native buffer.
        layout = (architecture, isLittleEndian) switch
        {
            (Architecture.X64, true) => new LinuxStatLayout(0, 8, 24, 104, 112),
            (Architecture.Arm64, true) => new LinuxStatLayout(0, 8, 16, 104, 112),
            _ => default,
        };
        return layout != default;
    }

    internal static bool IsLinuxPlatformSupported(
        Architecture architecture,
        bool isLittleEndian,
        bool openAt2Available) =>
        openAt2Available
        && TryGetLinuxStatLayout(architecture, isLittleEndian, out _);

    private static bool IsCurrentLinuxPlatformSupported()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var isLittleEndian = BitConverter.IsLittleEndian;
        if (!TryGetLinuxStatLayout(architecture, isLittleEndian, out _))
        {
            return false;
        }
        return IsLinuxPlatformSupported(
            architecture,
            isLittleEndian,
            openAt2Available: ProbeOpenAt2());
    }

    private static bool ProbeOpenAt2()
    {
        var how = new OpenHow
        {
            Flags = unchecked((ulong)UnixConstants.DirectoryOpenFlags),
            Resolve = UnixConstants.ResolveBeneath
                | UnixConstants.ResolveNoSymlinks
                | UnixConstants.ResolveNoCrossDevice,
        };
        var descriptor = UnixNative.syscall(
            UnixConstants.OpenAt2SystemCall,
            UnixConstants.CurrentWorkingDirectory,
            ".",
            ref how,
            (nuint)Marshal.SizeOf<OpenHow>());
        if (descriptor < 0 || descriptor > int.MaxValue)
        {
            return false;
        }

        using var handle = new SafeUnixHandle(checked((int)descriptor));
        return true;
    }

    private static bool Matches(SecureFileIdentity expected, NativeStat actual) =>
        expected.Volume == actual.Device
        && expected.FileIdLow == actual.Inode
        && expected.FileIdHigh == 0
        && expected.IsDirectory == actual.IsDirectory
        && expected.IsReparsePoint == actual.IsSymlink
        && expected.ChangeTimeSeconds == actual.ChangeTimeSeconds
        && expected.ChangeTimeNanoseconds == actual.ChangeTimeNanoseconds;

    private static bool Matches(NativeStat expected, NativeStat actual) =>
        expected.Device == actual.Device
        && expected.Inode == actual.Inode
        && expected.FileType == actual.FileType
        && expected.ChangeTimeSeconds == actual.ChangeTimeSeconds
        && expected.ChangeTimeNanoseconds == actual.ChangeTimeNanoseconds;

    private static string LastError(string action) => $"{action}: {LastErrorMessage()}";

    private static string LastErrorMessage() =>
        new Win32Exception(Marshal.GetLastPInvokeError()).Message;

    private readonly record struct NativeStat(
        ulong Device,
        ulong Inode,
        uint Mode,
        long ChangeTimeSeconds,
        long ChangeTimeNanoseconds)
    {
        internal uint FileType => Mode & FileTypeMask;
        internal bool IsDirectory => FileType == DirectoryType;
        internal bool IsSymlink => FileType == SymlinkType;
    }

    internal readonly record struct LinuxStatLayout(
        int DeviceOffset,
        int InodeOffset,
        int ModeOffset,
        int ChangeTimeSecondsOffset,
        int ChangeTimeNanosecondsOffset);

    private readonly record struct DirectoryEntry(string Name, ulong Inode);

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacAttributeList
    {
        internal ushort BitmapCount;
        internal ushort Reserved;
        internal uint CommonAttributes;
        internal uint VolumeAttributes;
        internal uint DirectoryAttributes;
        internal uint FileAttributes;
        internal uint ForkAttributes;
    }

    private sealed class DirectoryFrame : IDisposable
    {
        private IntPtr _directoryStream;
        private readonly bool _ownsParent;
        private bool _finished;

        internal DirectoryFrame(
            SafeUnixHandle directory,
            SafeUnixHandle parent,
            string name,
            string displayPath,
            int depth,
            bool ownsParent)
        {
            Directory = directory;
            Parent = parent;
            Name = name;
            DisplayPath = displayPath;
            Depth = depth;
            _ownsParent = ownsParent;
        }

        internal SafeUnixHandle Directory { get; }
        internal SafeUnixHandle Parent { get; }
        internal string Name { get; }
        internal string DisplayPath { get; }
        internal int Depth { get; }
        internal bool CanDelete { get; private set; } = true;

        internal void PreventDelete() => CanDelete = false;

        internal bool TryReadNext(out DirectoryEntry? entry, out string? error)
        {
            entry = null;
            error = null;
            if (_finished) return false;
            if (_directoryStream == IntPtr.Zero)
            {
                var duplicate = UnixNative.dup(Directory.FileDescriptor);
                if (duplicate < 0)
                {
                    error = LastError("duplicate directory handle failed");
                    _finished = true;
                    return false;
                }
                _directoryStream = UnixNative.fdopendir(duplicate);
                if (_directoryStream == IntPtr.Zero)
                {
                    UnixNative.close(duplicate);
                    error = LastError("open directory stream failed");
                    _finished = true;
                    return false;
                }
            }

            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var nativeEntry = UnixNative.readdir(_directoryStream);
                if (nativeEntry == IntPtr.Zero)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno != 0) error = new Win32Exception(errno).Message;
                    _finished = true;
                    return false;
                }

                string? name;
                ulong inode;
                if (OperatingSystem.IsMacOS())
                {
                    inode = unchecked((ulong)Marshal.ReadInt64(nativeEntry, 0));
                    var nameLength = unchecked((ushort)Marshal.ReadInt16(nativeEntry, 18));
                    name = Marshal.PtrToStringUTF8(IntPtr.Add(nativeEntry, 21), nameLength);
                }
                else
                {
                    inode = unchecked((ulong)Marshal.ReadInt64(nativeEntry, 0));
                    name = Marshal.PtrToStringUTF8(IntPtr.Add(nativeEntry, 19));
                }
                if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
                entry = new DirectoryEntry(name, inode);
                return true;
            }
        }

        public void Dispose()
        {
            if (_directoryStream != IntPtr.Zero)
            {
                UnixNative.closedir(_directoryStream);
                _directoryStream = IntPtr.Zero;
            }
            Directory.Dispose();
            if (_ownsParent) Parent.Dispose();
        }
    }

    private sealed class SafeUnixHandle : SafeHandleMinusOneIsInvalid
    {
        internal SafeUnixHandle(int fd) : base(ownsHandle: true) =>
            SetHandle(new IntPtr(fd));

        internal int FileDescriptor => handle.ToInt32();

        protected override bool ReleaseHandle() => UnixNative.close(handle.ToInt32()) == 0;
    }

    private sealed class StatBuffer : IDisposable
    {
        internal StatBuffer() => Pointer = Marshal.AllocHGlobal(StatBufferSize);

        internal IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private static class UnixConstants
    {
        internal static int DirectoryOpenFlags => OperatingSystem.IsMacOS()
            ? 0x00100000 | 0x00000100 | 0x01000000
            : 0x00010000 | 0x00020000 | 0x00080000;

        internal static int NoFollowFlag => OperatingSystem.IsMacOS() ? 0x0020 : 0x0100;
        internal static int RemoveDirectoryFlag => OperatingSystem.IsMacOS() ? 0x0080 : 0x0200;
        internal const ushort AttributeBitmapCount = 5;
        internal const uint AttributeCommonFullPath = 0x08000000;
        internal const int CurrentWorkingDirectory = -100;
        internal const long OpenAt2SystemCall = 437;
        internal const ulong ResolveNoCrossDevice = 0x01;
        internal const ulong ResolveNoSymlinks = 0x04;
        internal const ulong ResolveBeneath = 0x08;
    }

    private static class UnixNative
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int openat(int directory, string path, int flags);

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        internal static extern long syscall(
            long number,
            int directory,
            string path,
            ref OpenHow how,
            nuint size);

        [DllImport("libc", SetLastError = true)]
        internal static extern int fstat(int descriptor, IntPtr buffer);

        [DllImport("libc", SetLastError = true)]
        internal static extern int fgetattrlist(
            int descriptor,
            ref MacAttributeList attributes,
            [Out] byte[] buffer,
            nuint bufferSize,
            uint options);

        [DllImport("libc", SetLastError = true)]
        internal static extern nint readlink(string path, IntPtr buffer, nuint bufferSize);

        [DllImport("libc", SetLastError = true)]
        internal static extern int fstatat(int directory, string path, IntPtr buffer, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int unlinkat(int directory, string path, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int dup(int descriptor);

        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int descriptor);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr fdopendir(int descriptor);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr readdir(IntPtr directory);

        [DllImport("libc", SetLastError = true)]
        internal static extern int closedir(IntPtr directory);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr realpath(string path, IntPtr resolvedPath);

        [DllImport("libc")]
        internal static extern void free(IntPtr pointer);
    }
}
