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
        Assert.Contains("## Usage", stdout);
        Assert.Contains("| Global flag | What it does |", stdout);
        Assert.Contains("| Subcommand | Purpose |", stdout);
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
        Assert.Contains("2.0.2", stdout, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(string processPath, params string[] args)
    {
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
        }
    }
}
