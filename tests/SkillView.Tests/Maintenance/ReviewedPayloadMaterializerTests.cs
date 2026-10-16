using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ReviewedPayloadMaterializerTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-reviewed-draft-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    private string Create(string name, string tree, string body, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(temp.FullName, "fixtures", name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\ndescription: Draft fixture\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-path: skills/demo\n  github-ref: refs/heads/main\n  github-tree-sha: " + tree + "\n---\n" + body);
        foreach (var file in files)
        {
            var target = Path.Combine(path, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, file.Content);
        }
        return path;
    }

    [Fact]
    public void NonoverlappingLocalChangesAreBuiltFromIndependentBytesWithSourceTrackingAndPrivateRootMode()
    {
        var b = Create("base", new('a', 40), "Base body.\n", ("tool", "base"), ("deleted", "base"));
        var i = Create("installed", new('a', 40), "Local body.\n", ("tool", "base"), ("extra", "local extra"));
        var c = Create("source", new('b', 40), "Base body.\n", ("tool", "incoming"), ("deleted", "base"));
        Directory.CreateDirectory(Path.Combine(c, "new-empty"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(i, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var installed = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(i)!, i, Token);
        using var source = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(c)!, c, Token);
        using var baseline = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(b)!, b, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [i, b, c], Token);
        var local = CapturedPayloadArchive.Capture(state, "installed-evidence", installed, Token);
        var incoming = CapturedPayloadArchive.Capture(state, "source-evidence", source, Token);
        var before = installed.Capture(Token);
        var review = ThreeWayPayloadReview.Analyze(baseline.CaptureInto((_, _) => { }, null, Token), local.Snapshot, incoming.Snapshot);
        using var localFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [i, b, c], Token, allowCreate: false);
        using var sourceFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "source-evidence"), [i, b, c], Token, allowCreate: false);
        var result = ReviewedPayloadMaterializer.Stage(state, "merged", review, new Dictionary<string, CapturedPayloadArchive.Reader>
        {
            ["installed"] = local.OpenReader(localFiles),
            ["source"] = incoming.OpenReader(sourceFiles)
        }, incoming.Snapshot.FrontMatter!, Token);
        Assert.Contains("Local body.", File.ReadAllText(Path.Combine(result.Path, "SKILL.md")));
        Assert.Equal(new string('b', 40), result.FrontMatter!.GithubTreeSha);
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(result.Path, "tool")));
        Assert.Equal("local extra", File.ReadAllText(Path.Combine(result.Path, "extra")));
        Assert.False(File.Exists(Path.Combine(result.Path, "deleted")));
        Assert.True(Directory.Exists(Path.Combine(result.Path, "new-empty")));
        Assert.Equal(local.Snapshot.RootUnixMode, result.RootUnixMode);
        Assert.Equal(before.Fingerprint, installed.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, installed.Capture(Token).ObjectStateFingerprint);
    }

    [Theory]
    [InlineData("installed", "local")]
    [InlineData("source", "incoming")]
    [InlineData("resolved", "incoming")]
    public void ConflictsNeedAnExplicitCapturedChoiceBeforeAnyDraftIsCreated(string side, string expected)
    {
        var b = Create("base", new('a', 40), "Body.", ("conflict", "base"));
        var i = Create("installed", new('a', 40), "Body.", ("conflict", "local"));
        var c = Create("source", new('b', 40), "Body.", ("conflict", "incoming"));
        using var installed = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(i)!, i, Token);
        using var source = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(c)!, c, Token);
        using var baseline = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(b)!, b, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [i, b, c], Token);
        var local = CapturedPayloadArchive.Capture(state, "installed-evidence", installed, Token);
        var incoming = CapturedPayloadArchive.Capture(state, "source-evidence", source, Token);
        var recorded = baseline.CaptureInto((_, _) => { }, null, Token);
        var unresolved = ThreeWayPayloadReview.Analyze(recorded, local.Snapshot, incoming.Snapshot);
        Assert.Throws<IOException>(() => ReviewedPayloadMaterializer.Stage(state, "not-created", unresolved,
            new Dictionary<string, CapturedPayloadArchive.Reader>(), incoming.Snapshot.FrontMatter!, Token));
        Assert.False(Directory.Exists(Path.Combine(state.CanonicalPath, "not-created")));
        using var localFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [i, b, c], Token, allowCreate: false);
        using var sourceFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "source-evidence"), [i, b, c], Token, allowCreate: false);
        var review = ThreeWayPayloadReview.Analyze(recorded, local.Snapshot, incoming.Snapshot, [new("conflict", side)],
            resolvedFiles: side == "resolved" ? [incoming.Snapshot.Entries.Single(entry => entry.Path == "conflict")] : null);
        var result = ReviewedPayloadMaterializer.Stage(state, "merged", review, new Dictionary<string, CapturedPayloadArchive.Reader>
        {
            ["installed"] = local.OpenReader(localFiles),
            ["source"] = incoming.OpenReader(sourceFiles),
            ["resolved"] = incoming.OpenReader(sourceFiles)
        }, incoming.Snapshot.FrontMatter!, Token);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(result.Path, "conflict")));
    }

    [Fact]
    public void RawLinkModeCaptureDoesNotChangeLegacyBackupAndReplacementHashInputs()
    {
        var i = Create("installed", new('a', 40), "Body.");
        File.CreateSymbolicLink(Path.Combine(i, "dangling"), "absent");
        using var installed = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(i)!, i, Token);
        var legacy = installed.Capture(Token);
        var backup = PayloadSnapshot.CaptureWithEntrySink(i, (_, _) => { }, Token);
        var draft = installed.CaptureInto((_, _) => { }, null, Token);
        Assert.Equal(legacy.Fingerprint, backup.Fingerprint);
        Assert.Equal(legacy.ObjectStateFingerprint, backup.ObjectStateFingerprint);
        Assert.Null(legacy.Entries.Single(entry => entry.Path == "dangling").UnixMode);
        var projected = draft.Entries.Select(entry => entry.Kind == "link" ? entry with { UnixMode = null } : entry).ToImmutableArray();
        Assert.Equal(legacy.Fingerprint, PayloadSnapshot.Digest(projected, false));
        if (!OperatingSystem.IsWindows()) Assert.InRange(draft.Entries.Single(entry => entry.Path == "dangling").UnixMode!.Value, 0, 511);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingTheCompletedDraftCannotRedirectItsFinalCapture(bool replacementLink)
    {
        var i = Create("installed", new('a', 40), "Body.", ("tool", "original"));
        using var installed = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(i)!, i, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [i], Token);
        var local = CapturedPayloadArchive.Capture(state, "installed-evidence", installed, Token);
        var before = installed.Capture(Token);
        using var localFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [i], Token, allowCreate: false);
        var review = ThreeWayPayloadReview.Analyze(local.Snapshot, local.Snapshot, local.Snapshot);
        Assert.Throws<IOException>(() => ReviewedPayloadMaterializer.Stage(state, "merged", review,
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = local.OpenReader(localFiles) },
            local.Snapshot.FrontMatter!, Token, path =>
            {
                Directory.Move(path, path + "-held");
                if (replacementLink) Directory.CreateSymbolicLink(path, i);
                else
                {
                    Directory.CreateDirectory(path);
                    foreach (var file in Directory.EnumerateFiles(path + "-held")) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)local.Snapshot.RootUnixMode!.Value);
                }
            }));
        Assert.Equal(before.Fingerprint, installed.Capture(Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, installed.Capture(Token).ObjectStateFingerprint);
    }

    [Fact]
    public void UnixDraftRetainsLinkBytesAndItsObservedModeWithoutTouchingTheTarget()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link metadata admission is still explicit unfinished work.
        var i = Create("installed", new('a', 40), "Body.");
        var outsideDirectory = Path.Combine(temp.FullName, "outside"); Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "target"); File.WriteAllText(outsideFile, "untouched");
        File.SetUnixFileMode(outsideFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var outsideBefore = PayloadSnapshot.CaptureEvidence(outsideDirectory, Token);
        File.CreateSymbolicLink(Path.Combine(i, "external"), outsideFile);
        using var installed = NativeDirectoryMoveLease.Open(Path.GetDirectoryName(i)!, i, Token);
        using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "state"), [i], Token);
        var local = CapturedPayloadArchive.Capture(state, "installed-evidence", installed, Token);
        using var localFiles = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, "installed-evidence"), [i], Token, allowCreate: false);
        var result = ReviewedPayloadMaterializer.Stage(state, "merged", ThreeWayPayloadReview.Analyze(local.Snapshot, local.Snapshot, local.Snapshot),
            new Dictionary<string, CapturedPayloadArchive.Reader> { ["installed"] = local.OpenReader(localFiles) }, local.Snapshot.FrontMatter!, Token);
        Assert.Equal(local.Snapshot.Entries.Single(entry => entry.Path == "external"), result.Entries.Single(entry => entry.Path == "external"));
        Assert.Equal("untouched", File.ReadAllText(outsideFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outsideFile));
        Assert.Equal(outsideBefore.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(outsideDirectory, Token).ObjectStateFingerprint);
    }
}
