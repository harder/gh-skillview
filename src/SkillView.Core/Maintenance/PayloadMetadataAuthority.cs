using SkillView.Inventory;

namespace SkillView.Maintenance;

/// A held, private witness for the kernel's trusted-xattr permission check.
/// Capability bits in a nested user namespace are not sufficient evidence.
internal sealed class PayloadMetadataAuthority : IDisposable
{
    private readonly FileStream? witness;
    private PayloadMetadataAuthority(FileStream? witness) => this.witness = witness;
    internal static PayloadMetadataAuthority Open(NativeStateDirectory state, bool create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return new(null);
        try { return new(state.CreateAnonymousMetadataWitness(token)); }
        catch (IOException) { return new(null); } // Capture records unknown coverage, never inferred absence.
    }
    internal bool ProvesStandardLinuxNamespaces(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return OperatingSystem.IsLinux() && witness is not null
            && UnixSecureRemovalBackend.ProbeTrustedMetadataAuthority(witness.SafeFileHandle, token);
    }
    public void Dispose() => witness?.Dispose();
}
