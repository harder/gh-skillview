namespace SkillView.Inventory;

/// A new, privately staged payload. This separate surface permits payload names,
/// links and modes without weakening maintenance journal/state-file policy.
/// Callers must attest source metadata separately before approving publication.
internal abstract class NativePayloadDirectory : IDisposable
{
    internal abstract string CanonicalPath { get; }
    internal abstract NativePayloadDirectory CreateDirectory(string name, CancellationToken token);
    internal abstract FileStream CreateFile(string name, CancellationToken token);
    internal abstract void CreateLink(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink, CancellationToken token);
    internal abstract void Seal(int? unixMode, CancellationToken token);
    internal virtual void SealWithMetadata(int? unixMode, SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token,
        SkillView.Maintenance.PayloadMetadataAuthority? authority = null) =>
        throw new IOException("Native payload metadata publication is unavailable.");
    internal virtual void CreateLinkWithMetadata(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, bool directoryLink,
        SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token,
        SkillView.Maintenance.PayloadMetadataAuthority? authority = null) =>
        throw new IOException("Native payload link metadata publication is unavailable.");
    internal abstract SkillView.Maintenance.PayloadSnapshot FlushAndCapture(CancellationToken token, Action<NativePayloadItem>? inspectMetadata = null);
    public abstract void Dispose();

    internal static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new IOException("Invalid payload entry name.");
        if (!OperatingSystem.IsWindows()) return;
        if (name.Any(character => character < 32 || "\\:<>\"|?*".Contains(character))
            || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException("Unsupported Windows payload entry name.");
        var stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9')
            throw new IOException("Reserved Windows payload entry name.");
    }
}
