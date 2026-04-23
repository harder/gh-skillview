using System.Collections.Immutable;
using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class GhSkillUpdateServiceTests
{
    private static CapabilityProfile CapsWith(params string[] updateFlags)
    {
        return CapabilityProfile.Empty with
        {
            SkillSubcommandPresent = true,
            UpdateFlags = ImmutableHashSet.CreateRange(updateFlags),
        };
    }

    [Fact]
    public void BuildArgs_MinimalNoOptions()
    {
        var args = GhSkillUpdateService.BuildArgs(
            CapsWith(), new GhSkillUpdateService.Options());
        Assert.Equal(new[] { "skill", "update" }, args);
    }

    [Fact]
    public void BuildArgs_SkillsAppendedAsPositionals()
    {
        var args = GhSkillUpdateService.BuildArgs(
            CapsWith(),
            new GhSkillUpdateService.Options(Skills: new[] { "render-md", "fetch-url" }));
        Assert.Equal(new[] { "skill", "update", "render-md", "fetch-url" }, args);
    }

    [Fact]
    public void BuildArgs_EmptySkillsAreSkipped()
    {
        var args = GhSkillUpdateService.BuildArgs(
            CapsWith(),
            new GhSkillUpdateService.Options(Skills: new[] { "", "  ", "real-one" }));
        Assert.Equal(new[] { "skill", "update", "real-one" }, args);
    }

    [Fact]
    public void BuildArgs_AllRequiresCapability()
    {
        var missing = GhSkillUpdateService.BuildArgs(
            CapsWith(), new GhSkillUpdateService.Options(All: true));
        Assert.DoesNotContain("--all", missing);

        var present = GhSkillUpdateService.BuildArgs(
            CapsWith("--all"), new GhSkillUpdateService.Options(All: true));
        Assert.Contains("--all", present);
    }

    [Fact]
    public void BuildArgs_DryRunRequiresCapability()
    {
        var missing = GhSkillUpdateService.BuildArgs(
            CapsWith(), new GhSkillUpdateService.Options(DryRun: true));
        Assert.DoesNotContain("--dry-run", missing);

        var present = GhSkillUpdateService.BuildArgs(
            CapsWith("--dry-run"), new GhSkillUpdateService.Options(DryRun: true));
        Assert.Contains("--dry-run", present);
    }

    [Fact]
    public void BuildArgs_ForceAndUnpinAreCapabilityGated()
    {
        var off = GhSkillUpdateService.BuildArgs(
            CapsWith(),
            new GhSkillUpdateService.Options(Force: true, Unpin: true));
        Assert.DoesNotContain("--force", off);
        Assert.DoesNotContain("--unpin", off);

        var on = GhSkillUpdateService.BuildArgs(
            CapsWith("--force", "--unpin"),
            new GhSkillUpdateService.Options(Force: true, Unpin: true));
        Assert.Contains("--force", on);
        Assert.Contains("--unpin", on);
    }

    [Fact]
    public void BuildArgs_YesPrefersYesTokenWhenPresent()
    {
        var bothPresent = GhSkillUpdateService.BuildArgs(
            CapsWith("--yes", "--non-interactive"),
            new GhSkillUpdateService.Options(Yes: true));
        Assert.Contains("--yes", bothPresent);
        Assert.DoesNotContain("--non-interactive", bothPresent);
    }

    [Fact]
    public void BuildArgs_YesFallsBackToNonInteractive()
    {
        var nonInteractiveOnly = GhSkillUpdateService.BuildArgs(
            CapsWith("--non-interactive"),
            new GhSkillUpdateService.Options(Yes: true));
        Assert.Contains("--non-interactive", nonInteractiveOnly);
        Assert.DoesNotContain("--yes", nonInteractiveOnly);
    }

    [Fact]
    public void BuildArgs_YesDroppedWhenNoCapability()
    {
        // Without the probe confirming --yes/--non-interactive, the adapter
        // must not append either token — PRD §7.1.E hang-on-prompt guard.
        var args = GhSkillUpdateService.BuildArgs(
            CapsWith(), new GhSkillUpdateService.Options(Yes: true));
        Assert.DoesNotContain("--yes", args);
        Assert.DoesNotContain("--non-interactive", args);
    }

    [Fact]
    public void BuildArgs_JsonRequiresCapability()
    {
        var missing = GhSkillUpdateService.BuildArgs(
            CapsWith(), new GhSkillUpdateService.Options(Json: true));
        Assert.DoesNotContain("--json", missing);

        var present = GhSkillUpdateService.BuildArgs(
            CapsWith("--json"), new GhSkillUpdateService.Options(Json: true));
        Assert.Contains("--json", present);
    }

    [Fact]
    public void BuildArgs_AllFlagsCombined()
    {
        var args = GhSkillUpdateService.BuildArgs(
            CapsWith("--all", "--dry-run", "--force", "--unpin", "--yes", "--json"),
            new GhSkillUpdateService.Options(
                Skills: new[] { "s1" },
                All: true, DryRun: true, Force: true, Unpin: true, Yes: true, Json: true));
        Assert.Equal(
            new[] { "skill", "update", "--all", "--dry-run", "--force", "--unpin", "--yes", "--json", "s1" },
            args);
    }

    [Fact]
    public void ParseEntries_UpdatedArrow()
    {
        var entries = GhSkillUpdateService.ParseEntries(
            "Updating render-md from v1.0.0 → v1.1.0\n");
        Assert.Single(entries);
        Assert.Equal("render-md", entries[0].Name);
        Assert.Equal("v1.0.0", entries[0].FromVersion);
        Assert.Equal("v1.1.0", entries[0].ToVersion);
        Assert.Equal("updated", entries[0].Status);
    }

    [Fact]
    public void ParseEntries_UpToDateAndPinned()
    {
        var entries = GhSkillUpdateService.ParseEntries(
            "render-md: up-to-date\nfetch-url: pinned\nold-thing: skipped\n");
        Assert.Equal(3, entries.Length);
        Assert.Equal("up-to-date", entries[0].Status);
        Assert.Equal("pinned", entries[1].Status);
        Assert.Equal("skipped", entries[2].Status);
    }

    [Fact]
    public void ParseEntries_EmptyStdoutIsEmpty()
    {
        Assert.Empty(GhSkillUpdateService.ParseEntries(""));
        Assert.Empty(GhSkillUpdateService.ParseEntries("   \n\n"));
    }
}
