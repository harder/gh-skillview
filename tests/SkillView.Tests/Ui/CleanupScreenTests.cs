using System.Collections.Immutable;
using System.Reflection;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class CleanupScreenTests
{
    [Fact]
    public void DoRemove_RemovesEmptyDirectoryCandidateInsideScanRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var emptyDir = Path.Combine(root, "empty-dir");
        Directory.CreateDirectory(emptyDir);

        try
        {
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                emptyDir,
                "empty directory under scan root",
                Skill: null);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.False(Directory.Exists(emptyDir));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(" removed 1, skipped/failed 0", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DoRemove_RemovesBrokenSymlinkCandidateInsideScanRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingTarget = Path.Combine(root, "missing-target");
        var brokenLink = Path.Combine(root, "broken-link");
        Directory.CreateSymbolicLink(brokenLink, missingTarget);

        try
        {
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                brokenLink,
                "broken symlink",
                Skill: null);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.False(File.Exists(brokenLink) || Directory.Exists(brokenLink) || PathResolver.IsSymlink(brokenLink));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(" removed 1, skipped/failed 0", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DoRemove_RemovesBrokenSymlinkCandidateWhenCandidateCarriesSkill()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingTarget = Path.Combine(root, "missing-target");
        var brokenLink = Path.Combine(root, "broken-link");
        Directory.CreateSymbolicLink(brokenLink, missingTarget);

        try
        {
            var skill = Skill("broken-link", brokenLink) with
            {
                Validity = ValidityState.BrokenSymlink,
            };
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                brokenLink,
                "broken symlink",
                Skill: skill);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)], [skill]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.False(File.Exists(brokenLink) || Directory.Exists(brokenLink) || PathResolver.IsSymlink(brokenLink));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(" removed 1, skipped/failed 0", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DoRemove_SkipsEmptyDirectoryCandidateWhenDirectoryBecomesNonEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var emptyDir = Path.Combine(root, "empty-dir");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "added.txt"), "now non-empty");

        try
        {
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                emptyDir,
                "empty directory under scan root",
                Skill: null);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.True(Directory.Exists(emptyDir));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(" removed 0, skipped/failed 1", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DoRemove_SkipsBrokenSymlinkCandidateWhenPathBecomesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingTarget = Path.Combine(root, "missing-target");
        var brokenLink = Path.Combine(root, "broken-link");
        Directory.CreateSymbolicLink(brokenLink, missingTarget);
        File.Delete(brokenLink);
        Directory.CreateDirectory(brokenLink);
        File.WriteAllText(Path.Combine(brokenLink, "added.txt"), "real directory now");

        try
        {
            var skill = Skill("broken-link", brokenLink) with
            {
                Validity = ValidityState.BrokenSymlink,
            };
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                brokenLink,
                "broken symlink",
                Skill: skill);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)], [skill]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.True(Directory.Exists(brokenLink));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(" removed 0, skipped/failed 1", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DoRemove_SkipsEmptyDirectoryCandidateWhenPathBecomesSymlink()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "empty-dir");
        Directory.CreateDirectory(target);
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(target);
        Directory.CreateSymbolicLink(target, elsewhere);

        try
        {
            var candidate = new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                target,
                "empty directory under scan root",
                Skill: null);
            var screen = CreateScreen([candidate], [new ScanRoot(root, Scope.User, null)]);
            var status = new Label();

            InvokeDoRemove(screen, [0], status);

            Assert.True(PathResolver.IsSymlink(target));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(" removed 0, skipped/failed 1", status.Text.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RenderDetail_UsesStructuredMarkdownSections()
    {
        var candidate = new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.Duplicate,
            "/skills/demo`copy",
            "duplicate install",
            Skill("demo", "/skills/demo"));

        var detail = CleanupScreen.RenderDetail(candidate);

        Assert.Contains("## Candidate", detail);
        Assert.Contains("| Field | Value |", detail);
        Assert.Contains("| Kind | **Duplicate** |", detail);
        Assert.Contains("| Path | `` /skills/demo`copy `` |", detail);
        Assert.Contains("| Reason | duplicate install |", detail);
        Assert.Contains("## Installed skill", detail);
        Assert.Contains("## Summary", detail);
    }

    [Fact]
    public void BuildRemoveConfirmationText_SummarizesKindsAndPaths()
    {
        var candidates = ImmutableArray.Create(
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.Duplicate,
                "/tmp/a",
                "duplicate install",
                Skill: null),
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.Duplicate,
                "/tmp/b",
                "duplicate install",
                Skill: null),
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                "/tmp/c",
                "broken link",
                Skill: null));

        var text = CleanupScreen.BuildRemoveConfirmationText(candidates);

        Assert.Contains("Remove 3 cleanup candidate(s)?", text);
        Assert.Contains("- duplicate: 2", text);
        Assert.Contains("- broken-link: 1", text);
        Assert.Contains("/tmp/a", text);
        Assert.Contains("/tmp/c", text);
    }

    [Fact]
    public void BuildRemoveConfirmationText_ShowsFirstFewPathsOnly()
    {
        var candidates = Enumerable.Range(0, 5)
            .Select(i => new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                $"/tmp/{i}",
                "empty",
                Skill: null))
            .ToImmutableArray();

        var text = CleanupScreen.BuildRemoveConfirmationText(candidates);

        Assert.Contains("/tmp/0", text);
        Assert.Contains("/tmp/1", text);
        Assert.Contains("/tmp/2", text);
        Assert.DoesNotContain("/tmp/3", text);
        Assert.Contains("…and 2 more", text);
    }

    private static InstalledSkill Skill(string name, string resolvedPath) => new()
    {
        Name = name,
        ResolvedPath = resolvedPath,
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = new SkillFrontMatter
        {
            Name = name,
            Description = $"{name} description",
        },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    private static CleanupScreen CreateScreen(
        ImmutableArray<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill>? allSkills = null) =>
        new(
            app: null!,
            remove: new RemoveService(new Logger()),
            logger: new Logger(),
            candidates,
            scanRoots,
            allSkills: allSkills ?? Array.Empty<InstalledSkill>(),
            confirmBatchRemoval: _ => 1);

    private static void InvokeDoRemove(CleanupScreen screen, IEnumerable<int> checkedRows, Label status)
    {
        var method = typeof(CleanupScreen).GetMethod("DoRemove", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(screen, [new HashSet<int>(checkedRows), status]);
    }
}
