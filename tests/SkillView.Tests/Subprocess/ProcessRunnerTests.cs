using System.Runtime.InteropServices;
using System.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Subprocess;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_EnvironmentOverridesAreChildOnly()
    {
        const string name = "SKILLVIEW_STAGE_TEST";
        var parent = System.Environment.GetEnvironmentVariable(name);
        var command = OperatingSystem.IsWindows()
            ? ("pwsh", new[] { "-NoProfile", "-Command", "[Console]::Write($env:SKILLVIEW_STAGE_TEST)" })
            : ("/bin/sh", new[] { "-c", "printf '%s' \"$SKILLVIEW_STAGE_TEST\"" });
        var result = await new ProcessRunner(new Logger()).RunAsync(command.Item1, command.Item2,
            cancellationToken: TestContext.Current.CancellationToken,
            environment: new Dictionary<string, string> { [name] = "private-stage" });
        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal("private-stage", result.StdOut);
        Assert.Equal(parent, System.Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public async Task RunAsync_PreCanceledRequest_DoesNotAttemptProcessStart()
    {
        var runner = new ProcessRunner(new Logger(LogLevel.Debug));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                "skillview-executable-that-must-not-be-started",
                [],
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_CancellationDuringStartupLogging_PropagatesCancellation()
    {
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        using var cancellation = new CancellationTokenSource();
        using var subscription = logger.Subscribe(entry =>
        {
            if (entry.Category == "subprocess")
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                "skillview-executable-that-must-not-be-started",
                [],
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_ClosesStandardInput_ForCommandsThatWaitForEof()
    {
        var runner = new ProcessRunner(new Logger(LogLevel.Debug));
        var (executable, arguments) = CreateWaitForEofCommand();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var result = await runner.RunAsync(executable, arguments, cancellationToken: cts.Token);

        Assert.True(result.Succeeded);
        Assert.Contains("done", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_BoundsCapturedOutputAndMarksTruncation()
    {
        const int limit = 128;
        var runner = new ProcessRunner(new Logger(LogLevel.Debug), limit);
        var (executable, arguments) = CreateLargeOutputCommand();

        var result = await runner.RunAsync(executable, arguments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("output truncated after 128 characters", result.StdOut);
        Assert.InRange(result.StdOut.Length, limit, limit + 80);
        Assert.Contains("output truncated after 128 characters", result.StdErr);
        Assert.InRange(result.StdErr.Length, limit, limit + 80);
    }

    [Fact]
    public async Task RunAsync_CanceledUpdateRetainsBothStreamsAfterTermination()
    {
        var marker = Path.Combine(Path.GetTempPath(), "skillview-output-ready-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var command = OperatingSystem.IsWindows()
                ? ("pwsh", new[] { "-NoProfile", "-Command",
                    "[Console]::Out.WriteLine('Updated first'); [Console]::Out.Flush(); " +
                    "[Console]::Error.WriteLine('second pending'); [Console]::Error.Flush(); " +
                    "[IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal) + "','ready'); Start-Sleep -Seconds 30" })
                : ("/bin/sh", new[] { "-c", "printf '%s\\n' 'Updated first'; printf '%s\\n' 'second pending' >&2; printf ready > \"$1\"; sleep 30", "skillview", marker });
            var task = new ProcessRunner(new Logger(), terminationWait: TimeSpan.FromSeconds(2)).RunAsync(
                command.Item1, command.Item2, cancellationToken: cancellation.Token, retainCancellationOutput: true);
            using var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(10, admissionDeadline.Token);
            cancellation.Cancel();
            var result = await task;
            Assert.True(result.Cancelled);
            Assert.Equal(130, result.ExitCode);
            Assert.Contains("Updated first", result.StdOut);
            Assert.Contains("second pending", result.StdErr);
        }
        finally { cancellation.Cancel(); if (File.Exists(marker)) File.Delete(marker); }
    }

    [Fact]
    public async Task RunAsync_CancellationTerminatesWithinBoundedWait()
    {
        var runner = new ProcessRunner(
            new Logger(LogLevel.Debug),
            terminationWait: TimeSpan.FromSeconds(2));
        var (executable, arguments) = CreateLongRunningCommand();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(executable, arguments, cancellationToken: cancellation.Token));

        // Windows CI can spend several seconds starting and terminating pwsh
        // under load. Keep this well below the child's 30-second natural exit
        // without treating scheduler delay as a subprocess cancellation bug.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    private static (string Executable, string[] Arguments) CreateWaitForEofCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("pwsh", new[]
            {
                "-NoProfile",
                "-Command",
                "$input | Out-Null; Write-Output done"
            });
        }

        return ("/bin/sh", new[]
        {
            "-c",
            "cat >/dev/null; printf done"
        });
    }

    private static (string Executable, string[] Arguments) CreateLargeOutputCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("pwsh", new[]
            {
                "-NoProfile",
                "-Command",
                "[Console]::Out.Write('x' * 4096); [Console]::Error.Write('e' * 4096)"
            });
        }

        return ("/bin/sh", new[]
        {
            "-c",
            "i=0; while [ $i -lt 512 ]; do printf 0123456789; printf abcdefghij >&2; i=$((i+1)); done"
        });
    }

    private static (string Executable, string[] Arguments) CreateLongRunningCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("pwsh", new[]
            {
                "-NoProfile",
                "-Command",
                "Start-Sleep -Seconds 30"
            });
        }

        return ("/bin/sh", new[] { "-c", "sleep 30" });
    }
}
