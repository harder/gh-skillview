namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal static FileStream OpenRegularFileForMetadata(string path)
    {
        // An initial no-follow open may request complete metadata rights. When
        // unavailable, preserve inspectable byte evidence and record the exact
        // SACL visibility refusal during capture rather than claiming absence.
        try
        {
            using var privilege = WindowsMetadataPrivilege.Enter();
            var handle = WindowsNative.CreateFileW(ToExtendedPath(path), 0x80000000 | MetadataReadAccess,
                ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw new IOException("Open complete resolution metadata failed: " + LastErrorMessage());
                var identity = ReadIdentity(handle);
                if (identity.IsDirectory || identity.IsReparsePoint || WindowsNative.GetFileType(handle) != 1)
                    throw new IOException("Resolution input is not a regular non-link disk file.");
                return new FileStream(handle, FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }
        catch (IOException) { return OpenRegularFileForSnapshot(path); }
    }

    internal static void CaptureRegularFile(FileStream file, Func<NativePayloadItem, PayloadFileEvidence> observe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handle = file.SafeFileHandle;
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            var before = ReadIdentity(handle);
            if (before.IsDirectory || before.IsReparsePoint || WindowsNative.GetFileType(handle) != 1)
                throw new IOException("Resolution input is not a regular non-link disk file.");
            var canonical = ReadFinalPath(handle);
            var evidence = observe(new("", "file", file, null, null, null,
                ToSecureIdentity(before, canonical), handle));
            token.ThrowIfCancellationRequested();
            file.Position = 0;
            SecurePayloadWalker.VerifyContent(file, evidence, token);
            if (!Matches(before, ReadIdentity(handle)) || ReadFinalPath(handle) != canonical)
                throw new IOException("Resolution input native object or location changed during capture.");
        }
        finally { if (retained) handle.DangerousRelease(); }
    }
}
