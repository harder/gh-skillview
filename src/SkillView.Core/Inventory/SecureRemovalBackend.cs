namespace SkillView.Inventory;

internal readonly record struct SecureFileIdentity(
    ulong Volume,
    ulong FileIdLow,
    ulong FileIdHigh,
    string CanonicalPath,
    bool IsDirectory,
    bool IsReparsePoint);

internal interface ISecureRemovalBackend
{
    bool TryCaptureIdentity(string path, out SecureFileIdentity identity, out string? error);

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
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken);
}

internal static class SecureRemovalBackend
{
    private static readonly ISecureRemovalBackend? Current = Create();

    internal static bool IsSupported => Current is not null;

    internal static bool TryCaptureIdentity(
        string path,
        out SecureFileIdentity identity,
        out string? error)
    {
        if (Current is null)
        {
            identity = default;
            error = "secure removal is not supported on this operating system";
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
            error = "secure removal is not supported on this operating system";
            return false;
        }
        return Current.TryCanonicalizePath(path, out canonicalPath, out error);
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
        Action<string, bool> entryDeleted,
        Action<string, string> failure,
        CancellationToken cancellationToken) =>
        (Current ?? throw new PlatformNotSupportedException(
            "Secure removal is not supported on this operating system."))
        .RemoveLink(path, entryDeleted, failure, cancellationToken);

    private static ISecureRemovalBackend? Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsSecureRemovalBackend();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new UnixSecureRemovalBackend();
        }
        return null;
    }
}
