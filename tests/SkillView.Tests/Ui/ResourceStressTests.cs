using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.ResourceStress)]
public sealed class ResourceStressTests : IDisposable
{
    private const long AllocationBudget = 256L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "skillview-stress-" + Guid.NewGuid().ToString("N"));

    public ResourceStressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task NoisySubprocessErrors_KeepManagedAllocationsAndCaptureBounded()
    {
        const int captureLimit = 4 * 1024;
        var runner = new ProcessRunner(new Logger(), captureLimit);
        var (executable, arguments) = CreateNoisyCommand();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var result = await runner.RunAsync(
            executable,
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(result.Succeeded);
        Assert.Contains($"output truncated after {captureLimit} characters", result.StdErr);
        Assert.InRange(result.StdErr.Length, captureLimit, captureLimit + 100);
        Assert.InRange(allocated, 0, AllocationBudget);
    }

    [Fact]
    public void OversizedSkillFile_ReadsOnlyBoundedPrefix()
    {
        var scanRoot = Path.Combine(_root, "oversized", "skills");
        var skill = Path.Combine(scanRoot, "large-skill");
        Directory.CreateDirectory(skill);
        File.WriteAllText(
            Path.Combine(skill, LocalSkillScanner.SkillFileName),
            "---\nname: large-skill\ndescription: " + new string('x', 8 * 1024 * 1024));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = new LocalSkillScanner(new Logger()).Scan(
            [new ScanRoot(scanRoot, Scope.User, "test")]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(result);
        Assert.InRange(allocated, 0, 16L * 1024 * 1024);
    }

    [Fact]
    public void LargeInventory_RemainsWithinLinearBound()
    {
        const int count = 500;
        var scanRoot = Path.Combine(_root, "inventory", "skills");
        Directory.CreateDirectory(scanRoot);
        for (var index = 0; index < count; index++)
        {
            var skill = Path.Combine(scanRoot, $"skill-{index:D4}");
            Directory.CreateDirectory(skill);
            File.WriteAllText(
                Path.Combine(skill, LocalSkillScanner.SkillFileName),
                $"---\nname: skill-{index:D4}\n---\nbody");
        }
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = new LocalSkillScanner(new Logger()).Scan(
            [new ScanRoot(scanRoot, Scope.User, "test")]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, result.Length);
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
    }

    [Fact]
    public void LargeRemovalTree_UsesBoundedTraversalState()
    {
        const int fileCount = 2_000;
        const int directoryCount = 200;
        var skillPath = Path.Combine(_root, "removal", "large-skill");
        Directory.CreateDirectory(skillPath);
        File.WriteAllText(Path.Combine(skillPath, LocalSkillScanner.SkillFileName), "body");
        for (var index = 0; index < fileCount; index++)
        {
            File.WriteAllText(Path.Combine(skillPath, $"file-{index:D4}.txt"), "x");
        }
        for (var index = 0; index < directoryCount; index++)
        {
            var nested = Path.Combine(skillPath, $"dir-{index:D3}");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "child.txt"), "x");
        }
        var skill = new InstalledSkill
        {
            Name = "large-skill",
            ResolvedPath = skillPath,
            ScanRoot = Path.GetDirectoryName(skillPath)!,
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = "large-skill" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
        };
        var root = new ScanRoot(skill.ScanRoot, Scope.User, "test");
        var validation = RemoveValidator.Validate(skill, [root], [skill]);
        Assert.True(validation.Allowed);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var report = new RemoveService(new Logger()).Remove(
            validation,
            cancellationToken: TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(report.Succeeded, string.Join(System.Environment.NewLine, report.Errors));
        Assert.False(Directory.Exists(skillPath));
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
    }

    private static (string Executable, string[] Arguments) CreateNoisyCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("pwsh", new[]
            {
                "-NoProfile",
                "-Command",
                "[Console]::Error.Write('e' * 4194304)"
            });
        }

        return ("/bin/sh", new[]
        {
            "-c",
            "i=0; while [ $i -lt 4096 ]; do printf '%01024d' 0 >&2; i=$((i+1)); done"
        });
    }
}
