using System.Runtime.InteropServices;
using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class NativePayloadBorrowedHandleTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skillview-native-borrowed-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temp.Delete(true);
    private static void WriteSkill(string target, string body)
    {
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: demo\ndescription: Fixture\n---\n" + body);
    }

    [Fact]
    public void ReplacingAContainingParentCannotRedirectBoundCapture()
    {
        var parent = Path.Combine(_temp.FullName, "namespace"); var target = Path.Combine(parent, "demo");
        WriteSkill(target, "Original instructions.\n");
        using var lease = NativeDirectoryMoveLease.Open(parent, target, Token);
        var original = lease.Capture(Token); var seen = new List<string>(); PayloadSnapshot? captured = null;
        try { Directory.Move(parent, parent + "-retained"); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && (ex is IOException or UnauthorizedAccessException)
            && (ex.HResult & 0xFFFF) is 5 or 32)
        {
            // Windows can prevent the ancestor rename while the selected
            // directory is held. Verify that stronger boundary before accepting
            // this outcome; it is not a redirected-capture execution claim.
            Assert.True(Directory.Exists(parent));
            Assert.False(Directory.Exists(parent + "-retained"));
            Assert.Equal(original.ObjectStateFingerprint, lease.Capture(Token).ObjectStateFingerprint);
            return;
        }
        WriteSkill(target, "Replacement instructions.\n");
        try
        {
            captured = lease.CaptureInto((item, _) =>
            {
                if (item.Content is not { } content) return;
                using var bytes = new MemoryStream(); content.CopyTo(bytes);
                seen.Add(Encoding.UTF8.GetString(bytes.ToArray()));
            }, null, Token);
        }
        catch (IOException) { /* Native generation/location refusal is also safe. */ }
        Assert.DoesNotContain(seen, value => value.Contains("Replacement instructions", StringComparison.Ordinal));
        if (captured is not null)
        {
            Assert.Contains(seen, value => value.Contains("Original instructions", StringComparison.Ordinal));
            Assert.Equal(original.RootIdentity!.Value.Volume, captured.RootIdentity!.Value.Volume);
            Assert.Equal(original.RootIdentity.Value.FileIdHigh, captured.RootIdentity.Value.FileIdHigh);
            Assert.Equal(original.RootIdentity.Value.FileIdLow, captured.RootIdentity.Value.FileIdLow);
        }
        Assert.Contains("Replacement instructions", File.ReadAllText(Path.Combine(target, "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingEntrySinkReleasesBorrowedChildrenAndKeepsTheLeaseAlive(bool link)
    {
        var target = Path.Combine(_temp.FullName, "demo"); WriteSkill(target, "Original instructions.\n");
        var nested = Path.Combine(target, "nested"); Directory.CreateDirectory(nested);
        if (link) File.CreateSymbolicLink(Path.Combine(nested, "selected"), "missing-target");
        else File.WriteAllText(Path.Combine(nested, "selected"), "Original data.");
        using var lease = NativeDirectoryMoveLease.Open(_temp.FullName, target, Token);
        var original = lease.Capture(Token);
        SafeHandle? root = null; SafeHandle? borrowed = null; SafeHandle? parent = null;
        var reached = false;
        var failure = Assert.Throws<IOException>(() => lease.CaptureInto((item, _) =>
        {
            if (item.RelativePath != "nested/selected") return;
            reached = true; parent = Assert.IsAssignableFrom<SafeHandle>(item.NativeParentHandle);
            Assert.Equal("selected", item.NativeName); Assert.False(parent.IsClosed);
            borrowed = item.NativeHandle;
            if (link)
            {
                Assert.NotEmpty(item.LinkBytes!);
                if (OperatingSystem.IsWindows()) Assert.NotNull(borrowed);
                else Assert.Null(borrowed);
            }
            else Assert.NotNull(borrowed);
            if (borrowed is not null) Assert.False(borrowed.IsClosed);
            throw new IOException("sink rejected captured evidence");
        }, item => root = item.NativeHandle, Token));
        Assert.Equal("sink rejected captured evidence", failure.Message); Assert.True(reached);
        Assert.NotNull(parent); Assert.True(parent.IsClosed);
        if (borrowed is not null) Assert.True(borrowed.IsClosed);
        Assert.NotNull(root); Assert.False(root.IsClosed);
        Assert.Equal(original.ObjectStateFingerprint, lease.Capture(Token).ObjectStateFingerprint);
    }
}
