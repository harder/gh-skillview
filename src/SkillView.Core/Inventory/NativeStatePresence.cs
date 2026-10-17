namespace SkillView.Inventory;

/// A read-only presence observation, never a writer or a missing-path fallback.
internal static class NativeStatePresence
{
    internal static bool IsMissing(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Any(char.IsControl))
            throw new IOException("Desired-state presence path exceeds its bounded path contract.");
        if (!SecureRemovalBackend.IsSupported) throw new IOException("Native desired-state presence inspection unavailable: " + SecureRemovalBackend.UnsupportedReason);
        // Canonicalization supplies an address only. The native walk below must
        // still prove the first missing child or reject links/access failures.
        var address = OwnershipPath.CanonicalCandidate(path) ?? Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? WindowsSecureRemovalBackend.StateDirectoryIsMissing(address, token)
            : UnixSecureRemovalBackend.StateDirectoryIsMissing(address, token);
    }
}
