namespace SkillView.Inventory;

/// BackupRead requires requests larger than the 24-byte native structure even
/// though its wire header is 20 bytes. At most 24 bytes of unadmitted lookahead
/// stay in fixed scratch space; callers validate framing/budgets before retaining
/// data. This adapter has no native dependencies so framing is tested everywhere.
internal sealed class WindowsBackupReadStream(
    Func<byte[], int, int> readNative, Action<long> seekNative, Action abortNative, CancellationToken token) : Stream
{
    internal const int MinimumNativeRequest = 25;
    internal const int MaximumNativeRequest = 64 * 1024;
    private readonly byte[] scratch = new byte[MaximumNativeRequest];
    private int offset, pending;
    private bool disposed;
    internal int PendingBytes => pending;

    public override int Read(byte[] output, int start, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateBufferArguments(output, start, count);
        token.ThrowIfCancellationRequested();
        var total = 0;
        while (total < count)
        {
            token.ThrowIfCancellationRequested();
            if (pending == 0)
            {
                var requested = Math.Max(MinimumNativeRequest, Math.Min(count - total, MaximumNativeRequest));
                var read = readNative(scratch, requested);
                if (read < 0 || read > requested) throw new IOException("Windows backup read exceeded its bounded native request.");
                offset = 0; pending = read;
                if (pending == 0) break;
            }
            var copied = Math.Min(count - total, pending);
            scratch.AsSpan(offset, copied).CopyTo(output.AsSpan(start + total, copied));
            total += copied; offset += copied; pending -= copied;
        }
        return total;
    }

    internal void Skip(long count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        token.ThrowIfCancellationRequested();
        // The native cursor already includes lookahead. Consume those bytes
        // first, then seek only the remainder of this validated stream body.
        var buffered = (int)Math.Min(count, pending);
        offset += buffered; pending -= buffered; count -= buffered;
        if (count != 0) seekNative(count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        try { if (disposing) abortNative(); }
        finally { base.Dispose(disposing); }
    }
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
}
