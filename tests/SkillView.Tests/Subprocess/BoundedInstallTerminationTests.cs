using System.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Tests.Ui;
using Xunit;

namespace SkillView.Tests.Subprocess;

[Collection(TestCollections.ResourceStress)]
public sealed class BoundedInstallTerminationTests
{
    [Fact]
    public async Task RefusedTerminationReturnsStartedUndrainedOutcomeWithBothOpenPipesWithinBounds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Controlled POSIX child with open pipes.");
        var root = Directory.CreateTempSubdirectory("skillview-refused-termination-");
        using var child = new RefusingTerminationChild();
        using var cancellation = new CancellationTokenSource();
        var logger = new Logger(LogLevel.Debug);
        var runner = child.Runner(logger).WithIsolatedContext(root.FullName, new Dictionary<string, string>
        { ["HOME"] = root.FullName, ["PATH"] = "/usr/bin:/bin" });
        Task<ProcessResult>? execution = null;
        try
        {
            // exec preserves the exact observed child. sleep keeps stdout and
            // stderr open after the marker; neither reader can rely on EOF.
            execution = runner.RunInstallAsync("/bin/sh",
                ["-c", "printf 'retained stdout\\n'; printf 'retained stderr\\n' >&2; : > ready; exec /bin/sleep 30"],
                root.FullName, cancellation.Token, null);
            await WaitForAsync(() => File.Exists(Path.Combine(root.FullName, "ready")));
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
            Assert.True(elapsed.Elapsed >= RefusingTerminationChild.Grace);
            Assert.True(child.IsAlive);
            Assert.True(result.Started);
            Assert.Equal(ProcessStartState.Started, result.StartState);
            Assert.False(result.ChildDrained);
            Assert.True(result.Cancelled);
            Assert.Equal(130, result.ExitCode);
            Assert.Contains("retained stdout", result.StdOut);
            Assert.Contains("retained stderr", result.StdErr);
            Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains("did not exit within", StringComparison.Ordinal));
        }
        finally
        {
            // Killing/draining this held child also releases an old unbounded
            // implementation after WaitAsync fails, without leaving an orphan.
            cancellation.Cancel();
            if (execution is not null && !execution.IsCompleted && !child.IsAlive)
                await Task.WhenAny(execution, child.Observed).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            child.Dispose();
            if (execution is not null) await execution.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            root.Delete(true);
        }
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
