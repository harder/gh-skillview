using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Gh;

public sealed class GhAdapterMetadataAdmissionTests
{
    [Theory]
    [InlineData("captured-validity")]
    [InlineData("captured-conflict")]
    [InlineData("captured-owner")]
    [InlineData("captured-diagnostic")]
    [InlineData("fresh-diagnostic")]
    public async Task RealUpdateRejectsInvalidEvidenceEvenWhenTrackingValuesStillMatch(string kind)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX adapter recorder; no Windows child execution proof."); return; }
        using var fixture = new Fixture();
        var target = fixture.Target;
        target = kind switch
        {
            "captured-validity" => target with { Validity = ValidityState.InvalidMetadata },
            "captured-conflict" => target with { MetadataConflicts = ["Conflicting catalog tracking."] },
            "captured-owner" => target with { OwnershipDiagnostics = ["Owner metadata could not be read."] },
            "captured-diagnostic" => target with { FrontMatter = target.FrontMatter with { Diagnostics = ["Invalid metadata observation."] } },
            _ => target
        };
        if (kind == "fresh-diagnostic")
        {
            fixture.WriteMetadata("  custom: [value]\n");
            var fresh = fixture.Target;
            Assert.NotEmpty(fresh.FrontMatter.Diagnostics);
            Assert.Equal(target.FrontMatter.Upstream, fresh.FrontMatter.Upstream);
            Assert.Equal(target.FrontMatter.GithubRef, fresh.FrontMatter.GithubRef);
            Assert.Equal(target.FrontMatter.GithubTreeSha, fresh.FrontMatter.GithubTreeSha);
            Assert.Equal(target.Pinned, fresh.Pinned);
            Assert.Equal(target.FrontMatter.PinnedRef, fresh.FrontMatter.PinnedRef);
        }
        var bytes = File.ReadAllBytes(fixture.SkillFile);
        var service = new GhSkillUpdateService(fixture.Runner, fixture.Logger);
        var options = new GhSkillUpdateService.Options(Skills: ["demo"], Directory: fixture.Skills, Targets: [target], Force: true);
        var refused = await service.UpdateAsync(fixture.Gh, options, TestContext.Current.CancellationToken);
        Assert.False(refused.Succeeded); Assert.False(refused.MutationAttempted);
        Assert.Contains(kind == "fresh-diagnostic" ? "Current update tracking metadata" : "Captured update metadata", refused.ErrorMessage);
        Assert.False(File.Exists(fixture.Mutation)); Assert.False(File.Exists(fixture.Discovery));
        var dry = await service.UpdateAsync(fixture.Gh, options with { DryRun = true }, TestContext.Current.CancellationToken);
        Assert.True(dry.Succeeded, dry.ErrorMessage); Assert.False(dry.MutationAttempted);
        Assert.True(File.Exists(fixture.DryRun)); Assert.False(File.Exists(fixture.Mutation));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SkillFile));
    }

    [Theory]
    [InlineData(true, "  github-pinned: [v1]\n")]
    [InlineData(false, "  github-pinned: [v1]\n")]
    [InlineData(true, "  custom: [value]\n")]
    [InlineData(false, "  custom: [value]\n")]
    [InlineData(true, "  github-pinned: [\n")]
    [InlineData(false, "  github-pinned: [\n")]
    public async Task InstallRejectsMalformedUnknownPinEvidenceBeforeDiscoveryOrMutation(bool local, string metadata)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX adapter recorder; no Windows child execution proof."); return; }
        using var fixture = new Fixture();
        fixture.WriteMetadata(metadata);
        var target = fixture.Target;
        Assert.False(target.Pinned);
        Assert.True(target.Validity is ValidityState.InvalidMetadata or ValidityState.UnparsableFrontMatter
            || !target.FrontMatter.Diagnostics.IsEmpty);
        var bytes = File.ReadAllBytes(fixture.SkillFile);
        var result = await new GhSkillInstallService(fixture.Runner, fixture.Logger).InstallAsync(fixture.Gh, "fixture/source", "demo",
            new(Path: fixture.Skills, FromLocal: local, Overwrite: true), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Contains("Destination tracking or pin metadata is invalid or unknown", result.ErrorMessage);
        Assert.False(File.Exists(fixture.Discovery)); Assert.False(File.Exists(fixture.Mutation));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SkillFile));
    }

    [Fact]
    public async Task ValidExactSubjectReachesRecorderAndDryRunRemainsReadOnly()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX adapter recorder; no Windows child execution proof."); return; }
        using var fixture = new Fixture();
        var target = fixture.Target;
        Assert.Equal(ValidityState.Valid, target.Validity); Assert.Empty(target.FrontMatter.Diagnostics);
        var service = new GhSkillUpdateService(fixture.Runner, fixture.Logger);
        var options = new GhSkillUpdateService.Options(Skills: ["demo"], Directory: fixture.Skills, Targets: [target]);
        var dry = await service.UpdateAsync(fixture.Gh, options with { DryRun = true }, TestContext.Current.CancellationToken);
        Assert.True(dry.Succeeded, dry.ErrorMessage); Assert.False(dry.MutationAttempted);
        Assert.True(File.Exists(fixture.DryRun)); Assert.False(File.Exists(fixture.Mutation));
        var real = await service.UpdateAsync(fixture.Gh, options, TestContext.Current.CancellationToken);
        Assert.True(real.Succeeded, real.ErrorMessage); Assert.True(real.MutationAttempted);
        Assert.True(File.Exists(fixture.Mutation));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("skillview-adapter-metadata-");
        internal Logger Logger { get; } = new();
        internal string Skills { get; }
        internal string SkillFile { get; }
        internal string Gh { get; }
        internal string Mutation => Path.Combine(root.FullName, "mutation");
        internal string Discovery => Path.Combine(root.FullName, "discovery");
        internal string DryRun => Path.Combine(root.FullName, "dry-run");
        internal ProcessRunner Runner { get; }
        internal InstalledSkill Target => Assert.Single(new LocalSkillScanner(Logger).ScanWithCancellation(
            [new(Skills, Scope.Custom, null)], null, TestContext.Current.CancellationToken));
        internal Fixture()
        {
            Skills = Directory.CreateDirectory(Path.Combine(root.FullName, "skills")).FullName;
            SkillFile = Path.Combine(Directory.CreateDirectory(Path.Combine(Skills, "demo")).FullName, "SKILL.md");
            WriteMetadata("");
            Gh = Path.Combine(root.FullName, "fake-gh");
            File.WriteAllText(Gh, "#!/bin/sh\nfor arg do\n case \"$arg\" in --dry-run) touch \"$SV_DRY_RUN\"; printf 'All skills are up to date.\\n'; exit 0;; esac\ndone\nfor arg do\n case \"$arg\" in --all|demo) touch \"$SV_MUTATION\"; exit 0;; esac\ndone\ntouch \"$SV_DISCOVERY\"\nprintf '[root] demo\\tdescription\\n'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName;
            Runner = new(Logger, environment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance"),
                ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
                ["CODEX_HOME"] = Path.Combine(home, "codex"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
                ["SV_MUTATION"] = Mutation,
                ["SV_DISCOVERY"] = Discovery,
                ["SV_DRY_RUN"] = DryRun,
                ["TMPDIR"] = "/var/tmp",
                ["GIT_DIR"] = Path.Combine(root.FullName, ".git"),
                ["GIT_WORK_TREE"] = root.FullName,
                ["GIT_COMMON_DIR"] = Path.Combine(root.FullName, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            }, defaultWorkingDirectory: root.FullName);
        }
        internal void WriteMetadata(string extra) => File.WriteAllText(SkillFile,
            "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  github-path: skills/demo\n"
            + extra + "---\nOriginal instructions\n");
        public void Dispose() => root.Delete(true);
    }
}
