using System.Text;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class NativeStateChildDirectoryTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-held-state-child-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(temp.FullName, "state");

    private NativeStateDirectory Create(string root, string value)
    {
        var parent = NativeStateDirectory.OpenOrCreate(root, [], Token);
        try
        {
            using var child = parent.CreateStagingDirectory("capture", Token);
            using var file = child.OpenFile("evidence.json", false, Token);
            file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }

    private string Read(NativeStateDirectory child)
    {
        using var file = child.OpenExistingFile("evidence.json", false, Token);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ExistingChildHasActualNativeAuthorityAndNeverCreatesMissingEntries()
    {
        using var parent = Create(Root, "original");
        using var child = parent.OpenExistingDirectory("capture", Token);
        using var observed = child.CaptureOwnership(Token);
        using var expected = OwnedDirectoryLease.Open(Path.Combine(Root, "capture"));
        Assert.Equal(expected.Capture(Token).Identity, observed.Capture(Token).Identity);
        Assert.Equal("original", Read(child));
        Assert.ThrowsAny<IOException>(() => parent.OpenExistingDirectory("missing", Token));
        Assert.False(Directory.Exists(Path.Combine(Root, "missing")));
        Assert.Throws<IOException>(() => parent.OpenExistingDirectory("../capture", Token));
        using var wrong = parent.OpenFile("regular", false, Token);
        Assert.ThrowsAny<IOException>(() => parent.OpenExistingDirectory("regular", Token));
    }

    [Fact]
    public void ReplacedParentCannotRedirectAnExistingChildOpenToTheSameCanonicalAddress()
    {
        using var parent = Create(Root, "original");
        var retained = Root + "-original";
        Directory.Move(Root, retained);
        try
        {
            using var substitute = Create(Root, "foreign");
            Assert.Throws<IOException>(() => parent.OpenExistingDirectory("capture", Token));
            using var foreign = substitute.OpenExistingDirectory("capture", Token);
            Assert.Equal("foreign", Read(foreign));
        }
        finally
        {
            Directory.Move(Root, Root + "-foreign");
            Directory.Move(retained, Root);
        }
        using var restored = parent.OpenExistingDirectory("capture", Token);
        Assert.Equal("original", Read(restored));
    }

    [Fact]
    public void HeldChildRefusesParentSubstitutionAndReadsOriginalAfterParentAba()
    {
        using var parent = Create(Root, "original");
        using var child = parent.OpenExistingDirectory("capture", Token);
        var retained = Root + "-original";
        Directory.Move(Root, retained);
        try
        {
            using var substitute = Create(Root, "foreign");
            Assert.Throws<IOException>(() => Read(child));
            Assert.Throws<IOException>(() => child.CaptureOwnership(Token));
        }
        finally
        {
            Directory.Move(Root, Root + "-foreign");
            Directory.Move(retained, Root);
        }
        Assert.Equal("original", Read(child));
    }

    [Fact]
    public void HeldChildRefusesNamedChildSubstitutionAndReadsOriginalAfterChildAba()
    {
        using var parent = Create(Root, "original");
        using var child = parent.OpenExistingDirectory("capture", Token);
        var path = Path.Combine(Root, "capture"); var retained = Path.Combine(Root, "retained");
        Directory.Move(path, retained);
        try
        {
            using var substitute = parent.CreateStagingDirectory("capture", Token);
            using (var file = substitute.OpenFile("evidence.json", false, Token))
            { file.Write(Encoding.UTF8.GetBytes("foreign")); file.Flush(true); }
            Assert.Throws<IOException>(() => Read(child));
        }
        finally
        {
            Directory.Move(path, Path.Combine(Root, "foreign"));
            Directory.Move(retained, path);
        }
        Assert.Equal("original", Read(child));
    }

    [Fact]
    public void CanceledChildAdmissionDoesNotCreateAnEntry()
    {
        using var parent = Create(Root, "original");
        Assert.Throws<OperationCanceledException>(() => parent.OpenExistingDirectory("missing", new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(Root, "missing")));
    }

    [Fact]
    public void ChildAdmissionNeverFollowsADirectoryLink()
    {
        using var parent = Create(Root, "original");
        try { Directory.CreateSymbolicLink(Path.Combine(Root, "linked"), Path.Combine(Root, "capture")); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Assert.Skip("Windows directory-link fixture requires symlink creation privilege."); throw; }
        Assert.ThrowsAny<IOException>(() => parent.OpenExistingDirectory("linked", Token));
        using var actual = parent.OpenExistingDirectory("capture", Token);
        Assert.Equal("original", Read(actual));
    }

    public void Dispose() => temp.Delete(true);
}
