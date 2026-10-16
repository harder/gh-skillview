using System.Runtime.Versioning;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    [SupportedOSPlatform("windows")]
    private sealed class WindowsPayloadDirectory(WindowsStateSession session) : NativePayloadDirectory
    {
        internal override string CanonicalPath => session.CanonicalPath;
        internal override NativePayloadDirectory CreateDirectory(string name, CancellationToken token) => session.CreatePayloadChild(name, token);
        internal override FileStream CreateFile(string name, CancellationToken token) => session.CreatePayloadFile(name, token);
        internal override void CreateLink(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink, CancellationToken token)
        {
            if (unixMode is not null) throw new IOException("A Windows link cannot carry a Unix mode.");
            session.CreatePayloadLink(name, rawTarget, directoryLink, token);
        }
        internal override void Seal(int? unixMode, CancellationToken token) => session.SealPayload(unixMode, token);
        internal override void SealWithMetadata(int? unixMode, SkillView.Maintenance.PayloadNativeMetadata metadata,
            CancellationToken token, SkillView.Maintenance.PayloadMetadataAuthority? authority = null) =>
            session.SealPayloadWithMetadata(unixMode, metadata, token);
        internal override void CreateLinkWithMetadata(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink,
            SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token, SkillView.Maintenance.PayloadMetadataAuthority? authority = null)
        {
            if (unixMode is not null) throw new IOException("A Windows link cannot carry a Unix mode.");
            session.CreatePayloadLink(name, rawTarget, directoryLink, token, metadata);
        }
        internal override SkillView.Maintenance.PayloadSnapshot FlushAndCapture(CancellationToken token, Action<NativePayloadItem>? inspectMetadata = null) => session.CapturePayload(token, inspectMetadata);
        public override void Dispose() => session.Dispose();
    }
}
