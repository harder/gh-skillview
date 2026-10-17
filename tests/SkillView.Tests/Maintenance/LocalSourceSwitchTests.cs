using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class LocalSourceSwitchTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-source-switch-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    private (ReplacementPlanStore Store, InventorySnapshot Inventory, string State, string Source) Fixture(string kind, Action<string>? checkpoint = null)
    {
        var skills = Directory.CreateDirectory(Path.Combine(temp.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "demo")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "new-source")).FullName;
        var old = Path.Combine(temp.FullName, "old", "missing", "demo");
        var state = Path.Combine(temp.FullName, "private", "plans");
        if (kind == "old-mount") old = "/proc/skillview-source-switch-missing/old";
        if (kind == "old-dot")
        {
            var a = Directory.CreateDirectory(Path.Combine(temp.FullName, "a")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(temp.FullName, "private", "nested")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(a, "link"), nested);
            old = Path.Combine(a, "link", "..", "state");
            state = Path.Combine(temp.FullName, "private", "state", "plans");
        }
        if (kind == "old-file") { old = Path.Combine(temp.FullName, "old-file"); File.WriteAllText(old, "ordinary file"); }
        if (kind == "old-broken") { old = Path.Combine(temp.FullName, "old-broken"); Directory.CreateSymbolicLink(old, Path.Combine(temp.FullName, "absent")); }
        if (kind == "old-state-alias")
        {
            var privateParent = Directory.CreateDirectory(Path.GetDirectoryName(state)!).FullName;
            old = Path.Combine(temp.FullName, "alias", "old-missing");
            Directory.CreateSymbolicLink(Path.GetDirectoryName(old)!, privateParent);
            state = Path.Combine(privateParent, "old-missing", "plans");
        }
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Installed\nmetadata:\n  local-path: " + JsonSerializer.Serialize(old) + (kind == "pinned" ? "\n  github-pinned: v1" : "") + "\n---\nI\n");
        var sourceTracking = kind == "relative-c" ? "relative-history" : old;
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: New source\nmetadata:\n  local-path: " + JsonSerializer.Serialize(sourceTracking) + "\n---\nC\n");
        if (kind is "old-missing" or "old-state-alias")
        {
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "SKILL.md"), "---\nname: nested\n---\n");
        }
        ScanRoot[] roots = [new(skills, Scope.Custom, null)];
        var inventory = InventorySnapshot.Empty with { Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(), ConfiguredRoots = [.. roots], ScannedRoots = [.. roots], ConfiguredRootsComplete = kind != "partial" };
        var runner = new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["HOME"] = temp.FullName,
            ["USERPROFILE"] = temp.FullName,
            ["GIT_DIR"] = Path.Combine(temp.FullName, "private-git"),
            ["GIT_WORK_TREE"] = temp.FullName,
            ["GIT_COMMON_DIR"] = Path.Combine(temp.FullName, "private-git"),
            ["GIT_CONFIG_COUNT"] = "0"
        }, defaultWorkingDirectory: temp.FullName);
        return (new(state, new Logger(), checkpoint, runner), inventory, state, source);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("relative-new")]
    [InlineData("missing-new")]
    [InlineData("old-dot")]
    [InlineData("old-mount")]
    [InlineData("old-file")]
    [InlineData("old-broken")]
    [InlineData("old-state-alias")]
    [InlineData("partial")]
    [InlineData("pinned")]
    public async Task RefusesUnsafeOrNoTransitionBeforePrivateWritesOrChild(string kind)
    {
        if (!OperatingSystem.IsLinux() && kind == "old-mount") return;
        if (OperatingSystem.IsWindows() && kind is "old-broken" or "old-state-alias" or "old-dot") return; // Native link fixtures require host link privilege.
        var (store, inventory, state, source) = Fixture(kind);
        var target = Assert.Single(inventory.Skills);
        var before = File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md"));
        var selected = kind == "same" ? target.FrontMatter.LocalPath! : kind == "relative-new" ? "relative" : kind == "missing-new" ? Path.Combine(source, "missing") : source;
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalSourceSwitchAsync(Path.Combine(temp.FullName, "must-not-launch"), target, selected, inventory, Token));
        if (kind == "same") Assert.Contains("No source transition", error.Message);
        if (kind == "old-dot") Assert.Contains("Placement requires", error.Message);
        Assert.False(Directory.Exists(state)); Assert.False(Directory.Exists(state + "-local-previews"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md")));
    }

    [Fact]
    public async Task NullNewSourceCannotSilentlyEnterSchemaFivePreparation()
    {
        var (store, inventory, state, _) = Fixture("null");
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PlanLocalSourceSwitchAsync("must-not-run", Assert.Single(inventory.Skills), null!, inventory, Token));
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void CopiedSourceTrackingSplicePreservesBomCrLfCommentsAndUnknownScalarBytes()
    {
        var old = Path.Combine(temp.FullName, "old-history"); var selected = Path.Combine(temp.FullName, "new-source");
        var text = "\uFEFF---\r\n# authored comment\r\nname: demo\r\ndescription: 'Emoji 😀'\r\nmetadata:\r\n  custom: preserve-me\r\n  local-path: " + JsonSerializer.Serialize(old) + " # keep comment\r\n---\r\nBody 😀\r\n";
        var before = System.Text.Encoding.UTF8.GetBytes(text);
        var desired = LocalTrackingWriter.Bind(before, selected);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(text.Replace(JsonSerializer.Serialize(old), JsonSerializer.Serialize(selected), StringComparison.Ordinal)), desired);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(text), before);
    }

    [Fact]
    public async Task SchemaFiveStillRefusesCopiedSourceWithDifferentRecordedPath()
    {
        var (store, inventory, _, source) = Fixture("schema-five");
        var payload = Assert.Single(inventory.Skills).ResolvedPath;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Installed\nmetadata:\n  local-path: " + JsonSerializer.Serialize(source) + "\n---\nI\n");
        inventory = inventory with { Skills = new LocalSkillScanner(new Logger()).Scan(inventory.ScannedRoots).ToImmutableArray() };
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalReimportAsync(Path.Combine(temp.FullName, "must-not-launch"), Assert.Single(inventory.Skills), inventory, Token));
        Assert.Contains("unambiguous tracking", error.Message);
    }

    [Fact]
    public async Task SameTrackingCannotHideChangedInstalledAuthoredMetadata()
    {
        var (store, inventory, _, source) = Fixture("stale-authored");
        var payload = Assert.Single(inventory.Skills).ResolvedPath;
        var path = Path.Combine(payload, "SKILL.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("description: Installed", "description: Changed after inventory", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalSourceSwitchAsync(Path.Combine(temp.FullName, "must-not-launch"), Assert.Single(inventory.Skills), source, inventory, Token));
        Assert.Contains("Installed authored metadata changed", error.Message);
    }

    [Fact]
    public async Task ProvenMissingOldSourceDoesNotBecomePayloadDependency()
    {
        var captured = false;
        var (store, inventory, state, source) = Fixture("old-missing", phase => { if (phase == "local-source-captured") captured = true; });
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalSourceSwitchAsync(Path.Combine(temp.FullName, "must-not-launch"), Assert.Single(inventory.Skills), source, inventory, Token));
        Assert.True(captured); Assert.Contains("Nested SKILL.md", error.Message);
        Assert.False(Directory.Exists(Assert.Single(inventory.Skills).FrontMatter.LocalPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(state));
    }

    [Fact]
    public async Task CopiedSourceRelativeTrackingRefusesExplicitlyRatherThanRelaxingSchemaFive()
    {
        var (store, inventory, _, source) = Fixture("relative-c");
        var error = await Assert.ThrowsAsync<IOException>(() => store.PlanLocalSourceSwitchAsync(Path.Combine(temp.FullName, "must-not-launch"), Assert.Single(inventory.Skills), source, inventory, Token));
        Assert.Contains("Captured source local-path must be absent or an absolute", error.Message);
    }

    [Fact]
    public async Task CanceledCaptureRetainsExactDiagnosticAndLeavesBothPayloadsUnchanged()
    {
        using var cancel = new CancellationTokenSource();
        var (store, inventory, _, source) = Fixture("cancel", phase => { if (phase == "local-source-captured") cancel.Cancel(); });
        var target = Assert.Single(inventory.Skills);
        var beforeI = File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md"));
        var beforeC = File.ReadAllBytes(Path.Combine(source, "SKILL.md"));
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => store.PlanLocalSourceSwitchAsync(Path.Combine(temp.FullName, "must-not-launch"), target, source, inventory, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Contains("Preview", error.Message); Assert.Contains("final operation: not allocated", error.Message);
        Assert.Equal(beforeI, File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md"))); Assert.Equal(beforeC, File.ReadAllBytes(Path.Combine(source, "SKILL.md")));
    }
}
