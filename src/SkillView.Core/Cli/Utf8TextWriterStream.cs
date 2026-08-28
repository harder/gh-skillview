using System.Text;

namespace SkillView.Cli;

/// <summary>
/// Adapts a <see cref="TextWriter"/> to the UTF-8 stream expected by
/// <see cref="System.Text.Json.Utf8JsonWriter"/> without buffering the complete
/// JSON document or closing the process-wide console writer.
/// </summary>
internal sealed class Utf8TextWriterStream(TextWriter writer) : Stream
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _disposed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => writer.Flush();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<char> chars = stackalloc char[1024];
        while (!buffer.IsEmpty)
        {
            _decoder.Convert(
                buffer,
                chars,
                flush: false,
                out var bytesUsed,
                out var charsUsed,
                out _);
            if (charsUsed > 0)
            {
                writer.Write(chars[..charsUsed]);
            }
            buffer = buffer[bytesUsed..];
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Span<char> chars = stackalloc char[2];
            _decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                chars,
                flush: true,
                out _,
                out var charsUsed,
                out _);
            if (charsUsed > 0) writer.Write(chars[..charsUsed]);
            writer.Flush();
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
