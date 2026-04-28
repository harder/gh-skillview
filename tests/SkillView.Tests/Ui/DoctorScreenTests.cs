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

        Assert.Contains("**preview**", body);
        Assert.Contains("`--allow-hidden-dirs`  ✅", body);
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

        Assert.Contains("**preview**", body);
        Assert.Contains("`--allow-hidden-dirs`  ❌", body);
    }
}
