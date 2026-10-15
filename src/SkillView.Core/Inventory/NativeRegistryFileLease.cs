using System.Security.Cryptography;

namespace SkillView.Inventory;

internal sealed record RegistryFileSnapshot(byte[] Bytes, string ContentHash, SecureFileIdentity Identity);

/// Existing registry authority, including gh's data-file lock. Callers must
/// publish durable before/desired write intent before invoking Write.
internal abstract class NativeRegistryFileLease : IDisposable
{
    private readonly object gate = new();
    private bool disposed;
    protected abstract FileStream File { get; }
    internal abstract string CanonicalPath { get; }
    internal abstract SecureFileIdentity ParentIdentity { get; }
    protected abstract SecureFileIdentity CurrentIdentity(CancellationToken token);
    protected abstract void ValidateNative(CancellationToken token);
    protected abstract void FlushNative(CancellationToken token);
    protected abstract void DisposeNative();
    internal bool MutationAttempted { get; private set; }
    internal int WriteAttempts { get; private set; }

    internal RegistryFileSnapshot Read(CancellationToken token)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return ReadCore(token); }
    }
    internal void ValidateLocation(CancellationToken token)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); ValidateNative(token); }
    }
    private RegistryFileSnapshot ReadCore(CancellationToken token)
    {
        ValidateNative(token);
        var before = CurrentIdentity(token);
        var length = File.Length;
        if (length < 0 || length > SkillLockFileReader.MaxFileBytes) throw new IOException("Registry byte limit exceeded.");
        var bytes = new byte[checked((int)length)];
        File.Position = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = File.Read(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset)));
            if (count == 0) throw new IOException("Registry changed while reading.");
            offset += count;
        }
        if (File.Length != length) throw new IOException("Registry length changed while reading.");
        // Windows may retain ChangeTime during a same-length rewrite while a
        // handle is held. Verify raw bytes again through that same handle.
        File.Position = 0;
        var buffer = new byte[64 * 1024];
        offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = File.Read(buffer, 0, Math.Min(buffer.Length, bytes.Length - offset));
            if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(bytes.AsSpan(offset, count)))
                throw new IOException("Registry bytes changed before capture completed.");
            offset += count;
        }
        ValidateNative(token);
        var after = CurrentIdentity(token);
        if (File.Length != length || before != after) throw new IOException("Registry native evidence changed while reading.");
        return new(bytes, Hash(bytes), after);
    }

    internal RegistryFileSnapshot Write(RegistryFileSnapshot expected, byte[] desired, CancellationToken token, Action<string>? checkpoint = null)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return WriteCore(expected, desired, token, checkpoint); }
    }
    private RegistryFileSnapshot WriteCore(RegistryFileSnapshot expected, byte[] desired, CancellationToken token, Action<string>? checkpoint)
    {
        token.ThrowIfCancellationRequested();
        if (desired.Length > SkillLockFileReader.MaxFileBytes) throw new IOException("Desired registry byte limit exceeded.");
        var frozen = desired.ToArray();
        var current = ReadCore(token);
        if (current.Identity != expected.Identity || current.ContentHash != expected.ContentHash)
            throw new IOException("Registry changed after the reviewed write intent; no write was admitted.");
        File.Position = 0;
        token.ThrowIfCancellationRequested();
        MutationAttempted = true;
        WriteAttempts++;
        File.SetLength(0);
        checkpoint?.Invoke("registry-truncated");
        for (var offset = 0; offset < frozen.Length; offset += 64 * 1024)
        {
            token.ThrowIfCancellationRequested();
            File.Write(frozen.AsSpan(offset, Math.Min(64 * 1024, frozen.Length - offset)));
            checkpoint?.Invoke("registry-chunk-written");
        }
        token.ThrowIfCancellationRequested();
        File.Flush(true);
        FlushNative(token);
        checkpoint?.Invoke("registry-flushed");
        var result = ReadCore(token);
        if (!result.Bytes.AsSpan().SequenceEqual(frozen)) throw new IOException("Registry write verification failed; retained write evidence needs recovery.");
        return result;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            DisposeNative();
        }
    }

    internal static NativeRegistryFileLease Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported) throw new IOException("Native registry authority is unavailable: " + SecureRemovalBackend.UnsupportedReason);
        var full = Path.GetFullPath(path);
        if (Path.GetFileName(full) != SkillLockFileReader.FileName) throw new IOException("Only the gh registry data file can be leased.");
        return OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.OpenRegistryFile(full, token)
            : UnixSecureRemovalBackend.OpenRegistryFile(full, token);
    }
}
