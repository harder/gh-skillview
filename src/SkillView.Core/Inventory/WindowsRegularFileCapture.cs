namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
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
