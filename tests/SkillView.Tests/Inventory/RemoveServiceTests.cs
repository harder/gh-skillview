using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Inventory;

public class RemoveServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Logger _logger = new();

    public RemoveServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-rsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (InstalledSkill Skill, string Dir) MakeSkill(string name, int extraFiles = 0, int nestedDirs = 0)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "body");
        for (var i = 0; i < extraFiles; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        for (var i = 0; i < nestedDirs; i++)
        {
            var nd = Path.Combine(dir, $"n{i}");
            Directory.CreateDirectory(nd);
            File.WriteAllText(Path.Combine(nd, "inner.txt"), "x");
        }
        return (new InstalledSkill
        {
            Name = name,
            ResolvedPath = dir,
            ScanRoot = _tempRoot,
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = name },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        }, dir);
    }

    private ScanRoot Root() => new(_tempRoot, Scope.User, "claude");

    [Fact]
    public void Remove_HappyPath_DeletesRecursively()
    {
        var (skill, dir) = MakeSkill("rm-me", extraFiles: 2, nestedDirs: 1);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.True(validation.Allowed);

        var svc = new RemoveService(_logger);
        var report = svc.Remove(validation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(dir));
        Assert.True(report.FilesDeleted >= 3);
        Assert.True(report.DirectoriesDeleted >= 1);
    }

    [Fact]
    public void Remove_DryRun_DoesNotTouchDisk()
    {
        var (skill, dir) = MakeSkill("dry", extraFiles: 1);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var svc = new RemoveService(_logger);
        var report = svc.Remove(validation, new RemoveService.Options(DryRun: true),
            TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.True(report.DryRun);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "SKILL.md")));
    }

    [Fact]
    public void Remove_RefusedValidation_ReturnsRefused()
    {
        var (skill, dir) = MakeSkill("bad");
        // Stamp a .git dir to force refusal.
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.False(validation.Allowed);

        var svc = new RemoveService(_logger);
        var report = svc.Remove(validation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void RemoveMany_HappyPath_DeletesEveryValidatedTarget()
    {
        var (first, firstDir) = MakeSkill("group-one", extraFiles: 1);
        var (second, secondDir) = MakeSkill("group-two", extraFiles: 1);
        var firstValidation = RemoveValidator.Validate(first, new[] { Root() }, new[] { first, second });
        var secondValidation = RemoveValidator.Validate(second, new[] { Root() }, new[] { first, second });

        var svc = new RemoveService(_logger);
        var report = svc.RemoveMany([firstValidation, secondValidation],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.TargetsDeleted);
        Assert.False(Directory.Exists(firstDir));
        Assert.False(Directory.Exists(secondDir));
        Assert.True(report.FilesDeleted >= 4);
    }

    [Fact]
    public void RemoveMany_ReturnsPartialSuccess_WhenLaterTargetIsRefused()
    {
        var (first, firstDir) = MakeSkill("group-one", extraFiles: 1);
        var (second, secondDir) = MakeSkill("group-two", extraFiles: 1);
        Directory.CreateDirectory(Path.Combine(secondDir, ".git"));

        var firstValidation = RemoveValidator.Validate(first, new[] { Root() }, new[] { first, second });
        var secondValidation = RemoveValidator.Validate(second, new[] { Root() }, new[] { first, second });

        var svc = new RemoveService(_logger);
        var report = svc.RemoveMany([firstValidation, secondValidation],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.TargetsDeleted);
        Assert.False(Directory.Exists(firstDir));
        Assert.True(Directory.Exists(secondDir));
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void Remove_NestedDirectoryLink_DeletesOnlyLink()
    {
        var (skill, dir) = MakeSkill("directory-link");
        var external = Path.Combine(_tempRoot, "external-directory");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "must-survive.txt");
        File.WriteAllText(externalFile, "keep");
        var link = Path.Combine(dir, "linked-directory");
        if (!TryCreateDirectoryLink(link, external)) return;

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(externalFile));
        Assert.Equal("keep", File.ReadAllText(externalFile));
    }

    [Fact]
    public void Remove_NestedFileLink_DeletesOnlyLink()
    {
        var (skill, dir) = MakeSkill("file-link");
        var externalFile = Path.Combine(_tempRoot, "external-file.txt");
        File.WriteAllText(externalFile, "keep");
        var link = Path.Combine(dir, "linked-file.txt");
        if (!TryCreateFileLink(link, externalFile)) return;

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(externalFile));
        Assert.Equal("keep", File.ReadAllText(externalFile));
    }

    [Fact]
    public void Remove_BrokenNestedLink_DeletesLinkAndTargetDirectory()
    {
        var (skill, dir) = MakeSkill("broken-link");
        var missingTarget = Path.Combine(_tempRoot, "missing-target");
        var link = Path.Combine(dir, "broken");
        if (!TryCreateDirectoryLink(link, missingTarget)) return;

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.False(PathResolver.IsSymlink(link));
    }

    [Fact]
    public void Remove_LinkToAncestor_DoesNotTraverseCycle()
    {
        var (skill, dir) = MakeSkill("ancestor-link");
        var sibling = Path.Combine(_tempRoot, "must-survive");
        Directory.CreateDirectory(sibling);
        var siblingFile = Path.Combine(sibling, "keep.txt");
        File.WriteAllText(siblingFile, "keep");
        var link = Path.Combine(dir, "cycle");
        if (!TryCreateDirectoryLink(link, _tempRoot)) return;

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(siblingFile));
    }

    [Fact]
    public void Remove_LinkRetargetedAfterValidation_DoesNotTouchEitherTarget()
    {
        var (skill, dir) = MakeSkill("retargeted-link");
        var firstTarget = Path.Combine(_tempRoot, "first-target");
        var secondTarget = Path.Combine(_tempRoot, "second-target");
        Directory.CreateDirectory(firstTarget);
        Directory.CreateDirectory(secondTarget);
        var firstFile = Path.Combine(firstTarget, "first.txt");
        var secondFile = Path.Combine(secondTarget, "second.txt");
        File.WriteAllText(firstFile, "first");
        File.WriteAllText(secondFile, "second");
        var link = Path.Combine(dir, "changing-link");
        if (!TryCreateDirectoryLink(link, firstTarget)) return;

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        DeleteDirectoryLink(link);
        if (!TryCreateDirectoryLink(link, secondTarget)) return;

        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.True(File.Exists(firstFile));
        Assert.True(File.Exists(secondFile));
    }

    [Fact]
    public void Remove_AlreadyCanceled_DoesNotTouchDisk()
    {
        var (skill, dir) = MakeSkill("canceled", extraFiles: 2, nestedDirs: 1);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new RemoveService(_logger).Remove(validation, cancellationToken: cancellation.Token));
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "SKILL.md")));
    }

    [Fact]
    public void Remove_WindowsDirectoryJunction_DeletesOnlyJunction()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (skill, dir) = MakeSkill("junction");
        var external = Path.Combine(_tempRoot, "junction-target");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "must-survive.txt");
        File.WriteAllText(externalFile, "keep");
        var junction = Path.Combine(dir, "nested-junction");

        using var process = Process.Start(CreateJunctionStartInfo(junction, external));
        Assert.NotNull(process);
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"mklink failed with exit code {process.ExitCode}: {error}");

        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var report = new RemoveService(_logger).Remove(validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(externalFile));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryLink(string link)
    {
        try
        {
            File.Delete(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(link, recursive: false);
        }
    }

    private static ProcessStartInfo CreateJunctionStartInfo(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        return info;
    }
}
