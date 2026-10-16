namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static void CaptureRegularFile(FileStream file, Func<NativePayloadItem, PayloadFileEvidence> observe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handle = file.SafeFileHandle;
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            using var native = new SafeUnixHandle(handle.DangerousGetHandle().ToInt32(), ownsHandle: false);
            using var stat = new StatBuffer();
            var before = ReadStat(native, stat.Pointer);
            if (before.FileType != 0x8000) throw new IOException("Resolution input is not a regular file.");
            var canonical = ReadFinalPath(native);
            var evidence = observe(new("", "file", file, (int)(before.Mode & 0xFFF), null, null,
                ToSecureIdentity(before, canonical), native));
            token.ThrowIfCancellationRequested();
            file.Position = 0;
            SecurePayloadWalker.VerifyContent(file, evidence, token);
            if (!Matches(before, ReadStat(native, stat.Pointer)) || ReadFinalPath(native) != canonical)
                throw new IOException("Resolution input native object, mode or location changed during capture.");
        }
        finally { if (retained) handle.DangerousRelease(); }
    }
}
