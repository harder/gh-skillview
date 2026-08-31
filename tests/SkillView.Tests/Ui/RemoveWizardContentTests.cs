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

    [Fact]
    public void BuildReviewMarkdown_AgentLinkOutsideRoots_ExplainsScanRootRecovery()
    {
        var skill = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = "demo" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.CliList,
            Ignored = false,
            IsSymlinked = true,
            InstalledAt = null,
        };
        var target = new RemoveTarget(
            RemoveTargetKind.AgentSymlink,
            "Unlink from claude",
            "Remove only the link.",
            [skill],
            new AgentMembership("claude", "/outside/demo", true));
        var validation = new RemoveValidator.RemoveValidation(
            [new RemoveValidator.Error(
                RemoveValidator.ErrorKind.OutsideKnownRoots,
                "outside")],
            ImmutableArray<RemoveValidator.Warning>.Empty,
            "/outside/demo",
            ImmutableArray<string>.Empty);
        var evaluation = new RemoveTargetEvaluation(
            target,
            [new RemoveTargetItem(skill, validation)]);

        var markdown = RemoveWizardContent.BuildReviewMarkdown(evaluation);

        Assert.Contains("--scan-root", markdown, StringComparison.Ordinal);
        Assert.Contains("agent link", markdown, StringComparison.OrdinalIgnoreCase);

        Assert.True(RemoveScreen.HasSameSafetyContract(evaluation, evaluation));
        var changedValidation = validation with
        {
            Errors = [new RemoveValidator.Error(
                RemoveValidator.ErrorKind.FilesystemIdentityUnavailable,
                "backend unavailable")],
        };
        var changed = evaluation with
        {
            Items = [new RemoveTargetItem(skill, changedValidation)],
        };
        Assert.False(RemoveScreen.HasSameSafetyContract(evaluation, changed));
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
