using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed class WindowsSecureRemovalBackend : ISecureRemovalBackend
{
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileBasicInfo = 0;
    private const int FileIdInfo = 18;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FileIdExtdDirectoryRestartInfo = 20;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionPosixSemantics = 0x00000002;
    private const uint FileDispositionIgnoreReadonly = 0x00000010;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenForBackupIntent = 0x00004000;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int InitialFinalPathCapacity = 512;
    private const int MaxFinalPathCapacity = 32_768;
    private readonly Action? _directoryIdentityCapturedForTests;
    private readonly Action? _linkTargetObservedForTests;
    private readonly Action? _rootIdentityCapturedForTests;

    internal WindowsSecureRemovalBackend(
        Action? directoryIdentityCapturedForTests = null,
        Action? linkTargetObservedForTests = null,
        Action? rootIdentityCapturedForTests = null)
    {
        _directoryIdentityCapturedForTests = directoryIdentityCapturedForTests;
        _linkTargetObservedForTests = linkTargetObservedForTests;
        _rootIdentityCapturedForTests = rootIdentityCapturedForTests;
    }

    public bool TryCaptureIdentity(
        string path,
        out SecureFileIdentity identity,
        out string? error)
    {
        identity = default;
        error = null;
        try
        {
            using var handle = OpenEntry(
                path,
                directory: true,
                enumerateDirectory: false);
            var information = ReadIdentity(handle);
            identity = new SecureFileIdentity(
                information.Volume,
                information.FileIdLow,
                information.FileIdHigh,
                ReadFinalPath(handle),
                information.IsDirectory,
                information.IsReparsePoint,
                WindowsCreationTime: information.CreationTime,
                WindowsChangeTime: information.ChangeTime);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryCaptureDirectoryValidation(
        string path,
        out SecureDirectoryValidationSnapshot snapshot,
        out string? error)
    {
        snapshot = default;
        error = null;
        try
        {
            using var handle = OpenEntry(path, directory: true, enumerateDirectory: true);
            snapshot = CaptureDirectoryValidation(handle, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryCaptureDirectoryValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedDirectoryValidationSnapshot snapshot,
        out string? error)
    {
        snapshot = default;
        error = null;
        try
        {
            var components = GetRelativeComponents(rootPath, path);
            using var root = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var rootBefore = ReadIdentity(root);
            _rootIdentityCapturedForTests?.Invoke();
            using var target = OpenRelativeDirectory(root, components);
            var directory = CaptureDirectoryValidation(target, path);
            var rootIdentity = CaptureStableRootIdentity(root, rootBefore);
            snapshot = new SecureRootedDirectoryValidationSnapshot(
                rootIdentity,
                directory);
            return true;
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
        out string? error) =>
        TryCaptureLink(path, inspectTarget: false, out identity, out _, out error);

    public bool TryCaptureLinkValidation(
        string path,
        out SecureLinkValidationSnapshot snapshot,
        out string? error)
    {
        snapshot = default;
        if (!TryCaptureLink(
                path,
                inspectTarget: true,
                out var identity,
                out var isBroken,
                out error))
        {
            return false;
        }
        snapshot = new SecureLinkValidationSnapshot(identity, isBroken);
        return true;
    }

    private bool TryCaptureLink(
        string path,
        bool inspectTarget,
        out SecureLinkIdentity identity,
        out bool isBroken,
        out string? error)
    {
        identity = default;
        isBroken = false;
        error = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            var parentPath = Path.GetDirectoryName(fullPath)
                ?? throw new IOException($"path '{path}' has no parent directory");
            if (string.IsNullOrEmpty(name))
            {
                throw new IOException($"path '{path}' has no final entry name");
            }

            using var parent = OpenCanonicalDirectory(parentPath);
            (identity, isBroken) = CaptureLink(
                parent,
                name,
                path,
                inspectTarget);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryCaptureLinkValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedLinkValidationSnapshot snapshot,
        out string? error)
    {
        snapshot = default;
        error = null;
        try
        {
            var components = GetRelativeComponents(rootPath, path);
            var name = components[^1];
            using var root = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var rootBefore = ReadIdentity(root);
            _rootIdentityCapturedForTests?.Invoke();
            using var ownedParent = OpenRelativeParent(root, components);
            var parent = ownedParent ?? root;
            var (identity, isBroken) = CaptureLink(
                parent,
                name,
                path,
                inspectTarget: true);
            var rootIdentity = CaptureStableRootIdentity(root, rootBefore);
            snapshot = new SecureRootedLinkValidationSnapshot(
                rootIdentity,
                new SecureLinkValidationSnapshot(identity, isBroken));
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
            using var handle = OpenCanonicalDirectory(path);
            canonicalPath = ReadFinalPath(handle);
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

    private SecureDirectoryValidationSnapshot CaptureDirectoryValidation(
        SafeFileHandle handle,
        string displayPath)
    {
        var before = ReadIdentity(handle);
        if (!before.IsDirectory || before.IsReparsePoint)
        {
            throw new IOException(
                $"'{displayPath}' is no longer a non-link directory");
        }
        _directoryIdentityCapturedForTests?.Invoke();

        var hasSkill = EntryIsDirectory(
            handle,
            LocalSkillScanner.SkillFileName) is false;
        var hasGit = EntryIsDirectory(handle, ".git") is true;
        using var frame = new DirectoryFrame(
            handle,
            displayPath,
            before,
            depth: 0,
            ownsDirectory: false);
        var isEmpty = !frame.TryReadNext(out _, out var enumerationError);
        if (enumerationError is not null) throw new IOException(enumerationError);

        var canonicalPath = ReadFinalPath(handle);
        var after = ReadIdentity(handle);
        if (!Matches(before, after))
        {
            throw new IOException(
                "selected directory changed during policy inspection");
        }
        return new SecureDirectoryValidationSnapshot(
            ToSecureIdentity(after, canonicalPath),
            hasSkill,
            hasGit,
            isEmpty);
    }

    private (SecureLinkIdentity Identity, bool IsBroken) CaptureLink(
        SafeFileHandle parent,
        string name,
        string displayPath,
        bool inspectTarget)
    {
        var parentInformation = ReadIdentity(parent);
        using var link = OpenEntryAt(
            parent,
            name,
            directory: null,
            enumerateDirectory: false);
        var linkInformation = ReadIdentity(link);
        if (!linkInformation.IsReparsePoint)
        {
            throw new IOException(
                $"'{displayPath}' is no longer a symlink or reparse point");
        }

        var isBroken = false;
        if (inspectTarget)
        {
            try
            {
                using var target = OpenEntryAt(
                    parent,
                    name,
                    directory: null,
                    enumerateDirectory: false,
                    openReparsePoint: false,
                    requestDeleteAccess: false);
            }
            catch (WindowsOpenException ex) when (
                ex.NativeError is ErrorFileNotFound or ErrorPathNotFound)
            {
                isBroken = true;
            }
            _linkTargetObservedForTests?.Invoke();
            using var linkAfter = OpenEntryAt(
                parent,
                name,
                directory: null,
                enumerateDirectory: false);
            if (!Matches(linkInformation, ReadIdentity(linkAfter)))
            {
                throw new IOException("link changed during target inspection");
            }
        }

        var canonicalParent = ReadFinalPath(parent);
        var parentAfter = ReadIdentity(parent);
        if (!Matches(parentInformation, parentAfter))
        {
            throw new IOException("link parent changed during identity capture");
        }
        return (new SecureLinkIdentity(
            ToSecureIdentity(parentAfter, canonicalParent),
            ToSecureIdentity(
                linkInformation,
                Path.Combine(canonicalParent, name)),
            name), isBroken);
    }

    private static SecureFileIdentity CaptureStableRootIdentity(
        SafeFileHandle root,
        NativeIdentity before)
    {
        var canonicalRoot = ReadFinalPath(root);
        var after = ReadIdentity(root);
        if (!Matches(before, after))
        {
            throw new IOException("scan root changed during validation");
        }
        return ToSecureIdentity(after, canonicalRoot);
    }

    private static string[] GetRelativeComponents(string rootPath, string path)
    {
        var root = Path.GetFullPath(rootPath);
        var target = Path.GetFullPath(path);
        if (!PathResolver.IsInside(target, root)
            || PathIdentity.Equals(target, root))
        {
            throw new IOException(
                $"target '{target}' is not a child of scan root '{root}'");
        }

        var relative = Path.GetRelativePath(root, target);
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0
            || components.Any(component => component is "." or ".."))
        {
            throw new IOException("refusing an invalid scan-root-relative path");
        }
        return components;
    }

    private static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle root,
        IReadOnlyList<string> components)
    {
        SafeFileHandle? current = null;
        try
        {
            for (var index = 0; index < components.Count; index++)
            {
                var parent = current ?? root;
                var next = OpenEntryAt(
                    parent,
                    components[index],
                    directory: true,
                    enumerateDirectory: index == components.Count - 1,
                    openReparsePoint: index == components.Count - 1,
                    requestDeleteAccess: index == components.Count - 1);
                current?.Dispose();
                current = next;
            }
            return current
                ?? throw new IOException("scan-root-relative target is empty");
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? OpenRelativeParent(
        SafeFileHandle root,
        IReadOnlyList<string> components)
    {
        if (components.Count == 1) return null;

        SafeFileHandle? current = null;
        try
        {
            for (var index = 0; index < components.Count - 1; index++)
            {
                var parent = current ?? root;
                var next = OpenEntryAt(
                    parent,
                    components[index],
                    directory: true,
                    enumerateDirectory: false,
                    openReparsePoint: false,
                    requestDeleteAccess: false);
                current?.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current?.Dispose();
            throw;
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
        var fullPath = expectedIdentity.CanonicalPath;
        var frames = new Stack<DirectoryFrame>();
        SafeFileHandle? root = null;
        try
        {
            root = OpenEntry(fullPath, directory: true, enumerateDirectory: true);
            var rootIdentity = ReadIdentity(root);
            if (!Matches(expectedIdentity, rootIdentity))
            {
                failure(path, "selected target identity changed after validation");
                return;
            }
            if (!rootIdentity.IsDirectory || rootIdentity.IsReparsePoint)
            {
                failure(path, "selected target is no longer a non-link directory");
                return;
            }

            frames.Push(new DirectoryFrame(root, fullPath, rootIdentity, depth: 0));
            root = null;
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

                    var isDirectory = (child.Value.Attributes & FileAttributeDirectory) != 0;
                    var isReparsePoint =
                        (child.Value.Attributes & FileAttributeReparsePoint) != 0;
                    SafeFileHandle? childHandle = null;
                    try
                    {
                        childHandle = OpenEntryAt(
                            frame.Directory,
                            child.Value.Name,
                            directory: isDirectory,
                            enumerateDirectory: isDirectory && !isReparsePoint);
                        var openedIdentity = ReadIdentity(childHandle);
                        if (child.Value.FileIdLow != openedIdentity.FileIdLow
                            || child.Value.FileIdHigh != openedIdentity.FileIdHigh
                            || openedIdentity.Volume != rootIdentity.Volume
                            || openedIdentity.IsDirectory != isDirectory
                            || openedIdentity.IsReparsePoint != isReparsePoint)
                        {
                            failure(childPath, "entry identity changed during removal");
                            frame.PreventDelete();
                            continue;
                        }

                        if (isDirectory && !isReparsePoint)
                        {
                            if (frame.Depth >= maxDepth)
                            {
                                failure(childPath,
                                    $"directory nesting exceeds the safety limit of {maxDepth}");
                                frame.PreventDelete();
                                continue;
                            }
                            frames.Push(new DirectoryFrame(
                                childHandle,
                                childPath,
                                openedIdentity,
                                frame.Depth + 1));
                            childHandle = null;
                            continue;
                        }

                        entryDeleting(childPath, false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryDeleteHandle(childHandle, out var deleteError))
                        {
                            failure(childPath, deleteError!);
                            frame.PreventDelete();
                            continue;
                        }
                        entryDeleted(childPath, false);
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure(childPath, ex.Message);
                        frame.PreventDelete();
                        continue;
                    }
                    finally
                    {
                        childHandle?.Dispose();
                    }
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
                        entryDeleting(frame.DisplayPath, true);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryDeleteHandle(frame.Directory, out var deleteError))
                        {
                            failure(frame.DisplayPath, deleteError!);
                        }
                        else
                        {
                            entryDeleted(frame.DisplayPath, true);
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
            root?.Dispose();
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
        try
        {
            using var parent = OpenCanonicalDirectory(
                expectedIdentity.ParentIdentity.CanonicalPath);
            var parentIdentity = ReadIdentity(parent);
            if (!Matches(expectedIdentity.ParentIdentity, parentIdentity))
            {
                failure(path, "link parent identity changed after validation");
                return;
            }

            using var handle = OpenEntryAt(
                parent,
                expectedIdentity.Name,
                expectedIdentity.LinkIdentity.IsDirectory,
                enumerateDirectory: false);
            var identity = ReadIdentity(handle);
            if (!Matches(expectedIdentity.LinkIdentity, identity)
                || !identity.IsReparsePoint)
            {
                failure(path, "link identity changed after validation");
                return;
            }
            entryDeleting(path, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDeleteHandle(handle, out var deleteError))
            {
                failure(path, deleteError!);
                return;
            }
            entryDeleted(path, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure(path, ex.Message);
        }
    }

    private static SecureFileIdentity ToSecureIdentity(
        NativeIdentity identity,
        string canonicalPath) => new(
            identity.Volume,
            identity.FileIdLow,
            identity.FileIdHigh,
            canonicalPath,
            identity.IsDirectory,
            identity.IsReparsePoint,
            WindowsCreationTime: identity.CreationTime,
            WindowsChangeTime: identity.ChangeTime);

    private static SafeFileHandle OpenCanonicalDirectory(
        string path,
        bool enumerateDirectory = false)
    {
        var desiredAccess = FileReadAttributes | SynchronizeAccess;
        if (enumerateDirectory) desiredAccess |= FileListDirectory;
        var handle = WindowsNative.CreateFileW(
            ToExtendedPath(path),
            desiredAccess,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"open canonical directory '{path}' failed: {new Win32Exception(error).Message}");
        }
        return handle;
    }

    private static SafeFileHandle OpenEntry(
        string path,
        bool directory,
        bool enumerateDirectory)
    {
        var desiredAccess = DeleteAccess
            | FileReadAttributes
            | SynchronizeAccess;
        if (enumerateDirectory) desiredAccess |= FileListDirectory;
        var flags = OpenReparsePoint | (directory ? BackupSemantics : 0u);
        var handle = WindowsNative.CreateFileW(
            ToExtendedPath(path),
            desiredAccess,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"open '{path}' failed: {new Win32Exception(error).Message}");
        }
        return handle;
    }

    private static SafeFileHandle OpenEntryAt(
        SafeFileHandle parent,
        string name,
        bool? directory,
        bool enumerateDirectory,
        bool openReparsePoint = true,
        bool requestDeleteAccess = true)
    {
        ValidateRelativeEntryName(name);
        var desiredAccess = FileReadAttributes | SynchronizeAccess;
        if (requestDeleteAccess) desiredAccess |= DeleteAccess;
        if (enumerateDirectory) desiredAccess |= FileListDirectory;
        var openOptions = FileOpenForBackupIntent
            | FileSynchronousIoNonAlert;
        if (openReparsePoint) openOptions |= FileOpenReparsePoint;
        if (directory is true) openOptions |= FileDirectoryFile;
        if (directory is false) openOptions |= FileNonDirectoryFile;

        var nameBytes = checked(name.Length * sizeof(char));
        if (nameBytes > ushort.MaxValue - sizeof(char))
        {
            throw new IOException("relative entry name exceeds the native limit");
        }

        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = IntPtr.Zero;
        IntPtr rawHandle = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)nameBytes),
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer,
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
            };
            var status = WindowsNative.NtOpenFile(
                out rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                ShareRead | ShareWrite | ShareDelete,
                openOptions);
            if (status != 0)
            {
                var error = WindowsNative.RtlNtStatusToDosError(status);
                throw new WindowsOpenException(name, unchecked((int)error));
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            rawHandle = IntPtr.Zero;
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException($"open relative entry '{name}' returned an invalid handle");
            }
            return handle;
        }
        finally
        {
            if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
            {
                using var abandonedHandle = new SafeFileHandle(rawHandle, ownsHandle: true);
            }
            if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static bool? EntryIsDirectory(SafeFileHandle parent, string name)
    {
        try
        {
            using var entry = OpenEntryAt(
                parent,
                name,
                directory: null,
                enumerateDirectory: false,
                openReparsePoint: false,
                requestDeleteAccess: false);
            return ReadIdentity(entry).IsDirectory;
        }
        catch (WindowsOpenException ex) when (
            ex.NativeError is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }
    }

    private static void ValidateRelativeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('\\')
            || name.Contains('/')
            || name.Contains('\0'))
        {
            throw new IOException("refusing an invalid relative entry name");
        }
    }

    private static NativeIdentity ReadIdentity(SafeFileHandle handle)
    {
        FileBasicInfoData basicInformation;
        if (!WindowsNative.GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out basicInformation,
                Marshal.SizeOf<FileBasicInfoData>()))
        {
            throw new IOException(
                $"inspect opened entry metadata failed: {LastErrorMessage()}");
        }
        FileIdInfoData fileIdInformation;
        if (!WindowsNative.GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out fileIdInformation,
                Marshal.SizeOf<FileIdInfoData>()))
        {
            throw new IOException(
                $"inspect opened entry identity failed: {LastErrorMessage()}");
        }
        return new NativeIdentity(
            fileIdInformation.VolumeSerialNumber,
            fileIdInformation.FileId.Low,
            fileIdInformation.FileId.High,
            basicInformation.CreationTime,
            basicInformation.ChangeTime,
            (basicInformation.FileAttributes & FileAttributeDirectory) != 0,
            (basicInformation.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    private static bool TryDeleteHandle(SafeFileHandle handle, out string? error)
    {
        var extended = new FileDispositionInfoExData
        {
            Flags = FileDispositionDelete
                | FileDispositionPosixSemantics
                | FileDispositionIgnoreReadonly,
        };
        if (WindowsNative.SetFileInformationByHandle(
                handle,
                FileDispositionInfoEx,
                ref extended,
                Marshal.SizeOf<FileDispositionInfoExData>()))
        {
            error = null;
            return true;
        }

        var lastError = Marshal.GetLastPInvokeError();
        if (ShouldFallbackToLegacyDisposition(lastError))
        {
            var legacy = new FileDispositionInfoData { DeleteFile = true };
            if (WindowsNative.SetFileInformationByHandle(
                    handle,
                    FileDispositionInfo,
                    ref legacy,
                    Marshal.SizeOf<FileDispositionInfoData>()))
            {
                error = null;
                return true;
            }
            lastError = Marshal.GetLastPInvokeError();
        }

        error = $"delete opened entry failed: {new Win32Exception(lastError).Message}";
        return false;
    }

    private static bool Matches(SecureFileIdentity expected, NativeIdentity actual) =>
        expected.Volume == actual.Volume
        && expected.FileIdLow == actual.FileIdLow
        && expected.FileIdHigh == actual.FileIdHigh
        && expected.WindowsCreationTime == actual.CreationTime
        && expected.WindowsChangeTime == actual.ChangeTime
        && expected.IsDirectory == actual.IsDirectory
        && expected.IsReparsePoint == actual.IsReparsePoint;

    private static bool Matches(NativeIdentity expected, NativeIdentity actual) =>
        expected == actual;

    internal static bool ShouldFallbackToLegacyDisposition(int error) =>
        error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter;

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = InitialFinalPathCapacity;
        while (capacity <= MaxFinalPathCapacity)
        {
            var buffer = new StringBuilder(capacity);
            var length = WindowsNative.GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)capacity),
                flags: 0);
            if (length == 0)
            {
                throw new IOException(
                    $"resolve opened entry path failed: {LastErrorMessage()}");
            }
            if (length < capacity)
            {
                return NormalizeFinalPath(buffer.ToString());
            }
            if (length > MaxFinalPathCapacity)
            {
                break;
            }
            capacity = checked((int)length);
        }

        throw new IOException(
            $"resolved opened entry path exceeds {MaxFinalPathCapacity} characters");
    }

    internal static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }
        if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            && path.Length > extendedPrefix.Length + 2
            && char.IsAsciiLetter(path[extendedPrefix.Length])
            && path[extendedPrefix.Length + 1] == ':'
            && path[extendedPrefix.Length + 2] is '\\' or '/')
        {
            return path[extendedPrefix.Length..];
        }
        return path;
    }

    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return "\\\\?\\UNC\\" + fullPath[2..];
        }
        return "\\\\?\\" + fullPath;
    }

    private static string LastErrorMessage() =>
        new Win32Exception(Marshal.GetLastPInvokeError()).Message;

    private readonly record struct NativeIdentity(
        ulong Volume,
        ulong FileIdLow,
        ulong FileIdHigh,
        long CreationTime,
        long ChangeTime,
        bool IsDirectory,
        bool IsReparsePoint);

    private readonly record struct DirectoryEntry(
        string Name,
        uint Attributes,
        ulong FileIdLow,
        ulong FileIdHigh);

    private sealed class DirectoryFrame : IDisposable
    {
        private const int BufferSize = 1024;
        private const int NextEntryOffset = 0;
        private const int FileAttributesOffset = 56;
        private const int FileNameLengthOffset = 60;
        private const int FileIdLowOffset = 72;
        private const int FileIdHighOffset = 80;
        private const int FileNameOffset = 88;

        private readonly byte[] _buffer = new byte[BufferSize];
        private bool _restart = true;
        private bool _finished;
        private int _offset;
        private bool _hasBufferedEntry;

        internal DirectoryFrame(
            SafeFileHandle directory,
            string displayPath,
            NativeIdentity identity,
            int depth,
            bool ownsDirectory = true)
        {
            Directory = directory;
            DisplayPath = displayPath;
            Identity = identity;
            Depth = depth;
            _ownsDirectory = ownsDirectory;
        }

        private readonly bool _ownsDirectory;

        internal SafeFileHandle Directory { get; }
        internal string DisplayPath { get; }
        internal NativeIdentity Identity { get; }
        internal int Depth { get; }
        internal bool CanDelete { get; private set; } = true;

        internal void PreventDelete() => CanDelete = false;

        internal bool TryReadNext(out DirectoryEntry? entry, out string? error)
        {
            entry = null;
            error = null;
            if (_finished) return false;

            while (true)
            {
                if (!_hasBufferedEntry)
                {
                    Array.Clear(_buffer);
                    var informationClass = _restart
                        ? FileIdExtdDirectoryRestartInfo
                        : FileIdExtdDirectoryInfo;
                    if (!WindowsNative.GetFileInformationByHandleEx(
                            Directory,
                            informationClass,
                            _buffer,
                            _buffer.Length))
                    {
                        var nativeError = Marshal.GetLastPInvokeError();
                        _finished = true;
                        if (nativeError != ErrorNoMoreFiles)
                        {
                            error = $"enumerate opened directory failed: "
                                + new Win32Exception(nativeError).Message;
                        }
                        return false;
                    }
                    _restart = false;
                    _offset = 0;
                    _hasBufferedEntry = true;
                }

                var nameLength = BitConverter.ToInt32(_buffer, _offset + FileNameLengthOffset);
                if (nameLength < 0 || nameLength > _buffer.Length - _offset - FileNameOffset)
                {
                    _finished = true;
                    error = "opened-directory enumeration returned an invalid entry";
                    return false;
                }
                var name = Encoding.Unicode.GetString(
                    _buffer,
                    _offset + FileNameOffset,
                    nameLength);
                var attributes = BitConverter.ToUInt32(
                    _buffer,
                    _offset + FileAttributesOffset);
                var fileIdLow = BitConverter.ToUInt64(_buffer, _offset + FileIdLowOffset);
                var fileIdHigh = BitConverter.ToUInt64(_buffer, _offset + FileIdHighOffset);
                var next = BitConverter.ToUInt32(_buffer, _offset + NextEntryOffset);
                if (next == 0)
                {
                    _hasBufferedEntry = false;
                }
                else if (next > _buffer.Length - _offset)
                {
                    _finished = true;
                    error = "opened-directory enumeration returned an invalid offset";
                    return false;
                }
                else
                {
                    _offset += checked((int)next);
                }

                if (name is "." or "..") continue;
                entry = new DirectoryEntry(name, attributes, fileIdLow, fileIdHigh);
                return true;
            }
        }

        public void Dispose()
        {
            if (_ownsDirectory) Directory.Dispose();
        }
    }

    private sealed class WindowsOpenException : IOException
    {
        internal WindowsOpenException(string name, int nativeError)
            : base($"open relative entry '{name}' failed: "
                + new Win32Exception(nativeError).Message) =>
            NativeError = nativeError;

        internal int NativeError { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfoData
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoData
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoExData
    {
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoData
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }

    private static class WindowsNative
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtOpenFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            uint shareAccess,
            uint openOptions);

        [DllImport("ntdll.dll")]
        internal static extern uint RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            [Out] StringBuilder path,
            uint pathCapacity,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileIdInfoData information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileBasicInfoData information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            [Out] byte[] information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            ref FileDispositionInfoExData information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            ref FileDispositionInfoData information,
            int bufferSize);
    }
}
