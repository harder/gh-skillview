using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory.Models;
using SkillView.Preferences;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class DesiredPolicyPresentationTests
{
    [Fact]
    public void IntentRemainsSeparateFromActualPinAndRuntimeInDetailAndListJson()
    {
        var target = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.Custom,
            Agents = [],
            FrontMatter = SkillFrontMatter.Empty with { Pinned = true, PinnedRef = "v1", GithubRef = "refs/tags/v1" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            DesiredPolicy = new("active", "Desired intent is separate; session is unobserved.", new("exact-pin", "v2"), new string('a', 64))
        };
        var detail = InstalledScreen.RenderDetail(target);
        Assert.Contains("**Desired revision policy:** `exact-pin`", detail);
        Assert.Contains("**Desired ref:** `v2`", detail);
        Assert.Contains("**Pinned ref:** `v1`", detail);
        Assert.Contains("active-session loading", detail);
        using var document = JsonDocument.Parse(CliDispatcher.RenderListJson(InventorySnapshot.Empty with { Skills = [target] }));
        var row = Assert.Single(document.RootElement.GetProperty("skills").EnumerateArray());
        Assert.Equal("v1", row.GetProperty("pinnedRef").GetString());
        Assert.True(row.GetProperty("pinned").GetBoolean());
        var policy = row.GetProperty("desiredRevisionPolicy");
        Assert.Equal("v2", policy.GetProperty("intent").GetProperty("requestedRef").GetString());
        Assert.Equal("unknown", policy.GetProperty("runtimeState").GetString());
        Assert.Equal("active", policy.GetProperty("status").GetString());
    }
}
