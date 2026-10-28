using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class StandaloneMutationReportingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeApplyOrUndoSurvivesOneFailedFollowupCapture(bool restoring)
    {
        using var fixture = new Fixture();
        var inventory = fixture.Inventory(); var target = Assert.Single(inventory.Skills);
        var preview = fixture.Store().Preview(target, inventory, TestContext.Current.CancellationToken);
        QuarantineResult? prior = null;
        if (restoring)
        {
            prior = fixture.Store().Apply(target, inventory, preview.ReviewFingerprint, true, TestContext.Current.CancellationToken);
            Assert.True(prior.Succeeded, prior.Message);
        }
        var coordinator = fixture.Coordinator();
        await coordinator.CaptureInventoryAsync(fixture.Report, TestContext.Current.CancellationToken);
        Assert.NotNull(coordinator.DiscoverObservation);
        var generation = fixture.Services.ListAdapter.Generation;
        var executions = 0; var captures = 0; QuarantineResult? native = null;
        var result = await coordinator.RunStandaloneMutationAsync("captured-context", journal =>
        {
            executions++;
            return native = restoring
                ? fixture.Store(journal).Undo(prior!.Id, fixture.Inventory(), true, TestContext.Current.CancellationToken)
                : fixture.Store(journal).Apply(target, inventory, preview.ReviewFingerprint, true, TestContext.Current.CancellationToken);
        }, (context, token) =>
        {
            Assert.Equal("captured-context", context); Assert.True(token.CanBeCanceled); Assert.False(token.IsCancellationRequested);
            captures++;
            throw new IOException("\u001b[31mfollowup capture unavailable [literal](https://invalid.test) " + new string('x', 500) + "\nsecond line");
        });
        Assert.Equal(1, executions); Assert.Equal(1, captures);
        Assert.Same(native, result.Native); Assert.True(result.Native.Succeeded, result.Native.Message);
        Assert.True(result.Native.MutationObserved); Assert.Equal(restoring ? "restored" : "quarantined", result.Native.Status);
        Assert.Contains("post-action inventory verification failed: followup capture unavailable", result.Verification);
        Assert.Contains("Refresh Installed", result.Verification); Assert.DoesNotContain("No new native move", result.Verification);
        Assert.DoesNotContain('\u001b', result.Verification); Assert.DoesNotContain("second line", result.Verification);
        Assert.True(result.Verification.Length < 300);
        Assert.Equal(generation + 1, fixture.Services.ListAdapter.Generation); Assert.Null(coordinator.DiscoverObservation);
        Assert.Equal(restoring, Directory.Exists(fixture.Payload));
        Assert.Equal(!restoring, Directory.Exists(result.Native.RetainedPath));
        var evidence = restoring ? fixture.Payload : result.Native.RetainedPath;
        Assert.Equal(new byte[] { 0, 255, 3 }, File.ReadAllBytes(Path.Combine(evidence, "data")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalAndJournalOnlyResultsNeverBecomeSuccessfulMoves(bool journaled)
    {
        using var fixture = new Fixture(); var coordinator = fixture.Coordinator();
        var generation = fixture.Services.ListAdapter.Generation; var captures = 0;
        var native = new QuarantineResult("operation", journaled ? "prepared" : "refused", false, false, false,
            fixture.Payload, "retained", null, "No completed move");
        var result = await coordinator.RunStandaloneMutationAsync("context", journal =>
        { if (journaled) journal("prepared.json"); return native; }, (_, _) =>
        { captures++; throw new IOException("capture failed"); });
        Assert.Same(native, result.Native); Assert.False(result.Native.Succeeded); Assert.False(result.Native.MutationObserved);
        Assert.Equal(journaled ? 1 : 0, captures); Assert.Equal(generation + captures, fixture.Services.ListAdapter.Generation);
        Assert.Contains(journaled ? "verification failed" : "No new native move reported", result.Verification);
        Assert.True(Directory.Exists(fixture.Payload));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-standalone-reporting-");
        internal string Payload => Path.Combine(_root.FullName, "skills", "demo");
        internal TuiServices Services { get; }
        internal EnvironmentReport Report { get; } = new()
        {
            GhPath = null,
            GhVersionRaw = null,
            GhVersion = null,
            GhMeetsMinimum = false,
            Auth = GhAuthStatus.Unknown,
            GhSkillAvailable = false,
            LogDirectory = null
        };
        internal Fixture()
        {
            Directory.CreateDirectory(Payload); File.WriteAllText(Path.Combine(Payload, "SKILL.md"), "---\nname: demo\ndescription: Reporting fixture\n---\nBody\n");
            File.WriteAllBytes(Path.Combine(Payload, "data"), [0, 255, 3]);
            var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
            var work = Directory.CreateDirectory(Path.Combine(_root.FullName, "work")).FullName;
            Services = TuiServices.Build(new Logger(), processEnvironment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["XDG_STATE_HOME"] = Path.Combine(_root.FullName, "state"),
                ["XDG_CONFIG_HOME"] = Path.Combine(_root.FullName, "config"),
                ["CODEX_HOME"] = Path.Combine(home, ".codex"),
                ["XDG_DATA_HOME"] = Path.Combine(_root.FullName, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(_root.FullName, "cache"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
                ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
                ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
                ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(home, ".claude/plugins"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi"),
                ["GIT_DIR"] = Path.Combine(work, ".git"),
                ["GIT_WORK_TREE"] = work,
                ["GIT_COMMON_DIR"] = Path.Combine(work, ".git"),
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(_root.FullName, "no-global-config")
            }, workingDirectory: work);
        }
        internal InventorySnapshot Inventory()
        {
            var root = new ScanRoot(Path.GetDirectoryName(Payload)!, Scope.Custom, null);
            return InventorySnapshot.Empty with { Skills = new LocalSkillScanner(new Logger()).Scan([root]).ToImmutableArray(), ScannedRoots = [root], ConfiguredRoots = [root] };
        }
        internal QuarantineStore Store(Action<string>? journal = null) => new(Path.Combine(_root.FullName, "quarantine"), new Logger(), afterJournal: journal, runner: Services.Runner);
        internal SkillViewWorkflowCoordinator Coordinator() => new(Services,
            new(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [Path.GetDirectoryName(Payload)!], null, []),
            () => null, () => null, () => Report, _ => Task.FromResult(Report), _ => { }, () => { }, _ => { }, (_, _) => { },
            action => action(), (action, _) => { action(); return Task.FromResult(true); }, (_, _) => false, () => { }, () => { });
        public void Dispose() => _root.Delete(true);
    }
}
