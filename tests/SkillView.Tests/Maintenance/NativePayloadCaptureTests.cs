using System.Runtime.InteropServices;
using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class NativePayloadCaptureTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skillview-native-capture-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temp.Delete(true);
    private string Target => Path.Combine(_temp.FullName, "demo");
    private void WriteSkill(string directory, string body)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"),
            "---\nname: demo\ndescription: Capture fixture\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-path: skills/demo\n---\n" + body);
    }

    [Fact]
    public void BoundSinkReceivesOriginalBytesAndStrictAuthoredEvidenceThroughBorrowedHandles()
    {
        WriteSkill(Target, "Original body.\n");
        Directory.CreateDirectory(Path.Combine(Target, "empty"));
        var expected = PayloadSnapshot.Capture(Target, Token);
        var files = new Dictionary<string, byte[]>();
        var borrowedFiles = new List<SafeHandle>(); SafeHandle? borrowedRoot = null;
        using (var lease = NativeDirectoryMoveLease.Open(_temp.FullName, Target, Token))
        {
            var observed = lease.CaptureInto((item, entry) =>
            {
                Assert.NotNull(item.NativeHandle); Assert.False(item.NativeHandle.IsClosed);
                Assert.Equal(entry.Path, item.RelativePath);
                if (item.Content is not { } content) return;
                using var copied = new MemoryStream(); content.CopyTo(copied);
                files.Add(entry.Path, copied.ToArray()); borrowedFiles.Add(item.NativeHandle);
            }, root =>
            {
                Assert.Equal("", root.RelativePath); Assert.Equal("directory", root.Kind);
                borrowedRoot = Assert.IsAssignableFrom<SafeHandle>(root.NativeHandle);
                Assert.False(borrowedRoot.IsClosed);
            }, Token);
            Assert.Equal(expected.Fingerprint, observed.Fingerprint);
            Assert.Equal(expected.ComparisonFingerprint, observed.ComparisonFingerprint);
            Assert.NotNull(observed.FrontMatter);
            Assert.Equal(expected.FrontMatter!.Name, observed.FrontMatter.Name);
            Assert.Equal(expected.FrontMatter.Upstream, observed.FrontMatter.Upstream);
            Assert.Equal(expected.FrontMatter.GithubPath, observed.FrontMatter.GithubPath);
            Assert.NotEqual(observed.Fingerprint, observed.ComparisonFingerprint);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Target, "SKILL.md")), files["SKILL.md"]);
            Assert.NotNull(borrowedRoot); Assert.False(borrowedRoot.IsClosed);
            Assert.All(borrowedFiles, handle => Assert.True(handle.IsClosed));
            Assert.Equal(expected.ObjectStateFingerprint, lease.Capture(Token).ObjectStateFingerprint);
        }
        Assert.True(borrowedRoot!.IsClosed);
    }

    [Fact]
    public void RenamingTheSelectionCannotMakeTheSinkReadItsReplacement()
    {
        WriteSkill(Target, "Original body.\n");
        using var lease = NativeDirectoryMoveLease.Open(_temp.FullName, Target, Token);
        var original = lease.Capture(Token);
        Directory.Move(Target, Target + "-retained"); WriteSkill(Target, "Replacement body.\n");
        var reads = new List<string>(); PayloadSnapshot? captured = null;
        try
        {
            captured = lease.CaptureInto((item, _) =>
            {
                if (item.Content is not { } content) return;
                using var bytes = new MemoryStream(); content.CopyTo(bytes);
                reads.Add(Encoding.UTF8.GetString(bytes.ToArray()));
            }, null, Token);
        }
        catch (IOException) { /* Explicit generation/name-change refusal is safe. */ }
        Assert.DoesNotContain(reads, body => body.Contains("Replacement body", StringComparison.Ordinal));
        if (captured is not null)
        {
            Assert.Contains(reads, body => body.Contains("Original body", StringComparison.Ordinal));
            Assert.Equal(original.RootIdentity!.Value.FileIdLow, captured.RootIdentity!.Value.FileIdLow);
            Assert.Equal(original.RootIdentity.Value.FileIdHigh, captured.RootIdentity.Value.FileIdHigh);
            Assert.Equal(original.RootIdentity.Value.Volume, captured.RootIdentity.Value.Volume);
        }
        Assert.Contains("Replacement body", File.ReadAllText(Path.Combine(Target, "SKILL.md")));
    }

    [Fact]
    public void RootObserverCancellationLeavesTheOwningLeaseUsable()
    {
        WriteSkill(Target, "Original body.\n"); using var lease = NativeDirectoryMoveLease.Open(_temp.FullName, Target, Token);
        using var cancellation = new CancellationTokenSource(); var observedRoot = false;
        Assert.Throws<OperationCanceledException>(() => lease.CaptureInto((_, _) => Assert.Fail("Canceled capture read an entry."),
            _ => { observedRoot = true; cancellation.Cancel(); }, cancellation.Token));
        Assert.True(observedRoot); Assert.NotEmpty(lease.Capture(Token).Entries);
    }
}
