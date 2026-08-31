namespace SkillView.Inventory;

internal readonly record struct SecureFileIdentity(
    ulong Volume,
    ulong FileIdLow,
    ulong FileIdHigh,
    string CanonicalPath,
    bool IsDirectory,
    bool IsReparsePoint,
    long ChangeTimeSeconds = 0,
    long ChangeTimeNanoseconds = 0,
    long WindowsCreationTime = 0,
    long WindowsChangeTime = 0);

internal readonly record struct SecureLinkIdentity(
    SecureFileIdentity ParentIdentity,
    SecureFileIdentity LinkIdentity,
    string Name)
{
    internal string CanonicalPath => Path.Combine(ParentIdentity.CanonicalPath, Name);
}

internal readonly record struct SecureDirectoryValidationSnapshot(
    SecureFileIdentity Identity,
    bool HasSkillFile,
    bool HasGitDirectory,
    bool IsEmpty);

internal readonly record struct SecureLinkValidationSnapshot(
    SecureLinkIdentity Identity,
    bool IsBroken);

internal readonly record struct SecureRootedDirectoryValidationSnapshot(
    SecureFileIdentity RootIdentity,
    SecureDirectoryValidationSnapshot Directory);

internal readonly record struct SecureRootedLinkValidationSnapshot(
    SecureFileIdentity RootIdentity,
    SecureLinkValidationSnapshot Link);

internal interface ISecureRemovalBackend
{
    bool TryCaptureIdentity(string path, out SecureFileIdentity identity, out string? error);

    bool TryCaptureDirectoryValidation(
        string path,
        out SecureDirectoryValidationSnapshot snapshot,
        out string? error);

    bool TryCaptureDirectoryValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedDirectoryValidationSnapshot snapshot,
        out string? error);

    bool TryCaptureLinkIdentity(string path, out SecureLinkIdentity identity, out string? error);

    bool TryCaptureLinkValidation(
        string path,
        out SecureLinkValidationSnapshot snapshot,
        out string? error);

    bool TryCaptureLinkValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedLinkValidationSnapshot snapshot,
        out string? error);

    bool TryCanonicalizePath(string path, out string canonicalPath, out string? error);

    void RemoveTree(
        string path,
        SecureFileIdentity expectedIdentity,
        bool requireEmptyDirectory,
        int maxDepth,
        Action<string> entryObserved,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken);

    void RemoveLink(
        string path,
        SecureLinkIdentity expectedIdentity,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken);
}

internal static class SecureRemovalBackend
{
    private static readonly BackendSelection Selection = Create();
    private static ISecureRemovalBackend? Current => Selection.Backend;

    internal static bool IsSupported => Current is not null;
    internal static string? UnsupportedReason => Selection.Error;

    internal static bool TryCaptureIdentity(
        string path,
        out SecureFileIdentity identity,
        out string? error)
    {
        if (Current is null)
        {
            identity = default;
            error = UnsupportedReason
                ?? "secure removal is not supported on this operating system";
            return false;
        }

        return Current.TryCaptureIdentity(path, out identity, out error);
    }

    internal static bool TryCanonicalizePath(
        string path,
        out string canonicalPath,
        out string? error)
    {
        if (Current is null)
        {
            canonicalPath = string.Empty;
            error = UnsupportedReason
                ?? "secure removal is not supported on this operating system";
            return false;
        }
        return Current.TryCanonicalizePath(path, out canonicalPath, out error);
    }

    internal static bool TryCaptureDirectoryValidation(
        string path,
        out SecureDirectoryValidationSnapshot snapshot,
        out string? error)
    {
        if (Current is null)
        {
            snapshot = default;
            error = UnsupportedReason
                ?? "secure removal is not supported on this operating system";
            return false;
        }
        return Current.TryCaptureDirectoryValidation(path, out snapshot, out error);
    }

    internal static bool TryCaptureDirectoryValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedDirectoryValidationSnapshot snapshot,
        out string? error)
    {
        if (Current is null)
        {
            snapshot = default;
            error = UnsupportedReason
                ?? "secure removal is not supported on this operating system";
            return false;
        }
        return Current.TryCaptureDirectoryValidationWithinRoot(
            rootPath,
            path,
            out snapshot,
            out error);
    }

    internal static bool TryCaptureLinkIdentity(
        string path,
        out SecureLinkIdentity identity,
        out string? error)
    {
        if (Current is null)
        {
            identity = default;
            error = UnsupportedReason
                ?? "secure link removal is not supported on this operating system";
            return false;
        }

        return Current.TryCaptureLinkIdentity(path, out identity, out error);
    }

    internal static bool TryCaptureLinkValidation(
        string path,
        out SecureLinkValidationSnapshot snapshot,
        out string? error)
    {
        if (Current is null)
        {
            snapshot = default;
            error = UnsupportedReason
                ?? "secure link removal is not supported on this operating system";
            return false;
        }
        return Current.TryCaptureLinkValidation(path, out snapshot, out error);
    }

    internal static bool TryCaptureLinkValidationWithinRoot(
        string rootPath,
        string path,
        out SecureRootedLinkValidationSnapshot snapshot,
        out string? error)
    {
        if (Current is null)
        {
            snapshot = default;
            error = UnsupportedReason
                ?? "secure link removal is not supported on this operating system";
            return false;
        }
        return Current.TryCaptureLinkValidationWithinRoot(
            rootPath,
            path,
            out snapshot,
            out error);
    }

    internal static void RemoveTree(
        string path,
        SecureFileIdentity expectedIdentity,
        bool requireEmptyDirectory,
        int maxDepth,
        Action<string> entryObserved,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken) =>
        (Current ?? throw new PlatformNotSupportedException(
            "Secure removal is not supported on this operating system."))
        .RemoveTree(
            path,
            expectedIdentity,
            requireEmptyDirectory,
            maxDepth,
            entryObserved,
            entryDeleting,
            entryDeleted,
            failure,
            cancellationToken);

    internal static void RemoveLink(
        string path,
        SecureLinkIdentity expectedIdentity,
        Action<string, bool> entryDeleting,
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken) =>
        (Current ?? throw new PlatformNotSupportedException(
            "Secure removal is not supported on this operating system."))
        .RemoveLink(
            path,
            expectedIdentity,
            entryDeleting,
            entryDeleted,
            failure,
            cancellationToken);

    private static BackendSelection Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new BackendSelection(new WindowsSecureRemovalBackend(), null);
        }
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform)
        {
            return new BackendSelection(new UnixSecureRemovalBackend(), null);
        }
        return new BackendSelection(
            null,
            UnixSecureRemovalBackend.UnsupportedReason
                ?? "secure removal is not supported on this operating system");
    }

    private readonly record struct BackendSelection(
        ISecureRemovalBackend? Backend,
        string? Error);
}
