using System.Runtime.InteropServices;
using System.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Subprocess;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_IsolatedContextDoesNotInheritParentOrConfiguredCredentials()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The fixed /usr/bin/env fixture requires Linux.");
        using var temporary = new PrivateProcessDirectory();
        var runner = new ProcessRunner(new Logger(), environment: new Dictionary<string, string> { ["GH_TOKEN"] = "must-not-inherit", ["GIT_DIR"] = "/must-not-inherit" });
        var parentHome = System.Environment.GetEnvironmentVariable("HOME");
        var isolated = runner.WithIsolatedContext(temporary.Path, new Dictionary<string, string>
        { ["HOME"] = temporary.Path, ["XDG_STATE_HOME"] = temporary.Path, ["TMPDIR"] = "/var/tmp" });
        Assert.Null(isolated.GetEffectiveEnvironmentVariable("GH_TOKEN"));
        Assert.Null(isolated.WithCaptureLimit(1024).GetEffectiveEnvironmentVariable("GH_TOKEN"));
        Assert.Null(isolated.WithContext(temporary.Path, new Dictionary<string, string>()).GetEffectiveEnvironmentVariable("GH_TOKEN"));
        var result = await isolated.RunAsync("/usr/bin/env", [], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal(new[] { "HOME=" + temporary.Path, "TMPDIR=/var/tmp", "XDG_STATE_HOME=" + temporary.Path },
            result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(parentHome, System.Environment.GetEnvironmentVariable("HOME"));
    }

    private sealed class PrivateProcessDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("skillview-process-context-");
        internal string Path => directory.FullName;
        public void Dispose() => directory.Delete();
    }

    [Fact]
    public async Task CapturedContextCopiesCallerValuesAndDerivedChildrenUseThatSnapshot()
    {
        using var folder = new ContextDirectory();
        const string name = "SKILLVIEW_CAPTURE_TEST";
        const string late = "SKILLVIEW_CAPTURE_LATE";
        var values = new Dictionary<string, string> { [name] = "captured", [late] = "" };
        var runner = new ProcessRunner(new Logger(), environment: values, defaultWorkingDirectory: folder.Path);
        var captured = runner.CaptureContext(); var fingerprint = captured.CaptureContextFingerprint();
        values[name] = "changed"; values[late] = "added-later";
        var absent = "SKILLVIEW_CAPTURE_ABSENT_" + Guid.NewGuid().ToString("N");
        values[absent] = "added-later";
        Assert.Same(captured, captured.CaptureContext());
        Assert.Equal("captured", captured.GetEffectiveEnvironmentVariable(name));
        Assert.Equal("", captured.GetEffectiveEnvironmentVariable(late));
        Assert.Null(captured.GetEffectiveEnvironmentVariable(absent));
        Assert.False(captured.CaptureEnvironment().ContainsKey(absent));
        Assert.Equal(fingerprint, captured.CaptureContextFingerprint());
        Assert.NotEqual(fingerprint, runner.CaptureContextFingerprint());
        var copied = captured.CaptureEnvironment(); copied[name] = "copy-mutation";
        Assert.Equal("captured", captured.GetEffectiveEnvironmentVariable(name));
        var child = captured.WithContext(folder.Path, new Dictionary<string, string> { [name] = "derived" });
        var command = OperatingSystem.IsWindows()
            ? ("pwsh", new[] { "-NoProfile", "-Command", "[Console]::Write($env:SKILLVIEW_CAPTURE_TEST + '|' + $env:SKILLVIEW_CAPTURE_LATE)" })
            : ("/bin/sh", new[] { "-c", "printf '%s|%s' \"$SKILLVIEW_CAPTURE_TEST\" \"$SKILLVIEW_CAPTURE_LATE\"" });
        var result = await child.RunAsync(command.Item1, command.Item2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal("derived|", result.StdOut);
        Assert.Equal(folder.Path, child.EffectiveWorkingDirectory);
        Assert.Equal("captured", captured.GetEffectiveEnvironmentVariable(name));
    }

    private sealed class ContextDirectory : IDisposable
    {
        private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("skillview-context-");
        internal string Path => folder.FullName;
        public void Dispose() => folder.Delete(true);
    }

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
