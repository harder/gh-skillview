using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class WindowsSecurityDescriptorQueryTests
{
    private const int BufferTooSmall = unchecked((int)0xC0000023);

    [Theory]
    [InlineData(0u)]
    [InlineData(19u)]
    [InlineData((uint)PayloadNativeMetadata.MaxEntryMetadataBytes + 1)]
    [InlineData(uint.MaxValue)]
    public void InvalidNativeSizesRefuseBeforeAllocatingAReadBuffer(uint needed)
    {
        var calls = 0;
        var error = Assert.Throws<IOException>(() => WindowsSecurityDescriptorQuery.Read((buffer, length) =>
        {
            calls++;
            Assert.Null(buffer); Assert.Equal(0u, length);
            return (BufferTooSmall, needed);
        }));
        Assert.Equal(1, calls);
        Assert.Contains($"length={needed}", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0xC0000022))]
    [InlineData(unchecked((int)0x80000005))]
    public void UnexpectedSizingStatusCannotAuthorizeASecondQuery(int status)
    {
        var calls = 0;
        Assert.Throws<IOException>(() => WindowsSecurityDescriptorQuery.Read((buffer, length) =>
        {
            calls++; Assert.Null(buffer); Assert.Equal(0u, length);
            return (status, 164u);
        }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(164)]
    [InlineData(208)]
    [InlineData(PayloadNativeMetadata.MaxEntryMetadataBytes)]
    public void SuccessfulNativeTransportRetainsExactlyTheBoundedReturnedBytes(int size)
    {
        var expected = Enumerable.Range(0, size).Select(index => (byte)(index * 17)).ToArray();
        byte[]? nativeBuffer = null;
        var calls = 0;
        var actual = WindowsSecurityDescriptorQuery.Read((buffer, length) =>
        {
            if (++calls == 1)
            {
                Assert.Null(buffer); Assert.Equal(0u, length);
                return (BufferTooSmall, (uint)size);
            }
            Assert.NotNull(buffer); Assert.Equal((uint)size, length); Assert.Equal(size, buffer.Length);
            expected.CopyTo(buffer, 0); nativeBuffer = buffer;
            return (0, (uint)size);
        });
        Assert.Equal(2, calls);
        Assert.Same(nativeBuffer, actual);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0xC0000022))]
    [InlineData(BufferTooSmall)]
    public void FailedOrInformationalReadStatusNeverReturnsPartialBytesOrRetries(int status)
    {
        var calls = 0;
        Assert.Throws<IOException>(() => WindowsSecurityDescriptorQuery.Read((buffer, _) =>
        {
            if (++calls == 1) return (BufferTooSmall, 164u);
            Assert.NotNull(buffer); buffer[0] = 1;
            return (status, 164u);
        }));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(163u)]
    [InlineData(165u)]
    [InlineData(uint.MaxValue)]
    public void ChangedNativeReadLengthRefusesWithoutGrowingOrRetrying(uint actual)
    {
        var calls = 0;
        var error = Assert.Throws<IOException>(() => WindowsSecurityDescriptorQuery.Read((_, _) =>
            ++calls == 1 ? (BufferTooSmall, 164u) : (0, actual)));
        Assert.Equal(2, calls);
        Assert.Contains($"expected=164, actual={actual}", error.Message);
    }
}
