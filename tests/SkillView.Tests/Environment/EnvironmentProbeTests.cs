using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Environment;

public sealed class EnvironmentProbeTests
{
    [Fact]
    public async Task LocalBaselineAndInventoryFinishWhileIndependentAuthChildIsHeld()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Credential-free POSIX child fixture.");
        var root = Directory.CreateTempSubdirectory("skillview-auth-gate-");
        try
        {
            var script = Path.Combine(root.FullName, "gh");
            File.WriteAllText(script, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$HOME/calls\"\ncase \"$*\" in\n'auth status') touch \"$HOME/auth-entered\"; while :; do sleep 0.05; done ;;\n'--version') printf 'gh version 2.103.2\\n' ;;\n'skill --help') printf 'gh skill local fixture\\n' ;;\n*) printf '[]\\n' ;;\nesac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var logger = new Logger(LogLevel.Debug);
            var runner = new ProcessRunner(logger).WithIsolatedContext(root.FullName, new Dictionary<string, string>
            {
                ["HOME"] = root.FullName,
                ["USERPROFILE"] = root.FullName,
                ["PATH"] = "/usr/bin:/bin",
                ["CODEX_HOME"] = Path.Combine(root.FullName, ".codex"),
                ["SKILLVIEW_STATE_HOME"] = Path.Combine(root.FullName, "state"),
                ["GIT_DIR"] = Path.Combine(root.FullName, ".git"),
                ["GIT_WORK_TREE"] = root.FullName,
                ["GIT_COMMON_DIR"] = Path.Combine(root.FullName, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            });
            var locator = new GhBinaryLocator(runner, logger, () => root.FullName, File.Exists);
            var auth = new GhAuthService(runner, logger);
            var probe = new EnvironmentProbe(locator, auth, runner, logger, null);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var held = auth.GetStatusAsync(script, cancel.Token);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(Path.Combine(root.FullName, "auth-entered"))) await Task.Delay(10, timeout.Token);
                var report = await probe.ProbeAsync(TestContext.Current.CancellationToken, includeAuthentication: false);
                Assert.True(report.BaselineOk); Assert.Same(GhAuthStatus.Unknown, report.Auth); Assert.False(held.IsCompleted);
                var copy = Directory.CreateDirectory(Path.Combine(root.FullName, "skills", "offline")).FullName;
                File.WriteAllText(Path.Combine(copy, "SKILL.md"), "---\nname: offline\ndescription: Local\n---\nBody\n");
                var inventory = new SkillView.Inventory.LocalInventoryService(new(), new(logger), new(runner, logger), logger, runner);
                var result = await inventory.CaptureAsync(report.GhPath, new([Path.GetDirectoryName(copy)!], false), TestContext.Current.CancellationToken);
                Assert.Contains(result.Skills, skill => skill.Name == "offline"); Assert.False(held.IsCompleted);
                Assert.Equal(1, File.ReadAllLines(Path.Combine(root.FullName, "calls")).Count(line => line == "auth status"));
            }
            finally { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held); }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ProbeAsync_PreCanceledRequest_StopsBeforeProbing()
    {
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger);
        var pathReads = 0;
        var fileProbes = 0;
        var locator = new GhBinaryLocator(
            runner,
            logger,
            pathProvider: () =>
            {
                pathReads++;
                return "must-not-be-read";
            },
            fileExists: _ =>
            {
                fileProbes++;
                return false;
            });
        var auth = new GhAuthService(runner, logger);
        var probe = new EnvironmentProbe(locator, auth, runner, logger, logDirectory: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(cancellation.Token));
        Assert.Equal(0, pathReads);
        Assert.Equal(0, fileProbes);
    }
}
