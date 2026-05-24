using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class RemoveConfirmModalTests
{
    [Fact]
    public void CanRunCompact_TrueForSimpleSingleSkillEval()
    {
        var root = NewTempRoot();
        try
        {
            var skill = MakeSkill(root, withGit: false);
            var target = new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.True(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CanRunCompact_FalseWhenValidationBlocks()
    {
        var root = NewTempRoot();
        try
        {
            // A .git directory inside the skill blocks remove (containment check),
            // surfacing as a validation Error.
            var skill = MakeSkill(root, withGit: true);
            var target = new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.False(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CanRunCompact_FalseForAgentSymlinkTarget()
    {
        var root = NewTempRoot();
        try
        {
            var skill = MakeSkill(root, withGit: false);
            // AgentSymlink targets are unlinks, not full removes — they go to
            // the wizard so the user sees what they're detaching from.
            var target = new RemoveTarget(
                RemoveTargetKind.AgentSymlink,
                "Unlink",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.False(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "skillview-remove-confirm-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static InventorySnapshot Snapshot(string root, params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot(root, Scope.User, "claude")),
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private static InstalledSkill MakeSkill(string root, bool withGit)
    {
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: demo\n---\nbody");
        if (withGit)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
        }
        return new()
        {
            Name = "demo",
            ResolvedPath = dir,
            ScanRoot = root,
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = "demo" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        };
    }
}
