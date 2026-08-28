using System.Runtime.InteropServices;
using System.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Subprocess;

public sealed class ProcessRunnerTests
{
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

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
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
