using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstallScreenTests
{
    [Fact]
    public void KnownAgents_UseCurrentGhAgentIds()
    {
        Assert.Contains("claude-code", InstallScreen.KnownAgents);
        Assert.Contains("github-copilot", InstallScreen.KnownAgents);
        Assert.Contains("gemini-cli", InstallScreen.KnownAgents);
    }
}
