using System.Runtime.InteropServices;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    private sealed class UnixPayloadDirectory(UnixStateSession session) : NativePayloadDirectory
    {
        internal override string CanonicalPath => session.CanonicalPath;
        internal override NativePayloadDirectory CreateDirectory(string name, CancellationToken token) => session.CreatePayloadChild(name, token);
        internal override FileStream CreateFile(string name, CancellationToken token) => session.CreatePayloadFile(name, token);
        internal override void CreateLink(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink, CancellationToken token) =>
            session.CreatePayloadLink(name, rawTarget, unixMode, token);
        internal override void Seal(int? unixMode, CancellationToken token) => session.SealPayload(unixMode, token);
        internal override void SealWithMetadata(int? unixMode, SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token,
            SkillView.Maintenance.PayloadMetadataAuthority? authority = null) => session.SealPayloadWithMetadata(unixMode, metadata, token, authority);
        internal override void CreateLinkWithMetadata(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink,
            SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token,
            SkillView.Maintenance.PayloadMetadataAuthority? authority = null) => session.CreatePayloadLink(name, rawTarget, unixMode, token, metadata, authority);
        internal override SkillView.Maintenance.PayloadSnapshot FlushAndCapture(CancellationToken token, Action<NativePayloadItem>? inspectMetadata = null) => session.CapturePayload(token, inspectMetadata);
        public override void Dispose() => session.Dispose();
    }

    private static class PayloadNative
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int symlinkat(byte[] target, int directory, string name);
    }
}
