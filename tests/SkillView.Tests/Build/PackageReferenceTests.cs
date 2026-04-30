using System.IO;
using Xunit;

namespace SkillView.Tests.Build;

public sealed class PackageReferenceTests
{
    [Fact]
    public void SkillViewCore_UsesTerminalGuiV2()
    {
        var text = ReadProjectFile("src", "SkillView.Core", "SkillView.Core.csproj");

        Assert.Contains("""<PackageReference Include="Terminal.Gui" Version="2.0.1" />""", text);
    }

    [Theory]
    [InlineData("src", "SkillView.App", "SkillView.App.csproj")]
    [InlineData("src", "SkillView.GhExtension", "SkillView.GhExtension.csproj")]
    public void ExecutableProjects_DoNotRootTerminalGuiAssembly(params string[] pathParts)
    {
        var text = ReadProjectFile(pathParts);

        Assert.DoesNotContain("""<TrimmerRootAssembly Include="Terminal.Gui" />""", text);
    }

    private static string ReadProjectFile(params string[] pathParts)
    {
        var repoRoot = FindRepoRoot();
        var projectFile = Path.Combine([repoRoot.FullName, .. pathParts]);
        return File.ReadAllText(projectFile);
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SkillView.sln")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find SkillView.sln from test base directory.");
    }
}
