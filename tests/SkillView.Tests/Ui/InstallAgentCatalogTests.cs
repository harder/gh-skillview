using System.IO;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstallAgentCatalogTests : IDisposable
{
    private readonly string _homeDirectory;

    public InstallAgentCatalogTests()
    {
        _homeDirectory = Path.Combine(Path.GetTempPath(), "skillview-agent-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_homeDirectory);
    }

    [Fact]
    public void DetectInstalledGhIds_UsesAgentHomeDirectories_NotSkillsDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_homeDirectory, ".copilot"));

        var found = InstallAgentCatalog.DetectInstalledGhIds(_homeDirectory);

        Assert.Contains("github-copilot", found);
    }

    [Fact]
    public void DetectInstalledDisplayEntries_ReportAgentHomePath()
    {
        var antigravityHome = Path.Combine(_homeDirectory, ".gemini", "antigravity");
        Directory.CreateDirectory(antigravityHome);

        var found = InstallAgentCatalog.DetectInstalledDisplayEntries(_homeDirectory);

        Assert.Contains(found, entry => entry.Label == "Antigravity" && entry.Path == antigravityHome);
    }

    [Fact]
    public void DetectInstalledGhIds_UsesPiConfigurationOverride()
    {
        var piConfig = Path.Combine(_homeDirectory, "custom-pi-config");
        Directory.CreateDirectory(piConfig);

        var found = InstallAgentCatalog.DetectInstalledGhIds(_homeDirectory, piConfig);

        Assert.Contains("pi", found);
    }

    [Fact]
    public void DetectInstalledGhIds_UsesPiDefaultConfigurationDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_homeDirectory, ".pi", "agent"));

        var found = InstallAgentCatalog.DetectInstalledGhIds(_homeDirectory);

        Assert.Contains("pi", found);
    }

    [Fact]
    public void DetectInstalledDisplayEntries_ReportsPiConfigurationOverride()
    {
        var piConfig = Path.Combine(_homeDirectory, "custom-pi-config");
        Directory.CreateDirectory(piConfig);

        var found = InstallAgentCatalog.DetectInstalledDisplayEntries(_homeDirectory, piConfig);

        Assert.Contains(found, entry => entry.Label == "Pi" && entry.Path == piConfig);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_homeDirectory, recursive: true);
        }
        catch
        {
        }
    }
}
