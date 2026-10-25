using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DisplayPathTests
{
    [Fact]
    public void UnknownCapturedHomeDoesNotAbbreviateTheAmbientUserProfile()
    {
        var runner = new SkillView.Subprocess.ProcessRunner(new SkillView.Logging.Logger())
            .WithIsolatedContext(Path.GetFullPath("isolated-child"), new Dictionary<string, string>());
        Assert.Null(DisplayPath.CaptureHome(runner));
        var path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "skills", "demo");
        Assert.Equal(path, DisplayPath.Abbreviate(path, DisplayPath.CaptureHome(runner)));
    }

    [Theory]
    [InlineData("/Users/alex/skills/demo", "/Users/alex", false, "~/skills/demo")]
    [InlineData("/home/alex", "/home/alex/", false, "~")]
    [InlineData("/home/alex/", "/home/alex/", false, "~/")]
    [InlineData("/home/alexander/demo", "/home/alex", false, "/home/alexander/demo")]
    [InlineData("/home/Alex/demo", "/home/alex", false, "/home/Alex/demo")]
    [InlineData("relative/demo", "/home/alex", false, "relative/demo")]
    [InlineData("https://example.test/home/alex/demo", "/home/alex", false, "https://example.test/home/alex/demo")]
    [InlineData("C:\\Users\\Alex\\skills\\demo", "C:\\Users\\Alex", true, "~\\skills\\demo")]
    [InlineData("c:/users/alex/skills/demo", "C:\\Users\\Alex\\", true, "~/skills/demo")]
    [InlineData("C:\\Users\\Alexis\\demo", "C:\\Users\\Alex", true, "C:\\Users\\Alexis\\demo")]
    [InlineData("D:\\Users\\Alex\\demo", "C:\\Users\\Alex", true, "D:\\Users\\Alex\\demo")]
    [InlineData("\\\\server\\users\\alex\\demo", "\\\\server\\users\\alex", true, "~\\demo")]
    [InlineData("/home/alex/demo", "", false, "/home/alex/demo")]
    [InlineData("/home/alex/demo", "/", false, "/home/alex/demo")]
    [InlineData("C:\\demo", "C:\\", true, "C:\\demo")]
    [InlineData("", "/home/alex", false, "")]
    public void Abbreviate_OnlyCurrentHomeAtDirectoryBoundaries(string path, string home, bool windows, string expected)
        => Assert.Equal(expected, DisplayPath.Abbreviate(path, home, windows));
}
