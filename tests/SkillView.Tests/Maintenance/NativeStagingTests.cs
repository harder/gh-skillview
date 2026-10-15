using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class NativeStagingTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-native-stage-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);

    [Fact]
    public void HeldStagingAllowsSkillAndNestedFilesButNeverOverwritesAnEntry()
    {
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "operation"), [], Token);
        using var writer = operation.OpenFile("writer.lock", true, Token);
        using var stage = operation.CreateStagingDirectory("candidate", Token);
        using (var skill = stage.OpenFile("SKILL.md", false, Token))
        { skill.Write("---\nname: demo\ndescription: Fixture\n---\nBody\n"u8); skill.Flush(true); }
        Assert.Throws<IOException>(() => stage.OpenFile("SKILL.md", false, Token));
        using (var child = stage.CreateStagingDirectory("scripts", Token))
        {
            using (var file = child.OpenFile("tool", false, Token)) { file.Write("keep"u8); file.Flush(true); }
            child.SealStagingDirectory(493, Token);
        }
        stage.SealStagingDirectory(493, Token);
        operation.FlushPublication(Token);
        Assert.Throws<IOException>(() => stage.OpenFile("later", false, Token));
        var snapshot = PayloadSnapshot.Capture(stage.CanonicalPath, Token);
        Assert.Equal(3, snapshot.Entries.Length);
        Assert.Contains(snapshot.Entries, entry => entry.Path == "scripts/tool");
    }

    [Fact]
    public void RenamedOperationCannotRedirectAStageFileWrite()
    {
        var path = Path.Combine(temporary.FullName, "operation");
        using var operation = NativeStateDirectory.OpenOrCreate(path, [], Token);
        using var writer = operation.OpenFile("writer.lock", true, Token);
        using var stage = operation.CreateStagingDirectory("candidate", Token);
        Directory.Move(path, path + "-moved");
        Directory.CreateDirectory(Path.Combine(path, "candidate"));
        File.WriteAllText(Path.Combine(path, "candidate", "SKILL.md"), "unrelated keep");
        Assert.Throws<IOException>(() => stage.OpenFile("SKILL.md", false, Token));
        Assert.Equal("unrelated keep", File.ReadAllText(Path.Combine(path, "candidate", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(path + "-moved", "candidate", "SKILL.md")));
    }

    [Fact]
    public void StageLinkCannotRedirectFileOrDirectoryCreation()
    {
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "operation"), [], Token);
        using var writer = operation.OpenFile("writer.lock", true, Token);
        using var stage = operation.CreateStagingDirectory("candidate", Token);
        var outside = Directory.CreateDirectory(Path.Combine(temporary.FullName, "outside")).FullName;
        var original = Path.Combine(outside, "keep"); File.WriteAllText(original, "unchanged");
        File.CreateSymbolicLink(Path.Combine(stage.CanonicalPath, "file"), original);
        Directory.CreateSymbolicLink(Path.Combine(stage.CanonicalPath, "folder"), outside);
        Assert.Throws<IOException>(() => stage.OpenFile("file", false, Token));
        Assert.Throws<IOException>(() => stage.CreateStagingDirectory("folder", Token));
        Assert.Equal("unchanged", File.ReadAllText(original));
        Assert.Equal(["keep"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
    }
    [Fact]
    public async Task VerifiedTreeMaterializesPrefixSharingSiblingsWithoutClosingTheirDirectoryEarly()
    {
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "operation"), [], Token);
        using var writer = operation.OpenFile("writer.lock", true, Token);
        using var stage = operation.CreateStagingDirectory("candidate", Token);
        var blobs = new Dictionary<string, byte[]>();
        string Blob(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var sha = GhRecordedTreeStager.GitObjectHash("blob", bytes);
            blobs[sha] = bytes; return sha;
        }
        var skill = Blob("---\nname: demo\ndescription: Fixture\n---\nBody\n");
        var rootFile = Blob("root-file"); var tool = Blob("executable-resource");
        var bin = Tree(("100755", "tool", tool));
        var expected = Tree(("100644", "SKILL.md", skill), ("100644", "bin.txt", rootFile), ("40000", "bin", bin));
        using var tree = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { path = "bin", type = "tree", mode = "040000", sha = bin, size = 0 },
            new { path = "bin.txt", type = "blob", mode = "100644", sha = rootFile, size = blobs[rootFile].Length },
            new { path = "bin/tool", type = "blob", mode = "100755", sha = tool, size = blobs[tool].Length },
            new { path = "SKILL.md", type = "blob", mode = "100644", sha = skill, size = blobs[skill].Length }
        }));
        await GhRecordedTreeStager.MaterializeAsync(tree.RootElement, expected, stage,
            (sha, _) => Task.FromResult(blobs[sha]), null, Token);
        stage.SealStagingDirectory(493, Token); operation.FlushPublication(Token);
        Assert.Equal("root-file", File.ReadAllText(Path.Combine(stage.CanonicalPath, "bin.txt")));
        Assert.Equal("executable-resource", File.ReadAllText(Path.Combine(stage.CanonicalPath, "bin", "tool")));
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)493, File.GetUnixFileMode(Path.Combine(stage.CanonicalPath, "bin", "tool")));
    }

    [Fact]
    public async Task SameLengthWrongBlobBytesAreRefusedBeforeNativeFileCreation()
    {
        using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(temporary.FullName, "operation"), [], Token);
        using var writer = operation.OpenFile("writer.lock", true, Token);
        using var stage = operation.CreateStagingDirectory("candidate", Token);
        var bytes = Encoding.UTF8.GetBytes("safe-data");
        var sha = GhRecordedTreeStager.GitObjectHash("blob", bytes);
        var expected = Tree(("100644", "SKILL.md", sha));
        using var tree = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        { new { path = "SKILL.md", type = "blob", mode = "100644", sha, size = bytes.Length } }));
        await Assert.ThrowsAsync<IOException>(() => GhRecordedTreeStager.MaterializeAsync(tree.RootElement, expected, stage,
            (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("evil-data")), null, Token));
        Assert.False(File.Exists(Path.Combine(stage.CanonicalPath, "SKILL.md")));
    }

    private static string Tree(params (string Mode, string Name, string Sha)[] entries)
    {
        using var bytes = new MemoryStream();
        foreach (var entry in entries.OrderBy(entry => entry.Name + (entry.Mode == "40000" ? "/" : ""), StringComparer.Ordinal))
        { bytes.Write(Encoding.UTF8.GetBytes(entry.Mode + " " + entry.Name + "\0")); bytes.Write(Convert.FromHexString(entry.Sha)); }
        return GhRecordedTreeStager.GitObjectHash("tree", bytes.ToArray());
    }

}
