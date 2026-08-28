using System.IO;
using System.Runtime.InteropServices;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Inventory;

public class LocalSkillScannerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Logger _logger = new();

    public LocalSkillScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private string MakeSkill(string root, string name, string? skillMdBody = null, bool includeSkillMd = true)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        if (includeSkillMd)
        {
            File.WriteAllText(
                Path.Combine(dir, "SKILL.md"),
                skillMdBody ?? $"---\nname: {name}\ndescription: test\n---\nbody\n");
        }
        return dir;
    }

    [Fact]
    public void Scan_finds_valid_skill()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(root, "foo");

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });

        var foo = Assert.Single(results);
        Assert.Equal("foo", foo.Name);
        Assert.Equal(ValidityState.Valid, foo.Validity);
        Assert.Equal(Scope.User, foo.Scope);
        Assert.Equal(Provenance.FsScan, foo.Provenance);
    }

    [Fact]
    public void Scan_flags_missing_skill_md()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(root, "orphan", includeSkillMd: false);

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });

        var orphan = Assert.Single(results);
        Assert.Equal(ValidityState.MissingSkillMd, orphan.Validity);
    }

    [Fact]
    public void Scan_flags_unparsable_frontmatter()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(root, "weird", skillMdBody: "no fence at all\n");

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });

        Assert.Equal(ValidityState.UnparsableFrontMatter, results[0].Validity);
    }

    [Fact]
    public void Scan_respects_skillview_ignore_marker()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        var dir = MakeSkill(root, "ignored");
        File.WriteAllText(Path.Combine(dir, ".skillview-ignore"), "");

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });

        Assert.True(results[0].Ignored);
    }

    [Fact]
    public void Scan_skips_hidden_by_default_and_includes_when_allowed()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(root, "visible");
        MakeSkill(root, ".hidden");

        var scanner = new LocalSkillScanner(_logger);
        var defaultResults = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });
        Assert.Single(defaultResults);

        var hiddenResults = scanner.Scan(
            new[] { new ScanRoot(root, Scope.User, "claude") },
            new LocalSkillScanner.Options(AllowHiddenDirs: true));
        Assert.Equal(2, hiddenResults.Length);
    }

    [Fact]
    public void Scan_dedupes_by_resolved_path_when_two_roots_point_at_same_dir()
    {
        var canonical = Path.Combine(_tempRoot, "canonical");
        Directory.CreateDirectory(canonical);
        MakeSkill(canonical, "shared");

        var rootA = Path.Combine(_tempRoot, "rootA");
        var rootB = Path.Combine(_tempRoot, "rootB");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        Directory.CreateSymbolicLink(Path.Combine(rootA, "shared"), Path.Combine(canonical, "shared"));
        Directory.CreateSymbolicLink(Path.Combine(rootB, "shared"), Path.Combine(canonical, "shared"));

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[]
        {
            new ScanRoot(rootA, Scope.User, "claude"),
            new ScanRoot(rootB, Scope.User, "cursor"),
        });

        var shared = Assert.Single(results);
        Assert.True(shared.IsSymlinked);
        Assert.Equal(2, shared.Agents.Length);
        Assert.Contains(shared.Agents, a => a.AgentId == "claude");
        Assert.Contains(shared.Agents, a => a.AgentId == "cursor");
    }

    [Fact]
    public void Scan_reports_broken_symlinks_as_BrokenSymlink()
    {
        // Symbolic-link creation on Windows without dev-mode can fail. Skip on
        // platforms where the API throws, so the test is meaningful on POSIX
        // without being flaky elsewhere.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        var target = Path.Combine(_tempRoot, "missing-target");
        Directory.CreateSymbolicLink(Path.Combine(root, "dangling"), target);

        var scanner = new LocalSkillScanner(_logger);
        var results = scanner.Scan(new[] { new ScanRoot(root, Scope.User, "claude") });

        Assert.Contains(results, r => r.Validity == ValidityState.BrokenSymlink);
    }

    [Fact]
    public void Scan_CancellationBetweenCandidates_StopsPromptly()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(root, "one");
        MakeSkill(root, "two");
        MakeSkill(root, "three");

        using var cancellation = new CancellationTokenSource();
        var observed = 0;
        var scanner = new LocalSkillScanner(
            _logger,
            _ =>
            {
                if (Interlocked.Increment(ref observed) == 1)
                {
                    cancellation.Cancel();
                }
            });

        Assert.Throws<OperationCanceledException>(() => scanner.ScanWithCancellation(
            [new ScanRoot(root, Scope.User, "claude")],
            options: null,
            cancellation.Token));
        Assert.Equal(1, observed);
    }

    [Fact]
    public void Scan_DoesNotReadPastBoundedFrontMatterPrefix()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        MakeSkill(
            root,
            "oversized-frontmatter",
            "---\nname: oversized-frontmatter\ndescription: "
            + new string('x', LocalSkillScanner.MaxFrontMatterPrefixBytes)
            + "\n---\nbody\n");

        var scanner = new LocalSkillScanner(_logger);
        var result = Assert.Single(scanner.Scan(
            [new ScanRoot(root, Scope.User, "claude")]));

        Assert.Equal(ValidityState.UnparsableFrontMatter, result.Validity);
    }

    [Fact]
    public void Scan_IterationFailure_IsLoggedWithoutAbortingInventoryCapture()
    {
        var root = Path.Combine(_tempRoot, ".claude", "skills");
        Directory.CreateDirectory(root);
        var scanner = new LocalSkillScanner(
            _logger,
            candidateObservedForTests: null,
            _ => new ThrowingEnumerator());

        var result = scanner.Scan([new ScanRoot(root, Scope.User, "claude")]);

        Assert.Empty(result);
        Assert.Contains(
            _logger.Snapshot(),
            entry => entry.Category == "inventory.scan"
                && entry.Message.Contains("enumerate", StringComparison.Ordinal));
    }

    private sealed class ThrowingEnumerator : IEnumerator<string>
    {
        public string Current => throw new InvalidOperationException();
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => throw new IOException("simulated disconnect");
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
