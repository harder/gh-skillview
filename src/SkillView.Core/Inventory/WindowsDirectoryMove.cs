using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal static NativeDirectoryMoveLease OpenDirectoryMove(string rootPath, string path, CancellationToken token, bool registryDamaged = false)
    {
        token.ThrowIfCancellationRequested();
        var components = GetRelativeComponents(rootPath, path);
        if (components.Length > PayloadSnapshot.MaxDepth) throw new IOException("Relocation authority depth exceeded.");
        SafeFileHandle? root = null, parent = null, target = null;
        try
        {
            root = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var rootBefore = ReadIdentity(root);
            parent = OpenRelativeParent(root, components);
            var heldParent = parent ?? root;
            var parentBefore = ReadIdentity(heldParent);
            target = OpenEntryAt(heldParent, components[^1], directory: true, enumerateDirectory: true, requestDeleteAccess: true);
            var policy = new WindowsSecureRemovalBackend().CaptureDirectoryValidation(target, path, token);
            var rootIdentity = CaptureStableRootIdentity(root, rootBefore);
            var parentPath = ReadFinalPath(heldParent);
            if (!Matches(parentBefore, ReadIdentity(heldParent))) throw new IOException("Relocation parent changed.");
            if (parentBefore.Volume != rootBefore.Volume || policy.Identity.Volume != rootBefore.Volume
                || !PathIdentity.IsInside(policy.Identity.CanonicalPath, rootIdentity.CanonicalPath)
                || !PathIdentity.IsInside(parentPath, rootIdentity.CanonicalPath) && !PathIdentity.Equals(parentPath, rootIdentity.CanonicalPath))
                throw new IOException("Relocation authority escapes its scan root or volume.");
            var lease = new WindowsDirectoryMove(root, parent, target, components[^1], rootIdentity,
                ToSecureIdentity(parentBefore, parentPath), policy, registryDamaged);
            lease.Revalidate(token);
            root = parent = target = null;
            return lease;
        }
        finally { target?.Dispose(); parent?.Dispose(); root?.Dispose(); }
    }

    private sealed class WindowsDirectoryMove(SafeFileHandle root, SafeFileHandle? ownedParent, SafeFileHandle target,
        string originalName, SecureFileIdentity rootIdentity, SecureFileIdentity parentIdentity,
        SecureDirectoryValidationSnapshot policy, bool registryDamaged) : NativeDirectoryMoveLease
    {
        private SafeFileHandle Parent => ownedParent ?? root;
        internal override SecureFileIdentity RootIdentity => rootIdentity;
        internal override SecureFileIdentity ParentIdentity => parentIdentity;
        internal override SecureFileIdentity TargetIdentity => policy.Identity;
        internal override string CurrentPath => ReadFinalPath(target);
        internal override SecureFileIdentity CurrentRootIdentity => ToSecureIdentity(ReadIdentity(root), ReadFinalPath(root));
        internal override SecureFileIdentity CurrentParentIdentity => ToSecureIdentity(ReadIdentity(Parent), ReadFinalPath(Parent));
        internal override PayloadSnapshot Capture(CancellationToken token) =>
            PayloadSnapshot.CaptureEvidence(CurrentPath, (depth, observe, ct) => WalkOpenedPayload(target, depth, observe, ct), token);

        internal override PayloadSnapshot CaptureInto(Action<NativePayloadItem, PayloadEntry> sink,
            Action<NativePayloadItem>? observeRoot, CancellationToken token) =>
            PayloadSnapshot.CaptureBoundWithEntrySink(CurrentPath, sink,
                (depth, observe, ct) => WalkOpenedPayload(target, depth, observe, ct,
                    observeRoot is null ? null : item => observeRoot(item with { NativeParentHandle = Parent, NativeName = originalName })), token);

        internal override PayloadSnapshot CaptureRawInto(Action<NativePayloadItem, PayloadEntry> sink,
            Action<NativePayloadItem>? observeRoot, CancellationToken token) =>
            PayloadSnapshot.CaptureRawBoundWithEntrySink(CurrentPath, sink,
                (depth, observe, ct) => WalkOpenedPayload(target, depth, observe, ct,
                    observeRoot is null ? null : item => observeRoot(item with { NativeParentHandle = Parent, NativeName = originalName })), token);

        internal override void Revalidate(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (MutationAttempted) throw new IOException("A relocation attempt must be reconciled before retrying.");
            if (!PathIdentity.Equals(ReadFinalPath(root), rootIdentity.CanonicalPath)
                || !Matches(rootIdentity, ReadIdentity(root))) throw new IOException("Relocation scan root changed.");
            if (!PathIdentity.Equals(ReadFinalPath(Parent), parentIdentity.CanonicalPath)
                || !Matches(parentIdentity, ReadIdentity(Parent))) throw new IOException("Relocation parent changed.");
            if (!PathIdentity.Equals(CurrentPath, policy.Identity.CanonicalPath)) throw new IOException("Relocation selection moved.");
            using var named = OpenEntryAt(Parent, originalName, directory: true, enumerateDirectory: false, requestDeleteAccess: false);
            if (!Matches(policy.Identity, ReadIdentity(named))) throw new IOException("Relocation selection changed.");
            var current = new WindowsSecureRemovalBackend().CaptureDirectoryValidation(target, CurrentPath, token);
            if (current != policy) throw new IOException("Relocation directory policy changed.");
            if ((!registryDamaged && !current.HasSkillFile) || current.HasGitDirectory)
                throw new IOException("Relocation requires a skill directory without Git metadata.");
            if (ScanRootResolver.FindGitRoot(CurrentPath) is not null)
                throw new IOException("Repository-managed directories require an owner-aware maintenance plan.");
        }

        internal void Move(SafeFileHandle destination, string name, string destinationPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateRelativeEntryName(name);
            var destinationBefore = ReadIdentity(destination);
            if (destinationBefore.Volume != TargetIdentity.Volume) throw new IOException("Cross-volume relocation is refused.");
            if (!PathIdentity.Equals(ReadFinalPath(destination), destinationPath)) throw new IOException("Relocation destination moved.");
            var filename = Encoding.Unicode.GetBytes(name);
            var size = checked(20 + filename.Length);
            var information = Marshal.AllocHGlobal(size);
            try
            {
                // FILE_RENAME_INFORMATION's BOOLEAN is one byte; RootDirectory
                // starts at8 and FileName at20 on supported64-bit processes.
                Marshal.Copy(new byte[size], 0, information, size);
                Marshal.WriteIntPtr(information, 8, destination.DangerousGetHandle());
                Marshal.WriteInt32(information, 16, filename.Length);
                Marshal.Copy(filename, 0, information + 20, filename.Length);
                Revalidate(token);
                if (!Matches(destinationBefore, ReadIdentity(destination))) throw new IOException("Relocation destination changed.");
                token.ThrowIfCancellationRequested();
                MutationAttempted = true;
                var status = MoveNative.NtSetInformationFile(target, out _, information, checked((uint)size), 10);
                if (status != 0) throw new IOException("Exclusive directory relocation failed: "
                    + new Win32Exception(unchecked((int)WindowsNative.RtlNtStatusToDosError(status))).Message);
                MutationCompleted = true;
                using var named = OpenEntryAt(destination, name, directory: true, enumerateDirectory: false, requestDeleteAccess: false);
                var actual = ToSecureIdentity(ReadIdentity(named), ReadFinalPath(named));
                if (!SameObject(TargetIdentity, actual) || !Matches(ReadIdentity(target), ReadIdentity(named))
                    || !PathIdentity.Equals(actual.CanonicalPath, Path.Combine(destinationPath, name)))
                    throw new IOException("Relocated directory identity/location is uncertain; retain it for review.");
            }
            finally { Marshal.FreeHGlobal(information); }
        }

        internal override void MoveToSlot(string rootPath, string path, SecureFileIdentity expectedRoot,
            SecureFileIdentity expectedParent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var components = GetRelativeComponents(rootPath, path);
            if (components.Length > PayloadSnapshot.MaxDepth) throw new IOException("Restore authority depth exceeded.");
            using var destinationRoot = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var rootBefore = ReadIdentity(destinationRoot);
            if (!Matches(expectedRoot, rootBefore)
                || !PathIdentity.Equals(ReadFinalPath(destinationRoot), expectedRoot.CanonicalPath))
                throw new IOException("Original scan root changed; review recovery instead of guessing.");
            using var ownedDestination = OpenRelativeParent(destinationRoot, components);
            var destination = ownedDestination ?? destinationRoot;
            var parentPath = ReadFinalPath(destination);
            if (!Matches(expectedParent, ReadIdentity(destination))
                || !PathIdentity.Equals(parentPath, expectedParent.CanonicalPath)) throw new IOException("Original parent changed.");
            if (!PathIdentity.IsInside(parentPath, expectedRoot.CanonicalPath) && !PathIdentity.Equals(parentPath, expectedRoot.CanonicalPath))
                throw new IOException("Original parent escapes its scan root.");
            if (ReadIdentity(destination).Volume != rootBefore.Volume) throw new IOException("Original parent is on another volume.");
            if (ScanRootResolver.FindGitRoot(parentPath) is not null) throw new IOException("Original context is now repository-managed.");
            _ = CaptureStableRootIdentity(destinationRoot, rootBefore);
            Move(destination, components[^1], parentPath, token);
        }

        // Prepared journals are flushed before rename and final journals after
        // verification. Windows rename namespace durability is not separately
        // attested; restart reconciliation must inspect both locations.
        internal override void FlushSourceNamespace(CancellationToken token) => token.ThrowIfCancellationRequested();
        public override void Dispose() { target.Dispose(); ownedParent?.Dispose(); root.Dispose(); }
    }

    private static class MoveNative
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status,
            IntPtr information, uint length, int informationClass);
    }
}
