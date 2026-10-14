using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend : ISecureRemovalBackend
{
    private const int DirectoryEntryHeaderSize = 88;
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
    private readonly Action? _directoryFingerprintCapturedForTests;
    private readonly Action? _fingerprintEntryCapturedForTests;
    private readonly Action? _fingerprintEnumerationAdmittedForTests;

    internal WindowsSecureRemovalBackend(
        Action? directoryIdentityCapturedForTests = null,
        Action? linkTargetObservedForTests = null,
        Action? rootIdentityCapturedForTests = null,
        Action? directoryFingerprintCapturedForTests = null,
        Action? fingerprintEntryCapturedForTests = null,
        Action? fingerprintEnumerationAdmittedForTests = null)
    {
        _directoryIdentityCapturedForTests = directoryIdentityCapturedForTests;
        _linkTargetObservedForTests = linkTargetObservedForTests;
        _rootIdentityCapturedForTests = rootIdentityCapturedForTests;
        _directoryFingerprintCapturedForTests = directoryFingerprintCapturedForTests;
        _fingerprintEntryCapturedForTests = fingerprintEntryCapturedForTests;
        _fingerprintEnumerationAdmittedForTests = fingerprintEnumerationAdmittedForTests;
    }

    internal static FileStream OpenRegularFileForSnapshot(string path)
    {
        var handle = WindowsNative.CreateFileW(ToExtendedPath(path), 0x80000000,
            ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException($"open payload file failed: {LastErrorMessage()}");
            var identity = ReadIdentity(handle);
            if (identity.IsDirectory || identity.IsReparsePoint || WindowsNative.GetFileType(handle) != 1)
                throw new IOException("payload entry is not a regular non-link disk file");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static OwnedDirectoryLease OpenOwnedDirectory(string path)
    {
        var handle = OpenEntry(path, directory: true, enumerateDirectory: true);
        try
        {
            var canonical = ReadFinalPath(handle);
            var backend = new WindowsSecureRemovalBackend();
            return new OwnedDirectoryLease(token =>
            {
                token.ThrowIfCancellationRequested();
                if (!PathIdentity.Equals(ReadFinalPath(handle), canonical))
                    throw new IOException("owned staging directory was moved; retained for review");
                return backend.CaptureDirectoryValidation(handle, canonical, token);
            }, handle.Dispose);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SecureFileIdentity WalkPayload(string path, int maxDepth,
        Func<NativePayloadItem, PayloadFileEvidence?> observe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var root = OpenEntry(path, directory: true, enumerateDirectory: true, requestDeleteAccess: false);
        var rootBefore = ReadIdentity(root);
        if (!rootBefore.IsDirectory || rootBefore.IsReparsePoint)
            throw new IOException("payload root is not a non-link directory");
        var canonical = ReadFinalPath(root);
        var observations = new List<(string Path, NativeIdentity Identity, string? LinkHash, PayloadFileEvidence? FileEvidence)>();
        var inspector = new WindowsSecureRemovalBackend();
        var directoryFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var frames = new Stack<(DirectoryFrame Frame, string Relative, string Fingerprint)>();
        var rootFingerprint = inspector.CaptureDirectoryFingerprint(root, canonical, token);
        directoryFingerprints[""] = rootFingerprint;
        frames.Push((new DirectoryFrame(root, canonical, rootBefore, 0, ownsDirectory: false), "", rootFingerprint));
        try
        {
            while (frames.TryPeek(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (!current.Frame.TryReadNext(out var entry, out var error))
                {
                    if (error is not null) throw new IOException(error);
                    if (!Matches(current.Frame.Identity, ReadIdentity(current.Frame.Directory))
                        || current.Fingerprint != inspector.CaptureDirectoryFingerprint(current.Frame.Directory, current.Relative, token))
                        throw new IOException("payload directory changed during comparison");
                    frames.Pop().Frame.Dispose();
                    continue;
                }
                token.ThrowIfCancellationRequested();
                if (entry is null) continue;
                var relative = current.Relative + entry.Value.Name;
                var isDirectory = (entry.Value.Attributes & FileAttributeDirectory) != 0;
                var isReparse = (entry.Value.Attributes & FileAttributeReparsePoint) != 0;
                SafeFileHandle? child = OpenEntryAt(current.Frame.Directory, entry.Value.Name,
                    directory: null, enumerateDirectory: isDirectory && !isReparse,
                    requestDeleteAccess: false, requestReadAccess: !isDirectory && !isReparse);
                try
                {
                    var before = ReadIdentity(child);
                    if (before.FileIdLow != entry.Value.FileIdLow || before.FileIdHigh != entry.Value.FileIdHigh
                        || before.IsDirectory != isDirectory || before.IsReparsePoint != isReparse)
                        throw new IOException("payload entry changed before it could be opened");
                    if (observations.Count >= 10_000) throw new IOException("payload entry limit exceeded");
                    observations.Add((relative, before, null, null));
                    if (isReparse)
                    {
                        token.ThrowIfCancellationRequested();
                        var bytes = ReadReparseData(child);
                        observations[^1] = (relative, before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), null);
                        observe(new(relative, "link", null, null, bytes, Convert.ToBase64String(bytes)));
                    }
                    else if (isDirectory)
                    {
                        if (current.Frame.Depth >= maxDepth) throw new IOException("payload depth limit exceeded");
                        var childFingerprint = inspector.CaptureDirectoryFingerprint(child, relative, token);
                        directoryFingerprints[relative] = childFingerprint;
                        observe(new(relative, "directory", null, null, null, null));
                        frames.Push((new DirectoryFrame(child, relative, before, current.Frame.Depth + 1), relative + "/", childFingerprint));
                        child = null;
                    }
                    else
                    {
                        if (WindowsNative.GetFileType(child) != 1) throw new IOException("payload entry is not a disk file");
                        using var stream = new FileStream(child, FileAccess.Read);
                        var evidence = observe(new(relative, "file", stream, null, null, null))
                            ?? throw new IOException("payload file evidence is missing");
                        observations[^1] = (relative, before, null, evidence);
                        token.ThrowIfCancellationRequested();
                        if (!Matches(before, ReadIdentity(child))) throw new IOException("payload file changed during comparison");
                        // The stream owns the handle; all identity checks ran before its disposal.
                        child = null;
                    }
                    if (child is not null && !Matches(before, ReadIdentity(child)))
                        throw new IOException("payload link changed during comparison");
                }
                finally { child?.Dispose(); }
            }
            var knownDirectories = observations.Where(item => item.Identity.IsDirectory && !item.Identity.IsReparsePoint)
                .ToDictionary(item => item.Path, item => item.Identity, StringComparer.Ordinal);
            foreach (var observation in observations)
            {
                token.ThrowIfCancellationRequested();
                var parts = observation.Path.Split('/');
                using var heldParent = OpenPayloadParent(root, parts[..^1], knownDirectories, token);
                using var entry = OpenEntryAt(heldParent, parts[^1], directory: null,
                    enumerateDirectory: observation.Identity.IsDirectory && !observation.Identity.IsReparsePoint,
                    requestDeleteAccess: false, requestReadAccess: observation.FileEvidence is not null);
                if (!Matches(observation.Identity, ReadIdentity(entry)))
                    throw new IOException("payload entry changed before snapshot completion");
                if (observation.Identity.IsDirectory && !observation.Identity.IsReparsePoint
                    && directoryFingerprints[observation.Path] != inspector.CaptureDirectoryFingerprint(entry, observation.Path, token))
                    throw new IOException("payload directory contents changed before snapshot completion");
                if (observation.LinkHash is { } expectedLink
                    && expectedLink != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ReadReparseData(entry))))
                    throw new IOException("payload link changed before snapshot completion");
                if (observation.FileEvidence is { } expectedFile)
                {
                    if (WindowsNative.GetFileType(entry) != 1) throw new IOException("payload entry is not a disk file");
                    using var stream = new FileStream(entry, FileAccess.Read);
                    SecurePayloadWalker.VerifyContent(stream, expectedFile, token);
                    if (!Matches(observation.Identity, ReadIdentity(entry)))
                        throw new IOException("payload file changed during completion verification");
                }
            }
            if (rootFingerprint != inspector.CaptureDirectoryFingerprint(root, canonical, token))
                throw new IOException("payload root contents changed before snapshot completion");
            if (!Matches(rootBefore, ReadIdentity(root))) throw new IOException("payload root changed before snapshot completion");
            return ToSecureIdentity(rootBefore, canonical);
        }
        finally { while (frames.Count > 0) frames.Pop().Frame.Dispose(); }
    }

    private static SafeFileHandle OpenPayloadParent(SafeFileHandle root, string[] parts,
        IReadOnlyDictionary<string, NativeIdentity> known, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A zero-component parent remains held by the caller; duplicate its
        // native handle instead of reopening its pathname.
        if (!WindowsNative.DuplicateHandle(WindowsNative.GetCurrentProcess(), root,
                WindowsNative.GetCurrentProcess(), out var current, 0, false, 2))
            throw new IOException($"duplicate payload root failed: {LastErrorMessage()}");
        try
        {
            var relative = "";
            foreach (var part in parts)
            {
                token.ThrowIfCancellationRequested();
                var next = OpenEntryAt(current, part, directory: true, enumerateDirectory: false, requestDeleteAccess: false);
                try
                {
                    relative += relative.Length == 0 ? part : "/" + part;
                    if (!known.TryGetValue(relative, out var expected) || !Matches(expected, ReadIdentity(next)))
                        throw new IOException("payload ancestor changed before snapshot completion");
                }
                catch { next.Dispose(); throw; }
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static byte[] ReadReparseData(SafeFileHandle child)
    {
        var buffer = new byte[MaxReparseDataBytes];
        if (!WindowsNative.DeviceIoControl(child, FsctlGetReparsePoint,
                IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
            throw new IOException($"inspect reparse data failed: {LastErrorMessage()}");
        if (returned < 8 || returned > buffer.Length) throw new IOException("invalid reparse data length");
        return buffer.AsSpan(0, (int)returned).ToArray();
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
        out string? error,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshot = default;
        error = null;
        try
        {
            using var handle = OpenEntry(path, directory: true, enumerateDirectory: true);
            snapshot = CaptureDirectoryValidation(handle, path, cancellationToken);
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
        out string? error,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshot = default;
        error = null;
        try
        {
            var components = GetRelativeComponents(rootPath, path);
            using var root = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var rootBefore = ReadIdentity(root);
            _rootIdentityCapturedForTests?.Invoke();
            using var target = OpenRelativeDirectory(root, components);
            var directory = CaptureDirectoryValidation(target, path, cancellationToken);
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
        string displayPath,
        CancellationToken cancellationToken)
    {
        var before = ReadIdentity(handle);
        if (!before.IsDirectory || before.IsReparsePoint)
        {
            throw new IOException(
                $"'{displayPath}' is no longer a non-link directory");
        }
        _directoryIdentityCapturedForTests?.Invoke();

        var fingerprintBefore = CaptureDirectoryFingerprint(handle, displayPath, cancellationToken);
        _directoryFingerprintCapturedForTests?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var hasSkill = EntryIsDirectory(
            handle,
            LocalSkillScanner.SkillFileName) is false;
        var hasGit = EntryExistsWithoutFollowing(handle, ".git");
        using var frame = new DirectoryFrame(
            handle,
            displayPath,
            before,
            depth: 0,
            ownsDirectory: false);
        var isEmpty = !frame.TryReadNext(out _, out var enumerationError);
        if (enumerationError is not null) throw new IOException(enumerationError);
        cancellationToken.ThrowIfCancellationRequested();

        var fingerprintAfter = CaptureDirectoryFingerprint(handle, displayPath, cancellationToken);
        var canonicalPath = ReadFinalPath(handle);
        var after = ReadIdentity(handle);
        if (!Matches(before, after) || fingerprintBefore != fingerprintAfter)
        {
            throw new IOException(
                "selected directory changed during policy inspection");
        }
        return new SecureDirectoryValidationSnapshot(
            ToSecureIdentity(after, canonicalPath) with { DirectoryFingerprint = fingerprintAfter },
            hasSkill,
            hasGit,
            isEmpty);
    }

    internal const int MaxFingerprintEntries = 4096;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const int MaxReparseDataBytes = 16 * 1024;

    // Inspect only immediate children, through the held parent. The compact
    // digest is deterministic despite filesystem enumeration order. No path
    // reopen or directory-enumeration timestamp becomes an authority address.
    private string CaptureDirectoryFingerprint(
        SafeFileHandle directory,
        string displayPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<(string Name, byte[] Digest)>();
        using var frame = new DirectoryFrame(directory, displayPath,
            ReadIdentity(directory), depth: 0, ownsDirectory: false);
        string? enumerationError;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fingerprintEnumerationAdmittedForTests?.Invoke();
            if (!frame.TryReadNext(out var entry, out enumerationError)) break;
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null) continue;
            if (entries.Count >= MaxFingerprintEntries)
                throw new IOException("directory validation exceeds immediate-entry limit");
            using var child = OpenEntryAt(directory, entry.Value.Name,
                directory: null, enumerateDirectory: false, requestDeleteAccess: false);
            var before = ReadIdentity(child);
            if (before.FileIdLow != entry.Value.FileIdLow
                || before.FileIdHigh != entry.Value.FileIdHigh
                || before.IsDirectory != ((entry.Value.Attributes & FileAttributeDirectory) != 0)
                || before.IsReparsePoint != ((entry.Value.Attributes & FileAttributeReparsePoint) != 0))
                throw new IOException("directory entry identity changed during validation");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] reparseData = [];
            if (before.IsReparsePoint)
            {
                var buffer = new byte[MaxReparseDataBytes];
                if (!WindowsNative.DeviceIoControl(child, FsctlGetReparsePoint,
                        IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
                    throw new IOException($"inspect reparse data failed: {LastErrorMessage()}");
                if (returned < 8 || returned > buffer.Length)
                    throw new IOException("invalid reparse data length");
                reparseData = buffer.AsSpan(0, (int)returned).ToArray();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(before, ReadIdentity(child)))
                throw new IOException("directory entry identity changed during validation");
            using var encoded = new MemoryStream();
            using (var writer = new BinaryWriter(encoded, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(before.Volume);
                writer.Write(before.FileIdLow);
                writer.Write(before.FileIdHigh);
                writer.Write(before.CreationTime);
                writer.Write(before.ChangeTime);
                writer.Write(before.IsDirectory);
                writer.Write(before.IsReparsePoint);
                writer.Write(reparseData.Length);
                writer.Write(reparseData);
            }
            entries.Add((entry.Value.Name,
                System.Security.Cryptography.SHA256.HashData(encoded.GetBuffer().AsSpan(0, (int)encoded.Length))));
            _fingerprintEntryCapturedForTests?.Invoke();
        }
        // TryReadNext reports terminal enumeration failures separately.
        // A second frame would restart enumeration, so retain the last result.
        if (enumerationError is not null) throw new IOException(enumerationError);
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = new MemoryStream();
            using (var writer = new BinaryWriter(encoded, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(entry.Name.Length);
                foreach (var character in entry.Name) writer.Write((ushort)character);
                writer.Write(entry.Digest.Length);
                writer.Write(entry.Digest);
            }
            digest.AppendData(encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(digest.GetHashAndReset());
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

            if (expectedIdentity.DirectoryFingerprint is not { } fingerprint
                || fingerprint != CaptureDirectoryFingerprint(root, fullPath, cancellationToken))
            {
                failure(path, "selected target identity changed after validation (immediate entries)");
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

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
                        if (!TryDeleteHandle(
                                childHandle,
                                cancellationToken,
                                out var deleteError))
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
                        if (!TryDeleteHandle(
                                frame.Directory,
                                cancellationToken,
                                out var deleteError))
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
            if (!TryDeleteHandle(handle, cancellationToken, out var deleteError))
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
        bool enumerateDirectory,
        bool requestDeleteAccess = true)
    {
        var desiredAccess = FileReadAttributes | SynchronizeAccess;
        if (requestDeleteAccess) desiredAccess |= DeleteAccess;
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
        bool requestDeleteAccess = true,
        bool requestReadAccess = false)
    {
        ValidateRelativeEntryName(name);
        var desiredAccess = FileReadAttributes | SynchronizeAccess;
        if (requestDeleteAccess) desiredAccess |= DeleteAccess;
        if (requestReadAccess) desiredAccess |= 0x00000001;
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

    private static bool EntryExistsWithoutFollowing(SafeFileHandle parent, string name)
    {
        try
        {
            using var entry = OpenEntryAt(parent, name, directory: null,
                enumerateDirectory: false, openReparsePoint: true, requestDeleteAccess: false);
            return true;
        }
        catch (WindowsOpenException ex) when (ex.NativeError is ErrorFileNotFound or ErrorPathNotFound)
        { return false; }
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

    private static bool TryDeleteHandle(
        SafeFileHandle handle,
        CancellationToken cancellationToken,
        out string? error)
    {
        if (TryDeleteWithFallback(
                handle,
                cancellationToken,
                static (file, useExtendedDisposition) =>
                    SetDisposition(file, useExtendedDisposition),
                out var lastError))
        {
            error = null;
            return true;
        }

        error = $"delete opened entry failed: {new Win32Exception(lastError).Message}";
        return false;
    }

    internal static bool TryDeleteWithFallback<TState>(
        TState state,
        CancellationToken cancellationToken,
        Func<TState, bool, (bool Succeeded, int Error)> tryDisposition,
        out int lastError)
    {
        ArgumentNullException.ThrowIfNull(tryDisposition);

        cancellationToken.ThrowIfCancellationRequested();
        var result = tryDisposition(state, true);
        if (result.Succeeded)
        {
            lastError = 0;
            return true;
        }

        lastError = result.Error;
        if (!ShouldFallbackToLegacyDisposition(lastError)) return false;

        // FileDispositionInfo is a second destructive native boundary, not
        // merely error handling for FileDispositionInfoEx. Cancellation can
        // arrive while the first call is returning, so gate the fallback too.
        cancellationToken.ThrowIfCancellationRequested();
        result = tryDisposition(state, false);
        lastError = result.Error;
        return result.Succeeded;
    }

    private static (bool Succeeded, int Error) SetDisposition(
        SafeFileHandle handle,
        bool useExtendedDisposition)
    {
        bool succeeded;
        if (useExtendedDisposition)
        {
            var extended = new FileDispositionInfoExData
            {
                Flags = FileDispositionDelete
                    | FileDispositionPosixSemantics
                    | FileDispositionIgnoreReadonly,
            };
            succeeded = WindowsNative.SetFileInformationByHandle(
                handle,
                FileDispositionInfoEx,
                ref extended,
                Marshal.SizeOf<FileDispositionInfoExData>());
        }
        else
        {
            var legacy = new FileDispositionInfoData { DeleteFile = 1 };
            succeeded = WindowsNative.SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref legacy,
                Marshal.SizeOf<FileDispositionInfoData>());
        }

        return (succeeded, succeeded ? 0 : Marshal.GetLastPInvokeError());
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

    internal static int LegacyDispositionInfoSize =>
        Marshal.SizeOf<FileDispositionInfoData>();

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
        private const int FileNameOffset = DirectoryEntryHeaderSize;

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

                if (!HasCompleteDirectoryEntryHeader(_buffer.Length, _offset))
                {
                    _finished = true;
                    error = "opened-directory enumeration returned a truncated entry";
                    return false;
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
                else if (next > _buffer.Length - _offset - FileNameOffset)
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

    internal static bool HasCompleteDirectoryEntryHeader(
        int bufferLength,
        int offset) =>
        bufferLength >= DirectoryEntryHeaderSize
        && offset >= 0
        && offset <= bufferLength - DirectoryEntryHeaderSize;

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
        // Win32 BOOLEAN is one byte; it is not the four-byte BOOL used for
        // SetFileInformationByHandle's return value.
        internal byte DeleteFile;
    }

    private static class WindowsNative
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source,
            IntPtr targetProcess, out SafeFileHandle duplicate, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle handle, uint controlCode, IntPtr input, uint inputSize,
            [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

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
