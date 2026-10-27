using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class LocalReimportAdmissionTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-local-reimport-admission-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    [Theory]
    [InlineData("relative")]
    [InlineData("pinned")]
    [InlineData("remote")]
    [InlineData("overlap")]
    [InlineData("partial")]
    [InlineData("missing-source")]
    public async Task UnsupportedSelectionRefusesBeforePrivateStateOrGhExecution(string kind)
    {
        var skills = Directory.CreateDirectory(Path.Combine(temp.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "local")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "local-source")).FullName;
        if (kind == "missing-source") Directory.Delete(source);
        var path = kind == "relative" ? "relative" : kind == "overlap" ? payload : source;
        var content = "---\nname: local\ndescription: local\nmetadata:\n  local-path: " + JsonSerializer.Serialize(path)
            + (kind == "pinned" ? "\n  github-pinned: v1" : kind == "remote" ? "\n  github-repo: https://github.com/example/remote" : "") + "\n---\nbody\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), content);
        ScanRoot[] roots = [new(skills, Scope.Custom, null)];
        var inventory = InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray(),
            ConfiguredRootsComplete = kind != "partial"
        };
        var state = Path.Combine(temp.FullName, "private", "plans");
        var logger = new Logger(LogLevel.Debug);
        var forbiddenGh = Path.Combine(temp.FullName, "must-not-run");
        var store = new ReplacementPlanStore(state, logger, runner: new ProcessRunner(logger,
            environment: new Dictionary<string, string> { ["HOME"] = temp.FullName, ["USERPROFILE"] = temp.FullName }, defaultWorkingDirectory: temp.FullName));
        var before = File.ReadAllBytes(Path.Combine(payload, "SKILL.md"));
        await Assert.ThrowsAnyAsync<IOException>(() => store.PlanLocalReimportAsync(forbiddenGh, Assert.Single(inventory.Skills), inventory, Token));
        Assert.False(Directory.Exists(state)); Assert.False(Directory.Exists(state + "-local-previews"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(payload, "SKILL.md")));
        Assert.DoesNotContain(logger.Snapshot(), entry => entry.Category == "subprocess" && entry.Message.Contains(forbiddenGh, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullQuotaStillRequiresHeldSourceCaptureWithoutSpeculativeUnchanged(bool metadataOnlyChange)
    {
        var skills = Directory.CreateDirectory(Path.Combine(temp.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "local")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "source")).FullName;
        var content = "---\nname: local\ndescription: local\nmetadata:\n  local-path: " + JsonSerializer.Serialize(source) + "\n---\nbody\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), content);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), content);
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { payload, source, Path.Combine(payload, "SKILL.md"), Path.Combine(source, "SKILL.md") })
                WindowsPayloadMetadataTests.SetFixtureOwner(path);
            if (metadataOnlyChange) File.WriteAllText(Path.Combine(source, "SKILL.md") + ":capacity-witness", "source metadata only");
        }
        else if (metadataOnlyChange)
            File.SetUnixFileMode(Path.Combine(source, "SKILL.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ScanRoot[] roots = [new(skills, Scope.Custom, null)];
        var inventory = InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray()
        };
        var state = Path.Combine(temp.FullName, "private", "plans");
        using (var root = NativeStateDirectory.OpenOrCreate(state, [payload, source], Token))
        {
            using var writer = root.OpenFile("writer.lock", true, Token);
            for (var index = 0; index < 16; index++)
                using (root.CreateStagingDirectory(Guid.NewGuid().ToString("N"), Token)) { }
        }
        var beforeState = PayloadSnapshot.Capture(state, Token);
        var beforePayload = PayloadSnapshot.Capture(payload, Token);
        var beforeSource = PayloadSnapshot.Capture(source, Token);
        var logger = new Logger(LogLevel.Debug);
        var reachedCapture = false;
        var store = new ReplacementPlanStore(state, logger, checkpoint: step =>
        {
            if (step != "local-source-captured") return;
            reachedCapture = true;
            // Stop at the real capture boundary: no synthetic gh witness or
            // incomplete metadata comparison may establish an unchanged result.
            throw new IOException("fixture stopped after held source capture");
        }, runner: new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["HOME"] = temp.FullName, ["USERPROFILE"] = temp.FullName }, defaultWorkingDirectory: temp.FullName));
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalReimportAsync("must-never-be-executed", Assert.Single(inventory.Skills), inventory, Token));
        Assert.True(reachedCapture);
        Assert.Contains("fixture stopped after held source capture", error.Message);
        Assert.Contains("final operation: not allocated", error.Message);
        Assert.Equal(beforeState.ObjectStateFingerprint, PayloadSnapshot.Capture(state, Token).ObjectStateFingerprint);
        Assert.Equal(beforeState.Fingerprint, PayloadSnapshot.Capture(state, Token).Fingerprint);
        Assert.Equal(beforePayload.ObjectStateFingerprint, PayloadSnapshot.Capture(payload, Token).ObjectStateFingerprint);
        Assert.Equal(beforePayload.Fingerprint, PayloadSnapshot.Capture(payload, Token).Fingerprint);
        Assert.Equal(beforeSource.ObjectStateFingerprint, PayloadSnapshot.Capture(source, Token).ObjectStateFingerprint);
        Assert.Equal(beforeSource.Fingerprint, PayloadSnapshot.Capture(source, Token).Fingerprint);
        if (OperatingSystem.IsWindows() && metadataOnlyChange)
            Assert.Equal("source metadata only", File.ReadAllText(Path.Combine(source, "SKILL.md") + ":capacity-witness"));
        Assert.Equal(["writer.lock"], Directory.EnumerateFileSystemEntries(state + "-local-previews").Select(Path.GetFileName).ToArray());
        Assert.DoesNotContain(logger.Snapshot(), entry => entry.Category == "subprocess");
    }

    [Fact]
    public void ChildEnvironmentIsPrivateAndNeutralizesAuthGitAndProxyBypasses()
    {
        var home = Path.Combine(temp.FullName, "private-home");
        var originalHome = System.Environment.GetEnvironmentVariable("HOME");
        var values = GhLocalImportStager.PrivateEnvironment(home, Path.Combine(temp.FullName, "gh"), "http://127.0.0.1:12345");
        foreach (var key in new[] { "HOME", "USERPROFILE", "GH_CONFIG_DIR", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "XDG_STATE_HOME", "APPDATA", "GIT_DIR", "GIT_COMMON_DIR" })
            Assert.True(PathIdentity.IsInside(values[key], home) || values[key] == home, key);
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "CLAUDE_CODE_PLUGIN_DIRS", "CLAUDE_CODE_PLUGIN_SEED_DIR", "NO_PROXY", "no_proxy" }) Assert.Equal("", values[key]);
        Assert.Equal("0", values["GIT_CONFIG_COUNT"]);
        Assert.Equal(values["HTTPS_PROXY"], values["https_proxy"]);
        Assert.Equal(originalHome, System.Environment.GetEnvironmentVariable("HOME"));
    }

    [Fact]
    public void BinaryWitnessHashesActualRegularFileBytesAndRejectsLinks()
    {
        var binary = Path.Combine(temp.FullName, "gh");
        var bytes = new byte[] { 0, 255, 1, 2, 13, 10 };
        File.WriteAllBytes(binary, bytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), GhLocalImportStager.BinaryHash(binary, Token));
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(temp.FullName, "linked-gh"); File.CreateSymbolicLink(link, "gh");
            Assert.ThrowsAny<IOException>(() => GhLocalImportStager.BinaryHash(link, Token));
            var bound = GhLocalImportStager.BindBinary(link, Token);
            Assert.Equal(OwnershipPath.EntryAddress(binary), bound.Path);
            Assert.Equal(GhLocalImportStager.BinaryHash(binary, Token), bound.Hash);
        }
    }

    [Fact]
    public void CallerCancellationKeepsKindTokenAndBothBoundedChildStreams()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = new ProcessResult("gh", [], 130, "partial output", "partial error", TimeSpan.Zero) { Cancelled = true };
        var error = Assert.Throws<OperationCanceledException>(() => GhLocalImportStager.RequireChildResult(result, "fixture", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("partial output", error.Message); Assert.Contains("partial error", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeadlineAndFailureRemainTypedWithBothStreamsAndUncanceledCaller(bool deadline)
    {
        var result = new ProcessResult("gh", [], deadline ? 130 : 3, "output witness", "error witness", TimeSpan.Zero) { Cancelled = deadline };
        var error = Assert.Throws<GhSourceRequestException>(() => GhLocalImportStager.RequireChildResult(result, "fixture", Token));
        Assert.Equal("output witness", error.StdOut); Assert.Equal("error witness", error.StdErr);
        Assert.Equal(deadline, error.Message.Contains("deadline", StringComparison.Ordinal));
        Assert.False(Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData("SKILL.md", "existing-parent")]
    [InlineData("skill.md", "existing-parent")]
    [InlineData("SKILL.md", "missing-ancestors")]
    [InlineData("SKILL.md", "source-parent")]
    public async Task NestedSkillRefusesBeforeGhWithoutFirstUseNamespaceInvalidatingSource(string leaf, string layout)
    {
        var skills = Directory.CreateDirectory(Path.Combine(temp.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "local")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "source")).FullName;
        var authored = "---\nname: local\ndescription: local\n---\nbody\n";
        File.WriteAllText(Path.Combine(source, "SKILL.md"), authored);
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, leaf), "---\nname: nested\ndescription: separate\n---\nnested\n");
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: local\ndescription: local\nmetadata:\n  local-path: " + JsonSerializer.Serialize(source) + "\n---\ninstalled\n");
        ScanRoot[] roots = [new(skills, Scope.Custom, null)];
        var inventory = InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray()
        };
        var parent = layout == "source-parent" ? temp.FullName : Path.Combine(temp.FullName, "private");
        if (layout == "existing-parent") Directory.CreateDirectory(parent);
        var state = Path.Combine(parent, "plans");
        Assert.False(Directory.Exists(state));
        Assert.False(Directory.Exists(state + "-local-previews"));
        var store = new ReplacementPlanStore(state, new Logger(), runner: new ProcessRunner(new Logger(),
            environment: new Dictionary<string, string> { ["HOME"] = temp.FullName, ["USERPROFILE"] = temp.FullName }, defaultWorkingDirectory: temp.FullName));
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalReimportAsync(Path.Combine(temp.FullName, "must-not-run"), Assert.Single(inventory.Skills), inventory, Token));
        Assert.Contains("Nested SKILL.md", error.Message);
        Assert.Contains("no gh child was launched", error.Message);
        Assert.True(Directory.Exists(state));
        Assert.Empty(Directory.EnumerateFileSystemEntries(state));
        Assert.Equal(authored, File.ReadAllText(Path.Combine(source, "SKILL.md")));
    }
}
