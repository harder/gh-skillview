using System.Collections.Immutable;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DoctorScreenTests
{
    [Fact]
    public void Render_ShowsPreviewHiddenDirCapability()
    {
        var report = new EnvironmentReport
        {
            GhPath = "/usr/bin/gh",
            GhVersionRaw = "gh version 2.91.0",
            GhVersion = new SemVer(2, 91, 0),
            GhMeetsMinimum = true,
            Auth = GhAuthStatus.Unknown,
            Capabilities = CapabilityProfile.Empty with
            {
                SkillSubcommandPresent = true,
                PreviewFlags = ImmutableHashSet.Create("--allow-hidden-dirs"),
            },
            LogDirectory = "/tmp/skillview-logs",
        };

        var body = DoctorScreen.Render(report);

        Assert.Contains("## Environment", body);
        Assert.Contains("| Item | Value |", body);
        Assert.Contains("| gh | `/usr/bin/gh` |", body);
        Assert.Contains("| version | `gh version 2.91.0`", body);
        Assert.Contains("### preview", body);
        Assert.Contains("| Flag | Supported |", body);
        Assert.Contains("| `--allow-hidden-dirs` | ✅ |", body);
    }

    [Fact]
    public void Render_MarksPreviewHiddenDirCapabilityAbsent()
    {
        var report = new EnvironmentReport
        {
            GhPath = "/usr/bin/gh",
            GhVersionRaw = "gh version 2.91.0",
            GhVersion = new SemVer(2, 91, 0),
            GhMeetsMinimum = true,
            Auth = GhAuthStatus.Unknown,
            Capabilities = CapabilityProfile.Empty with { SkillSubcommandPresent = true },
            LogDirectory = "/tmp/skillview-logs",
        };

        var body = DoctorScreen.Render(report);

        Assert.Contains("### preview", body);
        Assert.Contains("| `--allow-hidden-dirs` | ❌ |", body);
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
            Capabilities = CapabilityProfile.Empty with { SkillSubcommandPresent = true },
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
