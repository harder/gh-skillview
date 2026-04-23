using System.IO;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public class IgnoreMarkerTests : IDisposable
{
    private readonly string _tempRoot;

    public IgnoreMarkerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesZeroByteMarker()
    {
        var wrote = IgnoreMarker.Write(_tempRoot);
        Assert.True(wrote);
        var marker = IgnoreMarker.MarkerPathFor(_tempRoot);
        Assert.True(File.Exists(marker));
        Assert.Equal(0, new FileInfo(marker).Length);
        Assert.True(IgnoreMarker.Exists(_tempRoot));
    }

    [Fact]
    public void Write_Idempotent_ReturnsFalseWhenAlreadyPresent()
    {
        IgnoreMarker.Write(_tempRoot);
        var second = IgnoreMarker.Write(_tempRoot);
        Assert.False(second);
    }

    [Fact]
    public void Remove_DeletesMarker()
    {
        IgnoreMarker.Write(_tempRoot);
        var removed = IgnoreMarker.Remove(_tempRoot);
        Assert.True(removed);
        Assert.False(IgnoreMarker.Exists(_tempRoot));
    }

    [Fact]
    public void Remove_NonExistentMarker_ReturnsFalse()
    {
        Assert.False(IgnoreMarker.Remove(_tempRoot));
    }

    [Fact]
    public void Write_NonexistentDirectory_Throws()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        Assert.Throws<DirectoryNotFoundException>(() => IgnoreMarker.Write(missing));
    }
}
