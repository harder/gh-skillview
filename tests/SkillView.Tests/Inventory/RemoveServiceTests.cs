using System.Collections.Immutable;
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
        var report = svc.Remove(validation);

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
        var report = svc.Remove(validation, new RemoveService.Options(DryRun: true));

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
        var report = svc.Remove(validation);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(dir));
    }
}
