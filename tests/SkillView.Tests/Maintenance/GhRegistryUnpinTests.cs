using System.Text;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhRegistryUnpinTests
{
    [Theory]
    [InlineData("\"pinnedRef\" : \"v1\", ")]
    [InlineData("\"pinnedRef\" : \"v1\", ", true)]
    [InlineData(", \"pinnedRef\" : \"v1\"", false, true)]
    public void OnlyPinMemberAndOneCommaAreRemoved(string pin, bool middle = false, bool last = false)
    {
        var first = "\"sourceType\" : \"github\"";
        var tree = "\"skillFolderHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
        var content = last ? first + ", " + tree + pin : middle ? first + ", " + pin + tree : pin + first + ", " + tree;
        var bytes = Encoding.UTF8.GetBytes("{ \"version\":3, \"skills\": { \"demo\": { " + content + " }, \"other\": {\"extension\":[ 1, 2 ]} }, \"keep\":true }\n");
        var captured = GhRegistryEditor.CaptureEntry(bytes, "demo", TestContext.Current.CancellationToken);
        var patch = GhRegistryEditor.PrepareUnpin(bytes, "demo", captured.Fingerprint, TestContext.Current.CancellationToken);
        var expected = last ? Encoding.UTF8.GetString(bytes).Replace(pin, "") : Encoding.UTF8.GetString(bytes).Replace(pin.TrimEnd(), "");
        Assert.Equal(expected, Encoding.UTF8.GetString(patch.DesiredBytes));
        var after = GhRegistryEditor.CaptureEntry(patch.DesiredBytes, "demo", TestContext.Current.CancellationToken);
        Assert.Equal(patch.DesiredBytes, GhRegistryEditor.PrepareUnpin(patch.DesiredBytes, "demo", after.Fingerprint, TestContext.Current.CancellationToken).DesiredBytes);
        Assert.Throws<IOException>(() => GhRegistryEditor.PrepareUnpin(bytes, "demo", new('0', 64), TestContext.Current.CancellationToken));
    }
}
