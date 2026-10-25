using System.IO;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public class CliDispatcherHelpTests
{
    [Fact]
    public async Task HelpFlag_PrintsMarkdownHelp()
    {
        var (exitCode, stdout) = await RunCliAsync("skillview", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("# SkillView", stdout);
        Assert.Contains("SkillView complements `gh skill`", stdout);
        Assert.Contains("## Usage", stdout);
        Assert.Contains("| Global flag | What it does |", stdout);
        Assert.Contains("| Subcommand | Purpose |", stdout);
        Assert.Contains("| `list` | Show installed skills from the filesystem and, when supported, `gh skill list`. | `--json`, `--scope`, `--agent`, `--dir`, `--allow-hidden-dirs` |", stdout);
        Assert.DoesNotContain("`--json`, `--scope`, `--agent`, `--path`, `--allow-hidden-dirs`", stdout);
        Assert.Contains("brew install harder/tap/skillview", stdout);
        Assert.Contains("WinGet manifests are generated for release review", stdout);
        Assert.Contains("automation-friendly", stdout);
        Assert.Contains("| `authoring-validate` |", stdout);
        Assert.Contains("--repository PATH", stdout);
        Assert.Contains("| `quarantine` |", stdout);
        Assert.Contains("| `restore-plan` |", stdout);
        Assert.Contains("| `update-preserve` |", stdout);
        Assert.DoesNotContain("update-merge-review", stdout);
        Assert.DoesNotContain("update-merge-choose", stdout);
        Assert.DoesNotContain("update-merge-plan", stdout);
        Assert.Contains("| `unpin-plan` |", stdout);
        Assert.Contains("| `reimport-plan` |", stdout);
        Assert.Contains("| `source-switch-plan` |", stdout);
        Assert.Contains("--new-source <absolute directory>", stdout);
        Assert.Contains("--disposition replace-local-source-retain-original", stdout);
        Assert.Contains("--disposition replace-local-retain-original", stdout);
        Assert.Contains("--disposition unpin-only", stdout);
        Assert.Contains("| `restore-inspect` |", stdout);
        Assert.Contains("| `restore-apply` |", stdout);
        Assert.Contains("| `undo` |", stdout);
        Assert.Contains("| `quarantine-history` |", stdout);
        Assert.Contains("SkillView requires `gh` 2.97.0 or newer", stdout);
        Assert.DoesNotContain("capability-gated flags", stdout);
        Assert.Contains("| `130` | Canceled by the caller or Ctrl+C |", stdout);
    }

    [Theory]
    [InlineData("doctor", 30)]
    [InlineData("preview", 30)]
    [InlineData("list", 120)]
    [InlineData("authoring-validate", 120)]
    [InlineData("search", 120)]
    [InlineData("install", 600)]
    [InlineData("cleanup", 600)]
    [InlineData("quarantine", 600)]
    [InlineData("undo", 600)]
    [InlineData("quarantine-history", 600)]
    [InlineData("restore-plan", 600)]
    [InlineData("restore-inspect", 600)]
    [InlineData("restore-apply", 600)]
    [InlineData("restore-recover", 600)]
    [InlineData("restore-undo", 600)]
    [InlineData("update-plan", 600)]
    [InlineData("update-inspect", 600)]
    [InlineData("update-ack-source", 600)]
    [InlineData("reimport-ack-source", 600)]
    [InlineData("restore-ack-source", 600)]
    [InlineData("update-apply", 600)]
    [InlineData("update-recover", 600)]
    [InlineData("update-undo", 600)]
    [InlineData("update-repair-owner", 600)]
    [InlineData("source-switch-plan", 600)]
    [InlineData("source-switch-inspect", 600)]
    [InlineData("source-switch-apply", 600)]
    [InlineData("source-switch-recover", 600)]
    [InlineData("source-switch-undo", 600)]
    [InlineData("reimport-plan", 600)]
    [InlineData("reimport-inspect", 600)]
    [InlineData("reimport-apply", 600)]
    [InlineData("reimport-recover", 600)]
    [InlineData("reimport-undo", 600)]
    [InlineData("unpin-plan", 600)]
    [InlineData("unpin-inspect", 600)]
    [InlineData("unpin-apply", 600)]
    [InlineData("unpin-recover", 600)]
    [InlineData("unpin-undo", 600)]
    [InlineData("unpin-repair-owner", 600)]
    [InlineData("update-preserve", 600)]
    public void SubcommandTimeouts_AreBoundedByOperationCost(string subcommand, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), CliDispatcher.TimeoutFor(subcommand));
    }

    [Fact]
    public async Task HelpFlag_UsesExtensionCommandNameWhenInvokedAsGhExtension()
    {
        var (exitCode, stdout) = await RunCliAsync("gh-skillview", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("`gh skillview`", stdout);
        Assert.Contains("gh skillview search terraform", stdout);
    }

    [Fact]
    public async Task VersionFlag_UsesExtensionCommandNameWhenInvokedAsGhExtension()
    {
        var (exitCode, stdout) = await RunCliAsync("gh-skillview", "--version");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith("gh skillview ", stdout.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionFlag_IncludesTerminalGuiVersion()
    {
        var (exitCode, stdout) = await RunCliAsync("skillview", "--version");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Terminal.Gui", stdout, StringComparison.Ordinal);
        Assert.Contains("2.5.0", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCanceledHelp_DoesNotPrintSuccessOutput()
    {
        await CliConsoleCapture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var options = ArgParser.Parse("skillview", ["--help"]);
            var services = TuiServices.Build(new Logger(LogLevel.Info));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CliDispatcher.RunAsync(options, services, cancellation.Token));
            Assert.Empty(writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            CliConsoleCapture.Gate.Release();
        }
    }

    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(string processPath, params string[] args)
    {
        await CliConsoleCapture.Gate.WaitAsync().ConfigureAwait(false);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var options = ArgParser.Parse(processPath, args);
            var services = TuiServices.Build(new Logger(LogLevel.Info));
            var exitCode = await CliDispatcher.RunAsync(options, services).ConfigureAwait(false);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            CliConsoleCapture.Gate.Release();
        }
    }
}
