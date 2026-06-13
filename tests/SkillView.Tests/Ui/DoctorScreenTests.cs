using System.Collections.Immutable;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DoctorScreenTests
{
    [Fact]
    public void Render_ShowsGhSkillPresent()
    {
        var report = new EnvironmentReport
        {
            GhPath = "/usr/bin/gh",
            GhVersionRaw = "gh version 2.94.0",
            GhVersion = new SemVer(2, 94, 0),
            GhMeetsMinimum = true,
            Auth = GhAuthStatus.Unknown,
            GhSkillAvailable = true,
            LogDirectory = "/tmp/skillview-logs",
        };

        var body = DoctorScreen.Render(report);

        Assert.Contains("## Environment", body);
        Assert.Contains("| gh | `/usr/bin/gh` |", body);
        Assert.Contains("| version | `gh version 2.94.0`", body);
        Assert.Contains("## `gh skill`", body);
        Assert.Contains("`gh skill` present", body);
    }

    [Fact]
    public void Render_MarksGhSkillAbsent()
    {
        var report = new EnvironmentReport
        {
            GhPath = "/usr/bin/gh",
            GhVersionRaw = "gh version 2.94.0",
            GhVersion = new SemVer(2, 94, 0),
            GhMeetsMinimum = true,
            Auth = GhAuthStatus.Unknown,
            GhSkillAvailable = false,
            LogDirectory = "/tmp/skillview-logs",
        };

        var body = DoctorScreen.Render(report);

        Assert.Contains("`gh skill` subcommand not detected", body);
    }

    [Fact]
    public void Render_FormatsDynamicTableValuesSafely()
    {
        var report = new EnvironmentReport
        {
            GhPath = "/Users/test/gh`beta`|preview",
            GhVersionRaw = "gh version 2.92.0\nbuild `123`",
            GhVersion = new SemVer(2, 92, 0),
            GhMeetsMinimum = true,
            Auth = new GhAuthStatus
            {
                LoggedIn = true,
                ActiveHost = "github.com|prod",
                Account = "octo`cat",
                Hosts = ImmutableArray.Create("github.com|prod", "github-enterprise\ninternal"),
                RawOutput = null,
            },
            GhSkillAvailable = true,
            LogDirectory = "/logs/main\nbranch|nightly",
        };

        var body = DoctorScreen.Render(report);

        Assert.Contains("| gh | `` /Users/test/gh`beta`\\|preview `` |", body);
        Assert.Contains("`` gh version 2.92.0 build `123` ``", body);
        Assert.Contains("| active | octo`cat@github.com\\|prod |", body);
        Assert.Contains("| other hosts | github.com\\|prod, github-enterprise internal |", body);
        Assert.Contains("| directory | `/logs/main branch\\|nightly` |", body);
    }
}
