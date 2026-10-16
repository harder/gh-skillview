using SkillView.Cli;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class CliDispatcherIgnoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--path")]
    [InlineData("--path a --install-id b")]
    [InlineData("--path a --clear --clear")]
    [InlineData("--path a --json --json")]
    [InlineData("--all")]
    public void InvalidSelectorsAreRefused(string arguments) =>
        Assert.NotNull(CliDispatcher.ParseIgnoreArgs(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Error);

    [Fact]
    public void ExactSelectorAndClearAreAccepted()
    {
        var parsed = CliDispatcher.ParseIgnoreArgs(["--install-id", "exact", "--clear", "--json"]);
        Assert.Null(parsed.Error); Assert.Equal("exact", parsed.InstallId); Assert.True(parsed.Clear); Assert.True(parsed.Json);
    }

    [Fact]
    public void AgentSymlinkAliasResolvesToThePhysicalInstallation()
    {
        using var fixture = new AliasFixture();
        Assert.True(OwnershipPath.Same(fixture.Target, CliDispatcher.ResolveIgnorePath(fixture.Alias)));
    }

    private sealed class AliasFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-ignore-alias-");
        internal string Target { get; }
        internal string Alias { get; }
        internal AliasFixture()
        {
            Target = Path.Combine(_root.FullName, "physical"); Alias = Path.Combine(_root.FullName, "agent");
            Directory.CreateDirectory(Target); Directory.CreateSymbolicLink(Alias, Target);
        }
        public void Dispose() { Directory.Delete(Alias); _root.Delete(true); }
    }
}
