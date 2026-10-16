using System.Buffers.Binary;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class WindowsBackupReadStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(25)]
    [InlineData(65536)]
    public void ShortHeadersNamesAndDataRetainFramingAcrossNativeShortReads(int fragment)
    {
        byte[] name = [58, 0];
        var bytes = Header(4, 1, name.Length).Concat(name).Concat(new byte[] { 255 })
            .Concat(Header(4, 0, name.Length)).Concat(name).ToArray();
        var native = new NativeReader(bytes, fragment);
        using var reader = native.Open(TestContext.Current.CancellationToken);
        var header = new byte[20];
        reader.ReadExactly(header);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(1, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)));
        Assert.InRange(reader.PendingBytes, 0, 24);
        var actualName = new byte[2]; reader.ReadExactly(actualName);
        Assert.Equal(name, actualName);
        Assert.Equal(255, reader.ReadByte());
        Assert.InRange(reader.PendingBytes, 0, 24);
        reader.ReadExactly(header);
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)));
        reader.ReadExactly(actualName);
        Assert.Equal(name, actualName);
        Assert.Equal(0, reader.Read(header, 0, header.Length));
        Assert.All(native.Requests, count => Assert.InRange(count, 25, 65536));
        Assert.Empty(native.Seeks);
    }

    [Fact]
    public void SkipConsumesLookaheadBeforeSeekingTheRemainingValidatedBody()
    {
        var native = new NativeReader(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        using var reader = native.Open(TestContext.Current.CancellationToken);
        var header = new byte[20]; reader.ReadExactly(header);
        Assert.Equal(5, reader.PendingBytes);
        reader.Skip(0); reader.Skip(3);
        Assert.Empty(native.Seeks);
        Assert.Equal(2, reader.PendingBytes);
        reader.Skip(10);
        Assert.Equal(new long[] { 8 }, native.Seeks);
        Assert.Equal(33, native.Position);
        Assert.Equal(33, reader.ReadByte());
        Assert.Equal(24, reader.PendingBytes);
        reader.Skip(24);
        Assert.Equal(new long[] { 8 }, native.Seeks);
        Assert.Equal(58, reader.ReadByte());
    }

    [Fact]
    public void LargeAdmittedBodiesSplitNativeRequestsWithoutOverreadingTheLogicalEnd()
    {
        var bytes = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray();
        var native = new NativeReader(bytes);
        using var reader = native.Open(TestContext.Current.CancellationToken);
        var actual = new byte[bytes.Length]; reader.ReadExactly(actual);
        Assert.Equal(bytes, actual);
        Assert.Equal(new[] { 65536, 65536, 18928 }, native.Requests);
        Assert.Equal(0, reader.PendingBytes);
        Assert.Equal(-1, reader.ReadByte());
    }

    [Fact]
    public void TruncatedHeaderReturnsItsActualLengthAndExactReadRejectsEof()
    {
        var native = new NativeReader(new byte[19], 3);
        using var reader = native.Open(TestContext.Current.CancellationToken);
        Assert.Equal(19, reader.Read(new byte[20], 0, 20));
        Assert.Equal(0, reader.Read(new byte[20], 0, 20));
        Assert.Throws<EndOfStreamException>(() => reader.ReadExactly(new byte[1]));
        Assert.All(native.Requests, count => Assert.Equal(25, count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(26)]
    public void ImpossibleNativeReadCountsRefuseWithoutCopyAndAlwaysAbort(int reported)
    {
        var aborts = 0;
        var actual = new byte[] { 42 };
        var reader = new WindowsBackupReadStream((_, count) => { Assert.Equal(25, count); return reported; }, _ => Assert.Fail("Unexpected seek"), () => aborts++, default);
        Assert.Throws<IOException>(() => reader.Read(actual, 0, 1));
        Assert.Equal(new byte[] { 42 }, actual);
        reader.Dispose(); reader.Dispose();
        Assert.Equal(1, aborts);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(actual, 0, 1));
        Assert.Throws<ObjectDisposedException>(() => reader.Skip(1));
    }

    [Fact]
    public void InvalidBoundsAndCancellationDoNotReachNativeIo()
    {
        var native = new NativeReader([1, 2, 3]);
        using (var reader = native.Open(TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, reader.Read([], 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(new byte[1], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(new byte[1], 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Skip(-1));
            Assert.Empty(native.Requests); Assert.Empty(native.Seeks);
        }
        Assert.Equal(1, native.Aborts);
        using var canceled = native.Open(new CancellationToken(true));
        Assert.Throws<OperationCanceledException>(() => canceled.Read(new byte[1], 0, 1));
        Assert.Throws<OperationCanceledException>(() => canceled.Skip(1));
        Assert.Empty(native.Requests); Assert.Empty(native.Seeks);
    }

    [Fact]
    public void NativeFailureStillAbortsItsContext()
    {
        var aborts = 0;
        using (var reader = new WindowsBackupReadStream((_, _) => throw new IOException("native failure"), _ => { }, () => aborts++, default))
            Assert.Throws<IOException>(() => reader.Read(new byte[20], 0, 20));
        Assert.Equal(1, aborts);
    }

    private static byte[] Header(uint id, long size, int nameBytes)
    {
        var result = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(result, id);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), size);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), nameBytes);
        return result;
    }

    private sealed class NativeReader(byte[] bytes, int fragment = int.MaxValue)
    {
        internal int Position;
        internal int Aborts;
        internal List<int> Requests { get; } = [];
        internal List<long> Seeks { get; } = [];
        internal WindowsBackupReadStream Open(CancellationToken token) => new((buffer, count) =>
        {
            Assert.InRange(count, 25, 65536);
            Requests.Add(count);
            var actual = Math.Min(Math.Min(count, fragment), bytes.Length - Position);
            bytes.AsSpan(Position, actual).CopyTo(buffer);
            Position += actual;
            return actual;
        }, count =>
        {
            Assert.InRange(count, 1, bytes.Length - Position);
            Seeks.Add(count); Position += checked((int)count);
        }, () => Aborts++, token);
    }
}
