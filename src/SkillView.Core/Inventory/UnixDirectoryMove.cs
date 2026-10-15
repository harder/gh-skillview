using System.Runtime.InteropServices;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static NativeDirectoryMoveLease OpenDirectoryMove(string rootPath, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var components = GetRelativeComponents(rootPath, path);
        if (components.Length > PayloadSnapshot.MaxDepth) throw new IOException("Relocation authority depth exceeded.");
        SafeUnixHandle? root = null, parent = null, target = null;
        try
        {
            using var buffer = new StatBuffer();
            root = OpenDirectory(RealPath(rootPath));
            var rootBefore = ReadStat(root, buffer.Pointer);
            parent = OpenRelativeParent(root, components, rootBefore.Device, buffer.Pointer);
            var heldParent = parent ?? root;
            var parentBefore = ReadStat(heldParent, buffer.Pointer);
            target = OpenRootBoundDirectoryAt(heldParent, components[^1], rootBefore.Device, buffer.Pointer, followFinalComponent: false);
            var policy = new UnixSecureRemovalBackend().CaptureDirectoryValidation(target, path, buffer);
            var rootIdentity = CaptureStableRootIdentity(root, rootBefore, buffer);
            var parentPath = ReadFinalPath(heldParent);
            if (!Matches(parentBefore, ReadStat(heldParent, buffer.Pointer))) throw new IOException("Relocation parent changed.");
            if (!PathIdentity.IsInside(policy.Identity.CanonicalPath, rootIdentity.CanonicalPath)
                || !PathIdentity.IsInside(parentPath, rootIdentity.CanonicalPath) && !PathIdentity.Equals(parentPath, rootIdentity.CanonicalPath))
                throw new IOException("Relocation authority escapes its scan root.");
            var lease = new UnixDirectoryMove(root, parent, target, components[^1], rootIdentity,
                ToSecureIdentity(parentBefore, parentPath), policy);
            lease.Revalidate(token);
            root = parent = target = null;
            return lease;
        }
        finally { target?.Dispose(); parent?.Dispose(); root?.Dispose(); }
    }

    private sealed class UnixDirectoryMove(SafeUnixHandle root, SafeUnixHandle? ownedParent, SafeUnixHandle target,
        string originalName, SecureFileIdentity rootIdentity, SecureFileIdentity parentIdentity,
        SecureDirectoryValidationSnapshot policy) : NativeDirectoryMoveLease
    {
        private SafeUnixHandle Parent => ownedParent ?? root;
        internal override SecureFileIdentity RootIdentity => rootIdentity;
        internal override SecureFileIdentity ParentIdentity => parentIdentity;
        internal override SecureFileIdentity TargetIdentity => policy.Identity;
        internal override string CurrentPath => ReadFinalPath(target);
        internal override SecureFileIdentity CurrentRootIdentity => Identity(root);
        internal override SecureFileIdentity CurrentParentIdentity => Identity(Parent);

        private static SecureFileIdentity Identity(SafeUnixHandle handle)
        {
            using var buffer = new StatBuffer();
            return ToSecureIdentity(ReadStat(handle, buffer.Pointer), ReadFinalPath(handle));
        }

        internal override PayloadSnapshot Capture(CancellationToken token) =>
            PayloadSnapshot.CaptureEvidence(CurrentPath,
                (depth, observe, ct) => WalkOpenedPayload(target, Parent, originalName, depth, observe, ct), token);

        internal override PayloadSnapshot FlushPayload(CancellationToken token) =>
            PayloadSnapshot.CaptureEvidence(CurrentPath,
                (depth, observe, ct) => WalkOpenedPayload(target, Parent, originalName, depth, observe, ct, flushToDisk: true), token);

        internal override void Revalidate(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (MutationAttempted) throw new IOException("A relocation attempt must be reconciled before retrying.");
            using var buffer = new StatBuffer();
            if (!PathIdentity.Equals(ReadFinalPath(root), rootIdentity.CanonicalPath)
                || !Matches(rootIdentity, ReadStat(root, buffer.Pointer))) throw new IOException("Relocation scan root changed.");
            if (!PathIdentity.Equals(ReadFinalPath(Parent), parentIdentity.CanonicalPath)
                || !Matches(parentIdentity, ReadStat(Parent, buffer.Pointer))) throw new IOException("Relocation parent changed.");
            if (!PathIdentity.Equals(CurrentPath, policy.Identity.CanonicalPath)
                || !TryReadStatAt(Parent, originalName, buffer.Pointer, out var named, out _)
                || !Matches(policy.Identity, named)) throw new IOException("Relocation selection changed.");
            var current = new UnixSecureRemovalBackend().CaptureDirectoryValidation(target, CurrentPath, buffer);
            if (current != policy) throw new IOException("Relocation directory policy changed.");
            if (!current.HasSkillFile || current.HasGitDirectory)
                throw new IOException("Relocation requires a skill directory without Git metadata.");
            if (ScanRootResolver.FindGitRoot(CurrentPath) is not null)
                throw new IOException("Repository-managed directories require an owner-aware maintenance plan.");
        }

        internal void Move(SafeUnixHandle destination, string name, string destinationPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var buffer = new StatBuffer();
            var destinationBefore = ReadStat(destination, buffer.Pointer);
            if (destinationBefore.Device != TargetIdentity.Volume) throw new IOException("Cross-filesystem relocation is refused.");
            if (!PathIdentity.Equals(ReadFinalPath(destination), destinationPath)) throw new IOException("Relocation destination moved.");
            Revalidate(token);
            if (!Matches(destinationBefore, ReadStat(destination, buffer.Pointer))) throw new IOException("Relocation destination changed.");
            token.ThrowIfCancellationRequested();
            MutationAttempted = true;
            // Never downgrade to ordinary rename, copy, recursive removal or a
            // retry after an ambiguous result. The prepared journal owns recovery.
            var result = OperatingSystem.IsMacOS()
                ? MoveNative.renameatx_np(Parent.FileDescriptor, originalName, destination.FileDescriptor, name, 4)
                : MoveNative.renameat2(Parent.FileDescriptor, originalName, destination.FileDescriptor, name, 1);
            if (result != 0) throw new IOException(LastError("Exclusive directory relocation failed"));
            MutationCompleted = true;
            using var named = OpenDirectoryAt(destination, name, refuseNestedMount: true);
            var actual = ToSecureIdentity(ReadStat(named, buffer.Pointer), ReadFinalPath(named));
            var held = ToSecureIdentity(ReadStat(target, buffer.Pointer), CurrentPath);
            if (!SameObject(TargetIdentity, actual) || !Matches(held, ReadStat(named, buffer.Pointer))
                || !PathIdentity.Equals(actual.CanonicalPath, Path.Combine(destinationPath, name)))
                throw new IOException("Relocated directory identity/location is uncertain; retain it for review.");
        }

        internal override void MoveToSlot(string rootPath, string path, SecureFileIdentity expectedRoot,
            SecureFileIdentity expectedParent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var components = GetRelativeComponents(rootPath, path);
            if (components.Length > PayloadSnapshot.MaxDepth) throw new IOException("Restore authority depth exceeded.");
            using var buffer = new StatBuffer();
            using var destinationRoot = OpenDirectory(RealPath(rootPath));
            var rootBefore = ReadStat(destinationRoot, buffer.Pointer);
            if (!Matches(expectedRoot, rootBefore)
                || !PathIdentity.Equals(ReadFinalPath(destinationRoot), expectedRoot.CanonicalPath))
                throw new IOException("Original scan root changed; review recovery instead of guessing.");
            using var ownedDestination = OpenRelativeParent(destinationRoot, components, rootBefore.Device, buffer.Pointer);
            var destination = ownedDestination ?? destinationRoot;
            var parentPath = ReadFinalPath(destination);
            if (!Matches(expectedParent, ReadStat(destination, buffer.Pointer))
                || !PathIdentity.Equals(parentPath, expectedParent.CanonicalPath)) throw new IOException("Original parent changed.");
            if (!PathIdentity.IsInside(parentPath, expectedRoot.CanonicalPath) && !PathIdentity.Equals(parentPath, expectedRoot.CanonicalPath))
                throw new IOException("Original parent escapes its scan root.");
            if (ScanRootResolver.FindGitRoot(parentPath) is not null) throw new IOException("Original context is now repository-managed.");
            _ = CaptureStableRootIdentity(destinationRoot, rootBefore, buffer);
            Move(destination, components[^1], parentPath, token);
            FlushStateDirectory(destination, token);
        }

        internal override void FlushSourceNamespace(CancellationToken token) => FlushStateDirectory(Parent, token);
        public override void Dispose() { target.Dispose(); ownedParent?.Dispose(); root.Dispose(); }
    }

    private static class MoveNative
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int renameat2(int sourceDirectory, string sourceName, int destinationDirectory, string destinationName, uint flags);
        [DllImport("libc", SetLastError = true)]
        internal static extern int renameatx_np(int sourceDirectory, string sourceName, int destinationDirectory, string destinationName, uint flags);
    }
}
