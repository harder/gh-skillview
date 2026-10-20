using System.Collections.Immutable;
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
    public async Task BatchReviewAndCanceledResultRetainAllCapturedContexts()
    {
        var skill = Skill("demo", Path.Combine(Path.GetTempPath(), "skillview-cleanup-subject", "demo")) with
        {
            Agents = [new("codex", "/fixture/shared", false, Scope.User, Context: "context-one", Profile: "one"),
            new("claude", "/fixture/shared", false, Scope.Project, Context: "context-two", Profile: "two")]
        };
        var impact = OperationImpact.Payload(OperationSubject.From(skill), "cleanup");
        var candidate = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.NameCollision, skill.ResolvedPath, "review", skill, true)
        { Impact = impact };
        var text = CleanupScreen.BuildRemoveConfirmationText([candidate], showDetails: true);
        Assert.Contains(impact.Subject.InstallationId, text);
        Assert.Contains("context-one", text); Assert.Contains("context-two", text);
        var screen = CreateScreen([candidate], []);
        var summary = await screen.RemoveSelectedAsync([0], TestContext.Current.CancellationToken);
        Assert.Same(impact, Assert.Single(summary.Impacts));
        Assert.Same(impact, Assert.Single(screen.LastReport!.Impacts));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RemoveSelectedAsync([0], cancel.Token));
        Assert.True(screen.LastReport!.IsCanceled);
        Assert.Same(impact, Assert.Single(screen.LastReport.Impacts));
    }

    [Fact]
    public async Task RemoveSelectedAsync_ReviewOnlyCollisionCannotDeleteValidPayload()
    {
        var root = Directory.CreateTempSubdirectory("skillview-review-only-");
        var dir = Path.Combine(root.FullName, "demo");
        Directory.CreateDirectory(dir);
        var content = "---\nname: demo\ndescription: preserve\n---\nlocal content";
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        try
        {
            var skill = Skill("demo", dir);
            var screen = CreateScreen([new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.NameCollision,
                dir, "comparison required", skill, ReviewOnly: true)], [new ScanRoot(root.FullName, Scope.Custom, null)], [skill]);
            var result = await screen.RemoveSelectedAsync([0], TestContext.Current.CancellationToken);
            Assert.Equal(0, result.Removed);
            Assert.Equal(content, File.ReadAllText(Path.Combine(dir, "SKILL.md")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void CountFailedSelections_DeduplicatedTarget_IsSkippedNotFailed()
    {
        var report = new RemoveService.BatchRemoveReport(
            Succeeded: true,
            TargetsDeleted: 1,
            FilesDeleted: 1,
            DirectoriesDeleted: 1,
            Errors: ImmutableArray<string>.Empty,
            DryRun: false)
        {
            TargetsSkipped = 1,
        };

        Assert.Equal(0, CleanupScreen.CountFailedSelections(2, report));
    }

    [Fact]
    public void CountFailedSelections_CanceledDeduplicatedTarget_RemainsSkipped()
    {
        Assert.Equal(1, CleanupScreen.CountFailedSelections(
            selectedCount: 3,
            removedCount: 1,
            skippedCount: 1));
    }

    [Fact]
    public async Task RemoveSelectedAsync_RemovesEmptyDirectoryCandidateInsideScanRoot()
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(emptyDir));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(0, screen.RemovedFileCount);
            Assert.Equal(1, screen.RemovedDirectoryCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(1, 0, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_RemovesBrokenSymlinkCandidateInsideScanRoot()
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.False(File.Exists(brokenLink) || Directory.Exists(brokenLink) || PathResolver.IsSymlink(brokenLink));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(1, screen.RemovedFileCount);
            Assert.Equal(0, screen.RemovedDirectoryCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(1, 0, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_RemovesSiblingBrokenLinksWithFreshParentIdentities()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "broken-first");
        var second = Path.Combine(root, "broken-second");
        Directory.CreateSymbolicLink(first, Path.Combine(root, "missing-first"));
        Directory.CreateSymbolicLink(second, Path.Combine(root, "missing-second"));

        try
        {
            var candidates = new[] { first, second }
                .Select(path => new CleanupClassifier.Candidate(
                    CleanupClassifier.CandidateKind.BrokenSymlink,
                    path,
                    "broken symlink",
                    Skill: null))
                .ToImmutableArray();
            var screen = CreateScreen(
                candidates,
                [new ScanRoot(root, Scope.User, null)]);

            var summary = await screen.RemoveSelectedAsync(
                [0, 1], TestContext.Current.CancellationToken);

            Assert.False(PathResolver.IsSymlink(first));
            Assert.False(PathResolver.IsSymlink(second));
            Assert.Equal(2, screen.RemovedCount);
            Assert.Equal(
                new CleanupScreen.RemovalSummary(2, 0, Confirmed: true),
                summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_DuplicateCandidatePath_IsSkippedBeforeValidation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "duplicate-candidate");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "body");

        try
        {
            var skill = Skill("duplicate-candidate", directory) with
            {
                ScanRoot = root,
            };
            var candidates = ImmutableArray.Create(
                new CleanupClassifier.Candidate(
                    CleanupClassifier.CandidateKind.SourceOrphaned,
                    directory,
                    "source orphaned",
                    skill),
                new CleanupClassifier.Candidate(
                    CleanupClassifier.CandidateKind.Duplicate,
                    directory,
                    "duplicate install",
                    skill));
            var screen = CreateScreen(
                candidates,
                [new ScanRoot(root, Scope.User, null)],
                [skill]);

            var summary = await screen.RemoveSelectedAsync(
                [0, 1], TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(directory));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(
                new CleanupScreen.RemovalSummary(1, 0, Confirmed: true)
                {
                    Skipped = 1,
                },
                summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveSelectedAsync_CancellationPublishesDuplicateSkipCount(bool supplyState)
    {
        if (!SecureRemovalBackend.IsSupported) return;

        var root = Path.Combine(
            Path.GetTempPath(),
            "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "cancel-duplicate");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "body");
        File.WriteAllText(Path.Combine(directory, "payload.txt"), "payload");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        try
        {
            var skill = Skill("cancel-duplicate", directory) with
            {
                ScanRoot = root,
            };
            var candidates = ImmutableArray.Create(
                new CleanupClassifier.Candidate(
                    CleanupClassifier.CandidateKind.SourceOrphaned,
                    directory,
                    "source orphaned",
                    skill),
                new CleanupClassifier.Candidate(
                    CleanupClassifier.CandidateKind.Duplicate,
                    directory,
                    "duplicate install",
                    skill));
            var remove = new RemoveService(
                new Logger(),
                entryObservedForTests: null,
                entryDeletingForTests: (_, _) => cancellation.Cancel());
            var screen = CreateScreen(
                candidates,
                [new ScanRoot(root, Scope.User, null)],
                [skill],
                remove);
            var attemptState = new CleanupScreen.RemovalAttemptState();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                screen.RemoveSelectedAsync(
                    [0, 1],
                    cancellation.Token,
                    progress: null,
                    attemptState: supplyState ? attemptState : null));

            Assert.Equal(1, screen.LastReport!.TargetsSkipped);
            Assert.True(screen.LastReport.IsCanceled);
            Assert.Equal(0, screen.RemovedCount);
            if (supplyState) Assert.Equal(1, attemptState.PreValidationSkipped);
            Assert.Equal(1, CleanupScreen.CountFailedSelections(
                selectedCount: 2,
                removedCount: 0,
                skippedCount: screen.LastReport.TargetsSkipped));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedPartialRemovalPreservesSkipsMutationTotalsAndRetry(bool cancel, bool supplyState)
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var root = Directory.CreateTempSubdirectory("skillview-cleanup-partial-");
        var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "demo")).FullName;
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "body");
        File.WriteAllText(Path.Combine(directory, "extra"), "preserve until retry");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var skill = Skill("demo", directory) with { ScanRoot = root.FullName };
            var candidates = ImmutableArray.Create(
                new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.SourceOrphaned, directory, "fixture", skill),
                new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.Duplicate, directory, "fixture duplicate", skill));
            var deleting = 0;
            var remove = new RemoveService(new Logger(), null, (_, _) =>
            {
                if (++deleting != 2) return;
                if (cancel) cancellation.Cancel();
                else throw new InvalidOperationException("fixture unexpected removal failure");
            });
            var screen = CreateScreen(candidates, [new(root.FullName, Scope.Custom, null)], [skill], remove);
            var state = supplyState ? new CleanupScreen.RemovalAttemptState() : null;
            if (cancel)
                await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RemoveSelectedAsync([0, 1], cancellation.Token, attemptState: state));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => screen.RemoveSelectedAsync([0, 1], cancellation.Token, attemptState: state));
            Assert.Equal(1, screen.LastReport!.TargetsSkipped);
            Assert.Equal(cancel, screen.LastReport.IsCanceled);
            Assert.Equal(cancel, screen.CancellationObserved);
            Assert.Equal(cancel ? 0 : 1, screen.LastReport.ErrorCount);
            Assert.Equal(1, screen.LastReport.FilesDeleted);
            Assert.Equal(1, screen.RemovedFileCount);
            Assert.Equal(0, screen.RemovedDirectoryCount);
            Assert.Equal(0, screen.RemovedCount);
            Assert.Single(Directory.EnumerateFiles(directory));
            // A declined/empty attempt cannot carry a prior known skip or add
            // that attempt's mutations a second time.
            await screen.RemoveSelectedAsync([], TestContext.Current.CancellationToken, attemptState: state);
            Assert.Null(screen.LastReport);
            if (state is not null) Assert.Equal(0, state.PreValidationSkipped);
            Assert.Equal(1, screen.RemovedFileCount);
            // Native directory enumeration does not promise a file order. If
            // the interrupted attempt removed SKILL.md first, a fresh removal
            // review must refuse the now-malformed directory. Repair this
            // fixture explicitly before retrying; never relax that validation.
            var metadata = Path.Combine(directory, "SKILL.md");
            var repairMetadata = !File.Exists(metadata);
            if (repairMetadata)
            {
                var refused = await screen.RemoveSelectedAsync([0, 1], TestContext.Current.CancellationToken, attemptState: state);
                Assert.Equal(0, refused.Removed);
                Assert.Equal(1, refused.Skipped);
                Assert.Equal(1, refused.Failed);
                Assert.Equal(1, screen.RemovedFileCount);
                Assert.Single(Directory.EnumerateFiles(directory));
                File.WriteAllText(metadata, "body");
            }
            var retry = await screen.RemoveSelectedAsync([0, 1], TestContext.Current.CancellationToken, attemptState: state);
            Assert.Equal(1, retry.Removed); Assert.Equal(1, retry.Skipped);
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(repairMetadata ? 3 : 2, screen.RemovedFileCount);
            Assert.Equal(1, screen.RemovedDirectoryCount);
            Assert.False(Directory.Exists(directory));
            Assert.Equal(cancel, screen.CancellationObserved); // Later success cannot erase cancellation.
            using var laterCancellation = new CancellationTokenSource();
            laterCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => screen.RemoveSelectedAsync([0, 1], laterCancellation.Token));
            Assert.True(screen.CancellationObserved); // Cancellation after success is retained too.
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnexpectedSecondTargetFailureRetainsCompletedTargetTotals()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var root = Directory.CreateTempSubdirectory("skillview-cleanup-completed-");
        var first = Directory.CreateDirectory(Path.Combine(root.FullName, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root.FullName, "second")).FullName;
        File.WriteAllText(Path.Combine(first, "SKILL.md"), "body");
        File.WriteAllText(Path.Combine(second, "SKILL.md"), "body");
        try
        {
            var one = Skill("first", first) with { ScanRoot = root.FullName };
            var two = Skill("second", second) with { ScanRoot = root.FullName };
            var candidates = ImmutableArray.Create(
                new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.SourceOrphaned, first, "fixture", one),
                new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.SourceOrphaned, second, "fixture", two));
            var canonicalSecond = OwnershipPath.CanonicalCandidate(second)!;
            var remove = new RemoveService(new Logger(), null, (path, _) =>
            {
                if (PathIdentity.IsInside(path, canonicalSecond)) throw new InvalidOperationException("fixture second target failure");
            });
            var screen = CreateScreen(candidates, [new(root.FullName, Scope.Custom, null)], [one, two], remove);
            await Assert.ThrowsAsync<InvalidOperationException>(() => screen.RemoveSelectedAsync([0, 1], TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(first)); Assert.True(Directory.Exists(second));
            Assert.Equal(1, screen.LastReport!.TargetsDeleted);
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(1, screen.RemovedFileCount);
            Assert.Equal(1, screen.RemovedDirectoryCount);
            Assert.Equal(1, screen.LastReport.ErrorCount);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RemoveSelectedAsync_RemovesBrokenSymlinkCandidateWhenCandidateCarriesSkill()
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.False(File.Exists(brokenLink) || Directory.Exists(brokenLink) || PathResolver.IsSymlink(brokenLink));
            Assert.Equal(1, screen.RemovedCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(1, 0, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_SkipsEmptyDirectoryCandidateWhenDirectoryBecomesNonEmpty()
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(emptyDir));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(0, 1, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_SkipsBrokenSymlinkCandidateWhenPathBecomesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillview-cleanup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingTarget = Path.Combine(root, "missing-target");
        var brokenLink = Path.Combine(root, "broken-link");
        Directory.CreateSymbolicLink(brokenLink, missingTarget);
        DeletePath(brokenLink);
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(brokenLink));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(0, 1, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoveSelectedAsync_SkipsEmptyDirectoryCandidateWhenPathBecomesSymlink()
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
            var summary = await screen.RemoveSelectedAsync(
                [0], TestContext.Current.CancellationToken);

            Assert.True(PathResolver.IsSymlink(target));
            Assert.Equal(0, screen.RemovedCount);
            Assert.Equal(new CleanupScreen.RemovalSummary(0, 1, Confirmed: true), summary);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RenderDetail_DefaultExplainsActionAndKeepsTechnicalEvidenceBehindDetails()
    {
        var candidate = new CleanupClassifier.Candidate(
            CleanupClassifier.CandidateKind.Duplicate,
            "/skills/demo`copy",
            "duplicate install",
            Skill("demo", "/skills/demo"));

        candidate = candidate with { Impact = OperationImpact.Payload(OperationSubject.From(candidate.Skill!), "cleanup") };
        var summary = CleanupScreen.RenderDetail(candidate);
        Assert.Contains("## Multiple copies", summary);
        Assert.Contains("Deletion has no automatic undo.", summary);
        Assert.Contains("`` /skills/demo`copy ``", summary);
        Assert.DoesNotContain("| Field |", summary);
        Assert.DoesNotContain(candidate.Impact.Subject.InstallationId, summary);
        var detail = CleanupScreen.RenderDetail(candidate, showDetails: true);
        Assert.Contains("## Technical details", detail);
        Assert.Contains("duplicate install", detail);
        Assert.Contains(candidate.Impact.Subject.InstallationId, detail);
        Assert.Contains("## Skill", detail);
        Assert.Contains("## Summary", detail);
    }

    [Fact]
    public void RenderDetail_ExplainsEmptyAndLinkOnlyRemovalAndReviewOnlyRefusals()
    {
        var empty = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.EmptyDirectory, "/skills/empty", "empty", null);
        var link = empty with { Kind = CleanupClassifier.CandidateKind.BrokenSymlink, Path = "/skills/link" };
        Assert.Contains("only if it is still empty", CleanupScreen.RenderDetail(empty));
        Assert.Contains("Removes only this link. Target files stay.", CleanupScreen.RenderDetail(link));
        var reviewOnly = link with { ReviewOnly = true };
        Assert.Contains("automatic removal is unavailable", CleanupScreen.RenderDetail(reviewOnly));
        Assert.Contains("Inspection changes nothing.", CleanupScreen.RenderDetail(reviewOnly));
    }

    [Fact]
    public void ConfirmationSummaryRetainsAgentsAndPathsWithoutNativeIds()
    {
        var skill = Skill("shared", "/skills/shared") with
        {
            Agents = [new("codex", "/agent/one", false, Scope.User, Context: "work-one", Profile: "one"),
                new("claude", "/agent/two", false, Scope.Project, Context: "work-two", Profile: "two")]
        };
        var impact = OperationImpact.Payload(OperationSubject.From(skill), "cleanup");
        var candidate = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.NameCollision, skill.ResolvedPath, "compare", skill, true)
        { Impact = impact };
        var text = CleanupScreen.BuildRemoveConfirmationText([candidate]);
        Assert.Contains("/skills/shared", text);
        Assert.Contains("codex", text); Assert.Contains("claude", text);
        Assert.Contains("work-one", text); Assert.Contains("work-two", text);
        Assert.Contains("automatic removal is unavailable", text);
        Assert.DoesNotContain(impact.Subject.InstallationId, text);
        Assert.DoesNotContain("| Field |", text);
    }

    [Fact]
    public void ProtectedEmptySummaryKeepsLocationBeforeLongEvidenceAndDoesNotRepeatTheReason()
    {
        var candidate = new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.EmptyDirectory,
            "/skills/empty", "context protection " + new string('x', 5000), null, ReviewOnly: true);
        var summary = CleanupScreen.RenderDetail(candidate);
        Assert.Contains("/skills/empty", summary);
        Assert.Contains("press F2", summary);
        Assert.DoesNotContain("Review needed", summary);
        Assert.Contains("context protection", CleanupScreen.RenderDetail(candidate, showDetails: true));
    }

    [Fact]
    public void BuildShortcuts_UsesCompactOrderedCleanupActions()
    {
        var shortcuts = CleanupScreen.BuildShortcutsForTests();

        Assert.Collection(
            shortcuts,
            shortcut =>
            {
                Assert.Equal("Space", shortcut.Title);
                Assert.Equal("Select", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("Enter", shortcut.Title);
                Assert.Equal("Review", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("F2", shortcut.Title);
                Assert.Equal("Details", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("i", shortcut.Title);
                Assert.Equal("Ignore", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("x", shortcut.Title);
                Assert.Equal("Export", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("Esc", shortcut.Title);
                Assert.Equal("Back", shortcut.HelpText);
            });
    }

    [Fact]
    public void BuildHeaderText_UsesShortSelectionHint()
    {
        Assert.Contains("Space Select", CleanupScreen.BuildHeaderTextForTests());
        Assert.Contains("Enter Review", CleanupScreen.BuildHeaderTextForTests());
        Assert.Contains("F2 Details", CleanupScreen.BuildHeaderTextForTests());
    }

    [Fact]
    public void ConfirmationGroupsKindsWithTheSameFriendlyIssueTogether()
    {
        var candidates = new[]
        {
            new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.Duplicate, "/one", "duplicate", null, true),
            new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.NameCollision, "/two", "collision", null, true),
            new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.HiddenNestedResidue, "/three", "hidden", null, true),
            new CleanupClassifier.Candidate(CleanupClassifier.CandidateKind.SourceOrphaned, "/four", "source", null, true)
        };
        var text = CleanupScreen.BuildRemoveConfirmationText(candidates);
        Assert.Contains("- Multiple copies: 2", text);
        Assert.Contains("- Installation needs review: 2", text);
        Assert.DoesNotContain("- Multiple copies: 1", text);
        Assert.DoesNotContain("- Installation needs review: 1", text);
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

        Assert.Contains("Review 3 cleanup item(s)", text);
        Assert.Contains("- Multiple copies: 2", text);
        Assert.Contains("- Broken link: 1", text);
        Assert.Contains("/tmp/a", text);
        Assert.Contains("/tmp/c", text);
    }

    [Fact]
    public void BuildRemoveConfirmationText_EnumeratesEverySelectedPath()
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
        Assert.Contains("/tmp/3", text);
        Assert.Contains("/tmp/4", text);
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
        IReadOnlyList<InstalledSkill>? allSkills = null,
        RemoveService? remove = null) =>
        new(
            app: null!,
            remove: remove ?? new RemoveService(new Logger()),
            logger: new Logger(),
            candidates,
            scanRoots,
            allSkills: allSkills ?? Array.Empty<InstalledSkill>(),
            confirmBatchRemoval: _ => 1);

    private static void DeletePath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException) when (PathResolver.IsSymlink(path))
        {
            Directory.Delete(path);
        }
    }
}
