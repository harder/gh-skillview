using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class RegistryRestoreAdmissionTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-registry-refusal-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temporary.Delete(true);

    private InstalledSkill Target() => new()
    {
        Name = "demo",
        ResolvedPath = Path.Combine(temporary.FullName, "skills", "demo"),
        ScanRoot = Path.Combine(temporary.FullName, "skills"),
        Scope = Scope.Custom,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.MissingSkillMd,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    private ReplacementPlanStore Store() => new(Path.Combine(temporary.FullName, "state"), new Logger(),
        runner: new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["CODEX_HOME"] = Path.Combine(temporary.FullName, ".codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(temporary.FullName, ".claude"),
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(temporary.FullName, ".claude", "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(temporary.FullName, ".pi", "agent"),
            ["XDG_CONFIG_HOME"] = Path.Combine(temporary.FullName, "config"),
            ["HOME"] = temporary.FullName,
            ["USERPROFILE"] = temporary.FullName,
            ["XDG_STATE_HOME"] = Path.Combine(temporary.FullName, "xdg"),
            ["GIT_DIR"] = Path.Combine(temporary.FullName, "absent-git"),
            ["GIT_WORK_TREE"] = temporary.FullName,
            ["GIT_CONFIG_COUNT"] = "0"
        }, defaultWorkingDirectory: temporary.FullName));

    [Theory]
    [InlineData("zero-claims")]
    [InlineData("partial")]
    [InlineData("root-incomplete")]
    [InlineData("ambiguous")]
    [InlineData("name-mismatch")]
    [InlineData("link")]
    [InlineData("diagnostic")]
    [InlineData("managed")]
    public async Task RefusesBeforeSourceOrPrivateState(string reason)
    {
        var target = Target();
        if (reason == "managed") target = target with
        {
            ResolvedPath = Path.Combine(temporary.FullName, ".codex", "skills", ".system", "demo"),
            ScanRoot = Path.Combine(temporary.FullName, ".codex", "skills", ".system")
        };
        Directory.CreateDirectory(target.ResolvedPath);
        target = reason switch
        {
            "name-mismatch" => target with { Validity = ValidityState.NameMismatch },
            "link" => target with { IsSymlinked = true },
            "diagnostic" => target with { OwnershipDiagnostics = ["unknown owner"] },
            _ => target
        };
        var inventory = InventorySnapshot.Empty with
        {
            Skills = reason == "ambiguous" ? [target, target] : [target],
            ConfiguredRootsComplete = reason != "root-incomplete",
            ScannedRoots = [new(target.ScanRoot, Scope.Custom, null)],
            ConfiguredRoots = [new(target.ScanRoot, Scope.Custom, null)],
            Diagnostics = new() { InaccessiblePaths = reason == "partial" ? ["unreadable"] : [] }
        };
        await Assert.ThrowsAsync<IOException>(() => Store().PlanRegistryRestoreAsync("must-never-launch", target, inventory, Token));
        Assert.False(Directory.Exists(Path.Combine(temporary.FullName, "state")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.ResolvedPath));
    }

    [Theory]
    [InlineData("two")]
    [InlineData("third-party")]
    [InlineData("read-only")]
    [InlineData("exclusive")]
    [InlineData("hash-kind")]
    [InlineData("short-tree")]
    [InlineData("schema")]
    [InlineData("raw-path")]
    [InlineData("selector")]
    [InlineData("diagnostic")]
    [InlineData("source-type")]
    public async Task IncompatibleClaimsCannotLaunchSourceOrCreateState(string reason)
    {
        var target = Target();
        Directory.CreateDirectory(target.ResolvedPath);
        // Synthetic claims are negative-only. Positive authority proofs use
        // actual gh installs and their untouched generated registry records.
        var claim = new SkillPackage("cli/cli", "github", "https://github.com/cli/cli.git", null, null)
        {
            Provider = "gh-vercel-global",
            SchemaVersion = 3,
            IsExclusiveOwner = false,
            SkillPath = "skills/demo/SKILL.md",
            ContentHash = new('a', 40),
            HashKind = "recorded-git-tree",
            Selector = "demo",
            CoveredRoots = [target.ScanRoot],
            LockFilePath = Path.Combine(temporary.FullName, "owner"),
            EntryFingerprint = new('b', 64),
            RecordFingerprint = new('c', 64)
        };
        claim = reason switch
        {
            "third-party" => claim with { Provider = "other" },
            "read-only" => claim with { ReadOnly = true },
            "exclusive" => claim with { IsExclusiveOwner = true },
            "hash-kind" => claim with { HashKind = "sha256" },
            "short-tree" => claim with { ContentHash = "1234" },
            "schema" => claim with { SchemaVersion = 2 },
            "raw-path" => claim with { SkillPath = "skills/demo/skill.md" },
            "selector" => claim with { Selector = "another" },
            "diagnostic" => claim with { Diagnostic = "unknown policy" },
            "source-type" => claim with { SourceType = "unknown" },
            _ => claim
        };
        target = target with { OwnershipClaims = reason == "two" ? [claim, claim] : [claim] };
        var inventory = InventorySnapshot.Empty with
        {
            Skills = [target],
            ScannedRoots = [new(target.ScanRoot, Scope.Custom, null)],
            ConfiguredRoots = [new(target.ScanRoot, Scope.Custom, null)]
        };
        await Assert.ThrowsAsync<IOException>(() => Store().PlanRegistryRestoreAsync("must-never-launch", target, inventory, Token));
        Assert.False(Directory.Exists(Path.Combine(temporary.FullName, "state")));
    }

    [Fact]
    public void PurposeAndReviewKeepObservedDamageSeparateFromDesiredTracking()
    {
        var target = Target();
        var result = SkillView.Tests.Ui.LocalReimportScreenTests.Plan() with
        {
            Purpose = ReplacementPlanStore.RegistryRestorePurpose,
            LocalImport = null,
            RegistryRestore = new(ValidityState.MissingSkillMd, new() { Name = "demo", Upstream = "https://github.com/cli/cli", GithubTreeSha = new('a', 40), GithubPath = "skills/demo" }, null, false),
            Subject = new(target.InstallationId, target.Name, target.Scope, [], target.FrontMatter, [])
        };
        Assert.False(SavedUpdateCoordinator.IsRevisionUpdate(result));
        Assert.False(SkillView.Ui.LocalReimportScreen.IsLocalReimport(result));
        var text = SkillView.Ui.UpdateScreen.RenderPlan(result);
        Assert.Contains("does not record which folder it belongs to", text);
        Assert.Contains("unknown historical ref; absent", text);
        Assert.Contains("No surviving content corroborates", text);
        Assert.Null(result.Subject.Metadata.GithubRef);
        Assert.Null(result.Subject.Metadata.GithubTreeSha);
    }
}
