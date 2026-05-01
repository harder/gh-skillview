using SkillView.Bootstrapping;
using Xunit;

namespace SkillView.Tests.Bootstrapping;

public class ArgParserTests
{
    [Fact]
    public void ExtensionBinaryNameIsDetected()
    {
        var opts = ArgParser.Parse("/usr/local/bin/gh-skillview", Array.Empty<string>());
        Assert.Equal(InvocationMode.GhExtension, opts.InvocationMode);
    }

    [Fact]
    public void StandaloneBinaryNameIsDetected()
    {
        var opts = ArgParser.Parse("/usr/local/bin/skillview", Array.Empty<string>());
        Assert.Equal(InvocationMode.Standalone, opts.InvocationMode);
    }

    [Fact]
    public void NoSubcommandLandsOnTui()
    {
        var opts = ArgParser.Parse("skillview", new[] { "--debug" });
        Assert.Equal(DispatchMode.Tui, opts.DispatchMode);
        Assert.True(opts.Debug);
    }

    [Fact]
    public void SubcommandLandsOnCli()
    {
        var opts = ArgParser.Parse("skillview", new[] { "doctor", "--json" });
        Assert.Equal(DispatchMode.Cli, opts.DispatchMode);
        Assert.Equal("doctor", opts.SubcommandName);
        Assert.Contains("--json", opts.SubcommandArgs);
    }

    [Fact]
    public void ScanRootFlagRepeatable()
    {
        var opts = ArgParser.Parse(
            "skillview",
            new[] { "--scan-root", "/a/b", "--scan-root=/c/d" });
        Assert.Equal(new[] { "/a/b", "/c/d" }, opts.ScanRoots);
    }

    [Fact]
    public void ScanRootWithoutValueThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            ArgParser.Parse("skillview", new[] { "--scan-root" }));
    }

    [Fact]
    public void GlobalFlagsBeforeSubcommandAreConsumed()
    {
        var opts = ArgParser.Parse(
            "skillview",
            new[] { "--debug", "--scan-root=/x", "list", "--json" });
        Assert.True(opts.Debug);
        Assert.Equal(new[] { "/x" }, opts.ScanRoots);
        Assert.Equal("list", opts.SubcommandName);
        Assert.Equal(new[] { "--json" }, opts.SubcommandArgs);
    }

    [Fact]
    public void DebugFlagRecognisedAfterSubcommand()
    {
        // `--debug` is a global flag that works on any subcommand.
        // It's stripped from the subcommand payload so downstream parsers
        // don't have to know about it.
        var opts = ArgParser.Parse(
            "skillview",
            new[] { "list", "--json", "--debug" });
        Assert.True(opts.Debug);
        Assert.Equal("list", opts.SubcommandName);
        Assert.Equal(new[] { "--json" }, opts.SubcommandArgs);
    }

    [Fact]
    public void ThemeFlagBeforeSubcommandIsConsumed()
    {
        var opts = ArgParser.Parse(
            "skillview",
            new[] { "--theme", "high-contrast", "list", "--json" });

        Assert.Equal(AppTheme.HighContrast, opts.Theme);
        Assert.Equal("list", opts.SubcommandName);
        Assert.Equal(new[] { "--json" }, opts.SubcommandArgs);
    }

    [Fact]
    public void ThemeDefaultsFromEnvironment()
    {
        var previous = System.Environment.GetEnvironmentVariable("SKILLVIEW_THEME");
        try
        {
            System.Environment.SetEnvironmentVariable("SKILLVIEW_THEME", "high-contrast");

            var opts = ArgParser.Parse("skillview", Array.Empty<string>());

            Assert.Equal(AppTheme.HighContrast, opts.Theme);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SKILLVIEW_THEME", previous);
        }
    }

    [Fact]
    public void ScanRootAfterSubcommandIsNotConsumedAsGlobal()
    {
        // Only `--debug` is recognised post-subcommand. `--scan-root` is a
        // global that must precede the subcommand; if it appears later it
        // stays in the payload (and the subcommand parser may reject it).
        var opts = ArgParser.Parse(
            "skillview",
            new[] { "list", "--scan-root", "/x" });
        Assert.Empty(opts.ScanRoots);
        Assert.Equal(new[] { "--scan-root", "/x" }, opts.SubcommandArgs);
    }
}
