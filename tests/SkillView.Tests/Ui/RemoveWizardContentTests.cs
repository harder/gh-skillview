using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class RemoveWizardContentTests
{
    [Fact]
    public void BuildReviewMarkdown_UsesFriendlyBlockedCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-remove-content-" + Guid.NewGuid().ToString("N"));

        try
        {
            var skill = CreateSkill(root, withGit: true);
            var target = new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "Deletes the selected skill install.",
                [skill]);

            var evaluation = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));
            var markdown = RemoveWizardContent.BuildReviewMarkdown(evaluation);

            Assert.Contains("Blocked", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ContainsGitDirectory", markdown);
            Assert.DoesNotContain("HasIncomingSymlinks", markdown);
            Assert.Contains("git clone", markdown, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static InventorySnapshot Snapshot(string root, params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot(root, Scope.User, "claude")),
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private static InstalledSkill CreateSkill(string root, bool withGit) 
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
