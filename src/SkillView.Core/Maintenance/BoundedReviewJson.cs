using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SkillView.Maintenance;

internal static class BoundedReviewJson
{
    internal static string Write<T>(Stream file, T value, JsonTypeInfo<T> type, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The bounded stream rejects before growing a large serialization buffer.
        using var bytes = new LimitedMemoryStream(limit);
        JsonSerializer.Serialize(bytes, value, type);
        token.ThrowIfCancellationRequested();
        var content = bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length));
        for (var offset = 0; offset < content.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(64 * 1024, content.Length - offset);
            file.Write(content.Slice(offset, count)); offset += count;
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    internal static (T Value, string Fingerprint) Read<T>(Stream file, JsonTypeInfo<T> type, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var length = file.Length;
        if (length is <= 0 || length > limit) throw new IOException("Saved review JSON exceeds its byte budget or is empty.");
        var bytes = new byte[checked((int)length)];
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(64 * 1024, bytes.Length - offset);
            file.ReadExactly(bytes.AsSpan(offset, count)); offset += count;
        }
        if (file.ReadByte() != -1) throw new IOException("Saved review JSON changed while reading.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!))
                throw new IOException("Saved review JSON has duplicate properties.");
        }
        var value = JsonSerializer.Deserialize(bytes, type) ?? throw new IOException("Saved review JSON is empty.");
        token.ThrowIfCancellationRequested();
        return (value, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count)
        {
            if (count > limit - Position) throw new IOException("Saved review JSON exceeds its byte budget.");
        }
    }
}
