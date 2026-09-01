using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Environment;

public sealed class EnvironmentProbeTests
{
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
