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
        Assert.Contains("Homebrew and WinGet scaffolding", stdout);
        Assert.Contains("automation-friendly", stdout);
        Assert.Contains("| `130` | Canceled by the caller or Ctrl+C |", stdout);
    }

    [Theory]
    [InlineData("doctor", 30)]
    [InlineData("preview", 30)]
    [InlineData("list", 120)]
    [InlineData("search", 120)]
    [InlineData("install", 600)]
    [InlineData("cleanup", 600)]
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
        Assert.Contains("2.4.17", stdout, StringComparison.Ordinal);
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
