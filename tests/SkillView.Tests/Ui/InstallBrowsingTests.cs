using SkillView.Gh;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstallBrowsingTests
{
    [Fact]
    public void CollapsingMoreKeepsSelectedAgentsAndDetectedFirst()
    {
        using var view = new View();
        var selection = new InstallAgentSelection(view);
        selection.SetDetected(new HashSet<string> { "codex" });
        var entries = InstallAgentCatalog.Entries;
        var codex = selection.Boxes[entries.IndexOf(entries.Single(entry => entry.GhId == "codex"))];
        var grok = selection.Boxes[entries.IndexOf(entries.Single(entry => entry.GhId == "grok"))];
        Assert.True(codex.Visible);
        Assert.False(grok.Visible);
        Assert.Equal(["codex"], selection.Selected);
        selection.More.InvokeCommand(Command.Accept);
        Assert.True(grok.Visible);
        grok.Value = CheckState.Checked;
        var selected = selection.Selected.ToArray();
        selection.More.InvokeCommand(Command.Accept);
        Assert.True(grok.Visible);
        Assert.Equal(selected, selection.Selected);
        Assert.Equal("Absolute(1)", codex.Y.ToString());
        Assert.NotEqual(codex.Y.ToString(), grok.Y.ToString());
        Assert.Contains(selection.Boxes, box => box.Visible && box.Text == "Universal");
    }

    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    [InlineData("custom")]
    public async Task PlacementListsExactCatalogTargetsAndDoesNotCreateThem(string scope)
    {
        using var fixture = new Fixture();
        var options = new GhSkillInstallService.Options(Agents: ["universal", "claude-code"], Scope: scope,
            Path: scope == "custom" ? "relative-destination" : null, Version: null);
        var observed = await InstallPlacementReview.CaptureAsync(fixture.Runner, fixture.Logger, fixture.Gh,
            "fixture/repo", "demo", options, TestContext.Current.CancellationToken);
        var expected = await GhPlacementCatalog.DestinationsAsync(options with { WorkingDirectory = fixture.Work }, fixture.Runner,
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, observed.Roots);
        foreach (var root in expected)
        {
            Assert.Contains(Path.Combine(root, "demo"), observed.Detail);
            Assert.False(Directory.Exists(root));
        }
        Assert.Contains("Version: gh default (not resolved)", observed.Detail);
        Assert.Contains("Guidance only", observed.Detail);
        Assert.Equal(InstallNavigationEvidence.Context(fixture.Runner), observed.ContextKey);
    }

    [Fact]
    public async Task ConflictIsVisibleAndSharedSelectorStillRefusesLaunch()
    {
        using var fixture = new Fixture();
        var root = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(Path.Combine(root, "demo"));
        File.WriteAllText(Path.Combine(fixture.Home, ".agents", ".skill-lock.json"),
            """{"version":3,"skills":{"demo":{"source":"fixture/repo","sourceType":"github","sourceUrl":"https://github.com/fixture/repo","skillPath":"skills/demo","skillFolderHash":"tree"}}}""");
        var options = new GhSkillInstallService.Options(Agents: ["universal"], Scope: "user");
        var review = await InstallPlacementReview.CaptureAsync(fixture.Runner, fixture.Logger, fixture.Gh,
            "fixture/repo", "demo", options, TestContext.Current.CancellationToken);
        Assert.Contains("Existing object: conflict", review.Detail);
        Assert.Contains("shared selector", review.Detail);
        var result = await new GhSkillInstallService(fixture.Runner, fixture.Logger).InstallAsync(fixture.Gh,
            "fixture/repo", "demo", options, TestContext.Current.CancellationToken);
        Assert.Equal(SkillView.Gh.Models.InstallAttemptOutcome.RefusedPreflight, result.Attempt);
        Assert.Contains("shared selector", result.ErrorMessage);
        Assert.False(File.Exists(fixture.Mutation));
    }

    [Fact]
    public async Task AllAndUnresolvedNamesStayUnknownAndReviewedContextCanOnlyRestrict()
    {
        using var fixture = new Fixture();
        var options = new GhSkillInstallService.Options(Agents: ["universal"], Scope: "user", All: true);
        var all = await InstallPlacementReview.CaptureAsync(fixture.Runner, fixture.Logger, fixture.Gh,
            "fixture/repo", null, options, TestContext.Current.CancellationToken);
        Assert.Contains("Exact target set unknown", all.Detail);
        Assert.DoesNotContain("Named-skill destinations", all.Detail);
        var unknown = await InstallPlacementReview.CaptureAsync(fixture.Runner, fixture.Logger, fixture.Gh,
            "fixture/repo", "different", options with { All = false }, TestContext.Current.CancellationToken);
        Assert.Contains("unresolved or ambiguous", unknown.Detail);
        var refused = await new GhSkillInstallService(fixture.Runner, fixture.Logger).InstallAsync(fixture.Gh,
            "fixture/repo", "demo", options with { All = false, ReviewedDestinations = all.Roots, ReviewedContext = "changed" },
            TestContext.Current.CancellationToken);
        Assert.Equal(SkillView.Gh.Models.InstallAttemptOutcome.RefusedPreflight, refused.Attempt);
        Assert.Contains("reviewed placement", refused.ErrorMessage);
        Assert.False(File.Exists(fixture.Mutation));
    }

    [Theory]
    [InlineData("other/repo", "skills/demo", false)]
    [InlineData("owner/repo", "skills/other", false)]
    [InlineData("OWNER/REPO", "skills/demo/SKILL.md", true)]
    [InlineData("owner/repo", null, false)]
    [InlineData("owner/repo", "", false)]
    public void InstalledMarkerUsesRepositoryAndActualSubpath(string repo, string? path, bool expected)
    {
        var skill = Skill("https://github.com/owner/repo.git", "skills/demo");
        var snapshot = InventorySnapshot.Empty with { Skills = [skill], ConfiguredRootsComplete = false };
        var observation = new DiscoverInstalledObservation(snapshot);
        var result = new SkillView.Gh.Models.SearchResultSkill(null, null, path, repo, "same-authored-name", 1);
        Assert.Equal(expected, observation.IsObserved(result));
        Assert.Contains("coverage is incomplete", observation.Describe(result));
        if (path is null) Assert.Contains("no trustworthy source path", observation.Describe(result));
        Assert.DoesNotContain("not installed", observation.Describe(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConflictingOwnerSourceNeverMarksAuthoredSourceInstalled()
    {
        var skill = Skill("https://github.com/owner/repo", "skills/demo") with
        {
            OwnershipClaims = [new("other/repo", "github", null, null, null) { SkillPath = "skills/demo" }],
        };
        var observed = new DiscoverInstalledObservation(InventorySnapshot.Empty with { Skills = [skill] });
        Assert.False(observed.IsObserved(new(null, null, "skills/demo", "owner/repo", skill.Name, 1)));
    }

    [Fact]
    public async Task InvalidationRetiresMarkerWithoutAnotherInventoryCall()
    {
        long generation = 1;
        var captures = 0;
        var cache = new DisplayInventoryCache(() => generation, _ => { captures++; return Task.FromResult(InventorySnapshot.Empty); }, (_, _) => false);
        cache.PublishFresh(InventorySnapshot.Empty with { Skills = [Skill("https://github.com/owner/repo", "skills/demo")] }, generation);
        Assert.True(cache.Observation!.IsObserved(new(null, null, "skills/demo", "owner/repo", "different-name", 0)));
        generation++;
        Assert.Null(cache.Observation);
        Assert.Equal(0, captures);
        await Task.CompletedTask;
    }

    private static InstalledSkill Skill(string repo, string path) => new()
    {
        Name = "same-authored-name",
        ResolvedPath = Path.Combine(Path.GetTempPath(), "observed-demo"),
        ScanRoot = Path.GetTempPath(),
        Scope = Scope.User,
        Agents = [],
        FrontMatter = new() { Upstream = repo, GithubPath = path },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("install-browsing-").FullName;
        internal string Home => Path.Combine(_root, "home");
        internal string Work => Path.Combine(_root, "work");
        internal string Gh => Path.Combine(_root, "gh");
        internal string Mutation => Path.Combine(_root, "mutation");
        internal Logger Logger { get; } = new(LogLevel.Debug);
        internal ProcessRunner Runner { get; }
        internal Fixture()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Credential-free shell fixture runs on Unix.");
            Directory.CreateDirectory(Home); Directory.CreateDirectory(Work);
            File.WriteAllText(Gh, "#!/bin/sh\ncase \"$*\" in 'skill install -- fixture/repo') printf 'demo\\tFixture\\n';; *) printf 'unexpected command' >&2; exit 9;; esac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Runner = new ProcessRunner(Logger).WithIsolatedContext(Work, new Dictionary<string, string>
            {
                ["HOME"] = Home,
                ["USERPROFILE"] = Home,
                ["PATH"] = "/usr/bin:/bin",
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
                ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
                ["CODEX_HOME"] = Path.Combine(Home, ".codex"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(Home, ".claude"),
            });
        }
        public void Dispose() { Directory.Delete(_root, true); }
    }
}
