using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class RemoveValidatorTests : IDisposable
{
    private readonly string _tempRoot;

    public RemoveValidatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private string MakeSkillDir(string name, bool withSkillMd = true, bool withGit = false)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        if (withSkillMd) File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: " + name + "\n---\nbody");
        if (withGit) Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    private InstalledSkill MakeSkill(string dir, string name, bool symlinked = false) => new()
    {
        Name = name,
        ResolvedPath = dir,
        ScanRoot = _tempRoot,
        Scope = Scope.User,
        Agents = ImmutableArray.Create(new AgentMembership("claude", dir, symlinked)),
        FrontMatter = new SkillFrontMatter { Name = name },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = symlinked,
        InstalledAt = null,
    };

    private ScanRoot Root() => new(_tempRoot, Scope.User, "claude");

    [Fact]
    public void Validate_HappyPath_Allowed()
    {
        var dir = MakeSkillDir("ok");
        var skill = MakeSkill(dir, "ok");
        var v = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.True(v.Allowed);
        Assert.Empty(v.Errors);
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void Validate_OutsideKnownRoots_Refused()
    {
        var dir = MakeSkillDir("ok");
        var skill = MakeSkill(dir, "ok");
        var otherRoot = new ScanRoot(Path.Combine(Path.GetTempPath(), "other-root-" + Guid.NewGuid()), Scope.User, null);
        var v = RemoveValidator.Validate(skill, new[] { otherRoot }, new[] { skill });
        Assert.False(v.Allowed);
        Assert.Contains(v.Errors, e => e.Kind == RemoveValidator.ErrorKind.OutsideKnownRoots);
    }

    [Fact]
    public void Validate_MissingSkillMd_Refused()
    {
        var dir = MakeSkillDir("bare", withSkillMd: false);
        var skill = MakeSkill(dir, "bare");
        var v = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.False(v.Allowed);
        Assert.Contains(v.Errors, e => e.Kind == RemoveValidator.ErrorKind.NotASkillDirectory);
    }

    [Fact]
    public void Validate_DotGitInTarget_Refused()
    {
        var dir = MakeSkillDir("cloned", withGit: true);
        var skill = MakeSkill(dir, "cloned");
        var v = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.False(v.Allowed);
        Assert.Contains(v.Errors, e => e.Kind == RemoveValidator.ErrorKind.ContainsGitDirectory);
    }

    [Fact]
    public void Validate_TargetIsScanRoot_Refused()
    {
        // Setting the scan root equal to the skill dir itself should refuse.
        var dir = MakeSkillDir("root-skill");
        var skill = MakeSkill(dir, "root-skill");
        var root = new ScanRoot(dir, Scope.User, "claude");
        var v = RemoveValidator.Validate(skill, new[] { root }, new[] { skill });
        Assert.False(v.Allowed);
        Assert.Contains(v.Errors, e => e.Kind == RemoveValidator.ErrorKind.TargetIsScanRoot);
    }

    [Fact]
    public void Validate_CanonicalCopyWithIncomingSymlinks_Warns()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation typically requires admin on Windows.
        var canonical = MakeSkillDir("canon");
        var linkDir = Path.Combine(_tempRoot, "incoming-link");
        Directory.CreateSymbolicLink(linkDir, canonical);

        var canonicalSkill = MakeSkill(canonical, "canon", symlinked: false);
        var linkSkill = MakeSkill(linkDir, "canon", symlinked: true) with
        {
            ResolvedPath = canonical,
            IsSymlinked = true,
            Agents = ImmutableArray.Create(new AgentMembership("other", linkDir, true)),
        };

        var v = RemoveValidator.Validate(canonicalSkill, new[] { Root() }, new[] { canonicalSkill, linkSkill });
        Assert.True(v.Allowed);
        Assert.True(v.RequiresSecondConfirm);
        Assert.Contains(v.Warnings, w => w.Kind == RemoveValidator.WarningKind.HasIncomingSymlinks);
        Assert.NotEmpty(v.IncomingSymlinkPaths);
    }
}
