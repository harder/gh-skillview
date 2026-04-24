using System.Runtime.InteropServices;
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
}
