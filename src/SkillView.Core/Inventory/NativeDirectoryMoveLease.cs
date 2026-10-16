using SkillView.Maintenance;

namespace SkillView.Inventory;

/// A held root/parent/directory authority for a metadata-preserving rename.
/// This is not a path-copy/delete fallback or an archive restore authority.
internal abstract class NativeDirectoryMoveLease : IDisposable
{
    internal abstract SecureFileIdentity RootIdentity { get; }
    internal abstract SecureFileIdentity ParentIdentity { get; }
    internal abstract SecureFileIdentity TargetIdentity { get; }
    internal abstract SecureFileIdentity CurrentRootIdentity { get; }
    internal abstract SecureFileIdentity CurrentParentIdentity { get; }
    internal abstract string CurrentPath { get; }
    internal bool MutationAttempted { get; private protected set; }
    internal bool MutationCompleted { get; private protected set; }
    internal abstract PayloadSnapshot Capture(CancellationToken token);
    internal abstract PayloadSnapshot CaptureInto(Action<NativePayloadItem, PayloadEntry> sink,
        Action<NativePayloadItem>? observeRoot, CancellationToken token);
    internal virtual PayloadSnapshot FlushPayload(CancellationToken token) => Capture(token);
    internal abstract void Revalidate(CancellationToken token);
    internal abstract void FlushSourceNamespace(CancellationToken token);
    internal abstract void MoveToSlot(string rootPath, string path, SecureFileIdentity expectedRoot,
        SecureFileIdentity expectedParent, CancellationToken token);
    public abstract void Dispose();

    internal void MoveToState(NativeStateDirectory destination, string name, CancellationToken token) =>
        destination.ReceiveDirectory(this, name, token);

    internal static NativeDirectoryMoveLease Open(string rootPath, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported)
            throw new IOException("Native directory relocation is unavailable: " + SecureRemovalBackend.UnsupportedReason);
        return OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.OpenDirectoryMove(rootPath, path, token)
            : UnixSecureRemovalBackend.OpenDirectoryMove(rootPath, path, token);
    }

    internal static bool SameObject(SecureFileIdentity left, SecureFileIdentity right) =>
        left.Volume == right.Volume && left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh
        && left.WindowsCreationTime == right.WindowsCreationTime
        && left.IsDirectory == right.IsDirectory && left.IsReparsePoint == right.IsReparsePoint;
}
