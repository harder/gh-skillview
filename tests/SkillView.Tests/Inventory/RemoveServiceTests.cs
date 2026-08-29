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
    public async Task RemoveAsync_RefusedValidationPublishesTerminalProgress()
    {
        var (skill, dir) = MakeSkill("bad-progress");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.False(validation.Allowed);
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        var report = await new RemoveService(_logger).RemoveAsync(
            validation,
            cancellationToken: TestContext.Current.CancellationToken,
            progress: progress);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(2, updates.Count);
        Assert.False(updates[0].IsCompleted);
        Assert.True(updates[^1].IsCompleted);
        Assert.False(updates[^1].IsCanceled);
        Assert.Equal(1, updates[^1].TargetsProcessed);
        Assert.Equal(0, updates[^1].TargetsDeleted);
        Assert.Equal(0, updates[^1].FilesProcessed);
        Assert.Equal(0, updates[^1].DirectoriesProcessed);
        Assert.Equal(1, updates[^1].Errors);
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
    public void Remove_TargetReplacedAfterValidation_RefusesBothObjects()
    {
        var (skill, dir) = MakeSkill("target-replaced");
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.True(validation.Allowed);
        Assert.NotNull(validation.ExecutionIdentity);

        var original = Path.Combine(_tempRoot, "target-replaced-original");
        Directory.Move(dir, original);
        Directory.CreateDirectory(dir);
        var replacementFile = Path.Combine(dir, "replacement.txt");
        File.WriteAllText(replacementFile, "keep replacement");

        var report = new RemoveService(_logger).Remove(
            validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Errors,
            error => error.Contains("identity changed", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(original, "SKILL.md")));
        Assert.Equal("keep replacement", File.ReadAllText(replacementFile));
    }

    [Fact]
    public void Remove_TargetReplacedByLinkAfterValidation_RefusesLinkAndBothDirectories()
    {
        var (skill, dir) = MakeSkill("target-replaced-by-link");
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        Assert.True(validation.Allowed);
        Assert.NotNull(validation.ExecutionIdentity);

        var original = Path.Combine(_tempRoot, "target-replaced-by-link-original");
        var external = Path.Combine(_tempRoot, "target-replaced-by-link-external");
        Directory.Move(dir, original);
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "must-survive.txt");
        File.WriteAllText(externalFile, "keep");
        if (!TryCreateDirectoryLink(dir, external)) return;

        var report = new RemoveService(_logger).Remove(
            validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.True(PathResolver.IsSymlink(dir));
        Assert.True(File.Exists(Path.Combine(original, "SKILL.md")));
        Assert.Equal("keep", File.ReadAllText(externalFile));
    }

    [Fact]
    public void Remove_DirectoryReplacedWhileObserved_DoesNotTraverseReplacement()
    {
        var (skill, dir) = MakeSkill("directory-swap");
        var nested = Path.Combine(dir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "original.txt"), "original");
        var external = Path.Combine(_tempRoot, "directory-swap-external");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "must-survive.txt");
        File.WriteAllText(externalFile, "keep");
        var movedOriginal = Path.Combine(_tempRoot, "directory-swap-original");
        var swapped = false;
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var service = new RemoveService(_logger, observed =>
        {
            if (swapped || !PathIdentity.Equals(observed, nested)) return;
            Directory.Move(nested, movedOriginal);
            swapped = TryCreateDirectoryLink(nested, external);
        });

        var report = service.Remove(
            validation,
            cancellationToken: TestContext.Current.CancellationToken);

        if (!swapped) return;
        Assert.False(report.Succeeded);
        Assert.True(File.Exists(externalFile));
        Assert.Equal("keep", File.ReadAllText(externalFile));
        Assert.True(File.Exists(Path.Combine(movedOriginal, "original.txt")));
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
    public void Remove_CancellationBetweenDirectoryEntriesStopsEnumerationPromptly()
    {
        var (skill, dir) = MakeSkill("cancel-during-enumeration", extraFiles: 2_000);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        using var cancellation = new CancellationTokenSource();
        var observedEntries = 0;
        var service = new RemoveService(_logger, _ =>
        {
            if (Interlocked.Increment(ref observedEntries) == 1)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() =>
            service.Remove(validation, cancellationToken: cancellation.Token));

        Assert.Equal(1, observedEntries);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(2_001, Directory.EnumerateFiles(dir).Count());
    }

    [Fact]
    public void Remove_CancellationAtLeafDeletionBoundary_DoesNotDeleteObservedEntry()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var (skill, dir) = MakeSkill("cancel-before-delete");
        var skillFile = Path.Combine(dir, "SKILL.md");
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        using var cancellation = new CancellationTokenSource();
        var deletingEntries = 0;
        var service = new RemoveService(
            _logger,
            entryObservedForTests: null,
            entryDeletingForTests: (_, isDirectory) =>
            {
                if (isDirectory) return;
                Interlocked.Increment(ref deletingEntries);
                cancellation.Cancel();
            });

        Assert.Throws<OperationCanceledException>(() =>
            service.Remove(validation, cancellationToken: cancellation.Token));

        Assert.Equal(1, deletingEntries);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(skillFile));
    }

    [Fact]
    public void Remove_CancellationAtDirectoryDeletionBoundary_DoesNotDeleteDirectory()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var (skill, dir) = MakeSkill("cancel-before-directory-delete");
        var nested = Path.Combine(dir, "nested");
        Directory.CreateDirectory(nested);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var canonicalNested = Path.Combine(validation.ResolvedPath, "nested");
        using var cancellation = new CancellationTokenSource();
        var service = new RemoveService(
            _logger,
            entryObservedForTests: null,
            entryDeletingForTests: (path, isDirectory) =>
            {
                if (isDirectory && PathIdentity.Equals(path, canonicalNested)) cancellation.Cancel();
            });

        Assert.Throws<OperationCanceledException>(() =>
            service.Remove(validation, cancellationToken: cancellation.Token));

        Assert.True(Directory.Exists(dir));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void Remove_UnixFinalDirectoryNameReplacement_DeletesOnlyEmptyReplacement()
    {
        if (OperatingSystem.IsWindows() || !SecureRemovalBackend.IsSupported) return;
        var (skill, dir) = MakeSkill("final-directory-name");
        var nested = Path.Combine(dir, "nested");
        var movedOriginal = Path.Combine(_tempRoot, "final-directory-name-original");
        Directory.CreateDirectory(nested);
        var swapped = false;
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var canonicalNested = Path.Combine(validation.ResolvedPath, "nested");
        var service = new RemoveService(
            _logger,
            entryObservedForTests: null,
            entryDeletingForTests: (path, isDirectory) =>
            {
                if (swapped || !isDirectory || !PathIdentity.Equals(path, canonicalNested)) return;
                Directory.Move(canonicalNested, movedOriginal);
                Directory.CreateDirectory(canonicalNested);
                swapped = true;
            });

        var report = service.Remove(
            validation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(swapped);
        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(movedOriginal));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(87, true)]
    [InlineData(5, false)]
    public void WindowsDispositionFallback_RecognizesUnsupportedErrors(int error, bool expected)
    {
        Assert.Equal(expected, WindowsSecureRemovalBackend.ShouldFallbackToLegacyDisposition(error));
    }

    [Fact]
    public async Task RemoveAsync_CancellationPublishesTerminalProgress()
    {
        var (skill, dir) = MakeSkill("cancel-async", extraFiles: 2_000);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        using var cancellation = new CancellationTokenSource();
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);
        var service = new RemoveService(_logger, _ => cancellation.Cancel());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RemoveAsync(validation, cancellationToken: cancellation.Token, progress: progress));

        Assert.NotEmpty(updates);
        Assert.True(updates[^1].IsCanceled);
        Assert.False(updates[^1].IsCompleted);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task RemoveAsync_ThrottlesProgressAndPublishesCompletion()
    {
        var (skill, dir) = MakeSkill("progress", extraFiles: 2_000);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        var report = await new RemoveService(_logger).RemoveAsync(
            validation,
            new RemoveService.Options(DryRun: true),
            TestContext.Current.CancellationToken,
            progress);

        Assert.True(report.Succeeded);
        Assert.True(Directory.Exists(dir));
        Assert.InRange(updates.Count, 2, 999);
        Assert.True(updates[^1].IsCompleted);
        Assert.False(updates[^1].IsCanceled);
        Assert.Equal(1, updates[^1].TargetsProcessed);
        Assert.Equal(1, updates[^1].TargetsDeleted);
        Assert.Equal(report.FilesDeleted, updates[^1].FilesProcessed);
        Assert.Equal(report.DirectoriesDeleted, updates[^1].DirectoriesProcessed);
    }

    [Fact]
    public async Task RemoveManyAsync_ReportsAggregateMonotonicProgress()
    {
        var (first, _) = MakeSkill("progress-one", extraFiles: 2);
        var (second, _) = MakeSkill("progress-two", extraFiles: 2);
        var firstValidation = RemoveValidator.Validate(first, new[] { Root() }, new[] { first, second });
        var secondValidation = RemoveValidator.Validate(second, new[] { Root() }, new[] { first, second });
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        var report = await new RemoveService(_logger).RemoveManyAsync(
            [firstValidation, secondValidation],
            cancellationToken: TestContext.Current.CancellationToken,
            progress: progress);

        Assert.True(report.Succeeded);
        Assert.NotEmpty(updates);
        Assert.True(updates[^1].IsCompleted);
        Assert.Equal(2, updates[^1].TargetsProcessed);
        Assert.Equal(2, updates[^1].TargetsDeleted);
        Assert.Equal(report.FilesDeleted, updates[^1].FilesProcessed);
        Assert.Equal(report.DirectoriesDeleted, updates[^1].DirectoriesProcessed);
        Assert.True(updates.Zip(updates.Skip(1), (left, right) =>
            left.FilesProcessed <= right.FilesProcessed
            && left.DirectoriesProcessed <= right.DirectoriesProcessed
            && left.TargetsProcessed <= right.TargetsProcessed
            && left.TargetsDeleted <= right.TargetsDeleted).All(value => value));
    }

    [Fact]
    public async Task RemoveManyAsync_CancellationBetweenTargetsPublishesExactAggregate()
    {
        var (first, firstDir) = MakeSkill("cancel-after-one", extraFiles: 2);
        var (second, secondDir) = MakeSkill("must-remain", extraFiles: 2);
        var firstValidation = RemoveValidator.Validate(first, new[] { Root() }, new[] { first, second });
        var secondValidation = RemoveValidator.Validate(second, new[] { Root() }, new[] { first, second });
        using var cancellation = new CancellationTokenSource();
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        IEnumerable<RemoveValidator.RemoveValidation> CancelBeforeSecond()
        {
            yield return firstValidation;
            cancellation.Cancel();
            yield return secondValidation;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RemoveService(_logger).RemoveManyAsync(
                CancelBeforeSecond(),
                cancellationToken: cancellation.Token,
                progress: progress));

        Assert.False(Directory.Exists(firstDir));
        Assert.True(Directory.Exists(secondDir));
        Assert.True(updates[^1].IsCanceled);
        Assert.Equal(1, updates[^1].TargetsProcessed);
        Assert.Equal(1, updates[^1].TargetsDeleted);
        Assert.Equal(3, updates[^1].FilesProcessed);
        Assert.Equal(1, updates[^1].DirectoriesProcessed);
    }

    [Fact]
    public async Task RemoveManyAsync_FailedTargetThenCancellationDoesNotReportTargetDeleted()
    {
        var refused = new RemoveValidator.RemoveValidation(
            Errors: ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                "not a skill")),
            Warnings: ImmutableArray<RemoveValidator.Warning>.Empty,
            ResolvedPath: Path.Combine(_tempRoot, "refused"),
            IncomingSymlinkPaths: ImmutableArray<string>.Empty);
        var (second, _) = MakeSkill("must-not-run");
        var secondValidation = RemoveValidator.Validate(
            second,
            new[] { Root() },
            new[] { second });
        using var cancellation = new CancellationTokenSource();
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        IEnumerable<RemoveValidator.RemoveValidation> CancelBeforeSecond()
        {
            yield return refused;
            cancellation.Cancel();
            yield return secondValidation;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RemoveService(_logger).RemoveManyAsync(
                CancelBeforeSecond(),
                cancellationToken: cancellation.Token,
                progress: progress));

        Assert.True(updates[^1].IsCanceled);
        Assert.Equal(1, updates[^1].TargetsProcessed);
        Assert.Equal(0, updates[^1].TargetsDeleted);
        Assert.Equal(0, updates[^1].FilesProcessed);
        Assert.Equal(0, updates[^1].DirectoriesProcessed);
        Assert.Equal(1, updates[^1].Errors);
    }

    [Fact]
    public async Task RemoveManyAsync_CancellationMidTargetPreservesPartialAggregate()
    {
        var (skill, dir) = MakeSkill("cancel-mid-target", extraFiles: 2_000);
        var validation = RemoveValidator.Validate(skill, new[] { Root() }, new[] { skill });
        using var cancellation = new CancellationTokenSource();
        var observedEntries = 0;
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);
        var service = new RemoveService(_logger, _ =>
        {
            if (Interlocked.Increment(ref observedEntries) == 2)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RemoveManyAsync(
                [validation],
                cancellationToken: cancellation.Token,
                progress: progress));

        Assert.True(Directory.Exists(dir));
        Assert.True(updates[^1].IsCanceled);
        Assert.Equal(0, updates[^1].TargetsProcessed);
        Assert.Equal(0, updates[^1].TargetsDeleted);
        Assert.Equal(1, updates[^1].FilesProcessed);
        Assert.Equal(0, updates[^1].DirectoriesProcessed);
    }

    [Fact]
    public async Task RemoveLinkAsync_DeletesOnlyObservedLink()
    {
        var externalFile = Path.Combine(_tempRoot, "keep.txt");
        File.WriteAllText(externalFile, "keep");
        var link = Path.Combine(_tempRoot, "agent-link.txt");
        if (!TryCreateFileLink(link, externalFile)) return;

        var report = await new RemoveService(_logger).RemoveLinkAsync(
            link,
            TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(PathResolver.IsSymlink(link));
        Assert.True(File.Exists(externalFile));
        Assert.Equal("keep", File.ReadAllText(externalFile));
    }

    [Fact]
    public async Task RemoveLinkAsync_AlreadyCanceledPublishesTerminalProgress()
    {
        var externalFile = Path.Combine(_tempRoot, "keep-canceled.txt");
        File.WriteAllText(externalFile, "keep");
        var link = Path.Combine(_tempRoot, "agent-link-canceled.txt");
        if (!TryCreateFileLink(link, externalFile)) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var updates = new List<RemoveService.RemoveProgress>();
        var progress = new CallbackProgress<RemoveService.RemoveProgress>(updates.Add);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RemoveService(_logger).RemoveLinkAsync(
                link,
                cancellation.Token,
                progress));

        Assert.True(PathResolver.IsSymlink(link));
        Assert.True(File.Exists(externalFile));
        Assert.Collection(
            updates,
            update =>
            {
                Assert.False(update.IsCompleted);
                Assert.False(update.IsCanceled);
            },
            update =>
            {
                Assert.False(update.IsCompleted);
                Assert.True(update.IsCanceled);
            });
    }

    [Fact]
    public void FailureCollector_RetainsBoundedDetailsAndExactCount()
    {
        var failures = new RemoveService.FailureCollector();
        var total = RemoveService.MaxRetainedErrors + 50;

        for (var i = 0; i < total; i++)
        {
            failures.Add($"failure {i}");
        }

        var retained = failures.ToImmutable();
        Assert.Equal(total, failures.Count);
        Assert.Equal(RemoveService.MaxRetainedErrors + 1, retained.Length);
        Assert.Equal("failure 0", retained[0]);
        Assert.Contains("50 additional error(s) omitted", retained[^1]);
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
