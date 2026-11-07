using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Preferences;
using SkillView.Subprocess;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed partial class LocationPreferencesTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skillview-locations-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(_temp.FullName, "state");
    private string Records => Directory.GetDirectories(Root, "records-*").Single();
    private LocationPreferences Store() => new(Root);
    private SelectedLocation Selection(string kind = "workspace", string leaf = "project") =>
        LocationPreferences.ValidateNew(new(kind, Directory.CreateDirectory(Path.Combine(_temp.FullName, leaf)).FullName,
            kind == "profile" ? "codex" : null, kind == "profile" ? "work" : null), _temp.FullName, Token);
    private static AppOptions Options() => new(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []);
    public void Dispose() => _temp.Delete(true);

    [Fact]
    public void MissingReadCreatesNothingAndSaveReloadRemovePreservesSelectedDirectory()
    {
        var store = Store(); var read = store.Read([], Token);
        Assert.Null(read.Diagnostic); Assert.False(Directory.Exists(Root));
        var selected = Selection(); var file = Path.Combine(selected.Path, "untouched"); File.WriteAllText(file, "private");
        Assert.True(SecureRemovalBackend.TryObserveIdentity(selected.Path, out var before, out _));
        var saved = store.Save(read, [selected], [], _temp.FullName, Token);
        Assert.Equal(saved.Fingerprint, Store().Read([], Token).Fingerprint);
        Assert.Equal(saved.Locations, Store().Read([], Token).Locations);
        var cleared = Store().Save(saved, [], [], _temp.FullName, Token);
        Assert.Empty(Store().Read([], Token).Locations); Assert.Equal(2, cleared.Sequence);
        Assert.Equal("private", File.ReadAllText(file));
        Assert.True(SecureRemovalBackend.TryObserveIdentity(selected.Path, out var after, out _)); Assert.Equal(before, after);
    }

    [Fact]
    public void ExplicitCliOptionsOverrideOnlyTheirOwnCategory()
    {
        var workspace = Selection(); var root = Selection("scan-root", "scan"); var profile = Selection("profile", "profile");
        var read = new LocationRead([workspace, root, profile]);
        var empty = LocationPreferences.Apply(Options(), read);
        Assert.Equal([workspace.Path], empty.WorkspaceDirectories); Assert.Equal([root.Path], empty.ScanRoots);
        Assert.Equal("work", Assert.Single(empty.Profiles).Name);
        var cli = Options() with { ScanRoots = ["explicit-root"] };
        var applied = LocationPreferences.Apply(cli, read);
        Assert.Equal(["explicit-root"], applied.ScanRoots); Assert.Equal([workspace.Path], applied.WorkspaceDirectories);
        Assert.Equal(cli, cli with { });
    }

    [Fact]
    public void RemovedDirectoryIsRetainedOnReloadAsConfiguredEvidence()
    {
        var selected = Selection(); var saved = Store().Save(new([]), [selected], [], _temp.FullName, Token);
        Directory.Delete(selected.Path);
        var read = Store().Read([], Token); Assert.Null(read.Diagnostic); Assert.Equal(saved.Fingerprint, read.Fingerprint);
        Assert.Equal(saved.Locations, read.Locations);
        Assert.Equal([selected.Path], LocationPreferences.Apply(Options(), read).WorkspaceDirectories);
        Assert.Empty(Store().Save(read, [], [], _temp.FullName, Token).Locations);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"sequence\":1,\"sequence\":1,\"locations\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"sequence\":1,\"locations\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"sequence\":1,\"locations\":[{\"kind\":\"scan-root\",\"path\":\"relative\",\"agent\":null,\"name\":null}]}")]
    [InlineData("{\"schemaVersion\":1,\"sequence\":1,\"locations\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"sequence\":1,\"locations\":[{\"kind\":\"profile\",\"path\":\"/tmp\",\"agent\":\"invented\",\"name\":null}]}")]
    [InlineData("{")]
    public void HostileOrPartialRecordsAreVisibleAndNeverOverwritten(string text)
    {
        var selected = Selection(); Store().Save(new([]), [selected], [], _temp.FullName, Token);
        var file = Path.Combine(Records, "0001.json"); File.WriteAllText(file, text);
        var read = Store().Read([], Token); Assert.NotNull(read.Diagnostic); Assert.Empty(read.Locations);
        Assert.Throws<IOException>(() => Store().Save(read, [], [], _temp.FullName, Token));
        Assert.Equal(text, File.ReadAllText(file)); Assert.Single(Directory.GetFiles(Records, "*.json"));
    }

    [Fact]
    public void StaleWriterAndUnexpectedEntryRefuseWithoutReplacingRecords()
    {
        var selected = Selection(); var original = new LocationRead([]);
        var saved = Store().Save(original, [selected], [], _temp.FullName, Token);
        Assert.Throws<IOException>(() => Store().Save(original, [], [], _temp.FullName, Token));
        File.WriteAllText(Path.Combine(Root, "unexpected"), "untouched");
        Assert.NotNull(Store().Read([], Token).Diagnostic);
        Assert.Throws<IOException>(() => Store().Save(saved, [], [], _temp.FullName, Token));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(Root, "unexpected")));
    }

    [Fact]
    public void SymlinkRecordAndPayloadStatePlacementRefuse()
    {
        if (OperatingSystem.IsWindows()) return;
        var selected = Selection(); var saved = Store().Save(new([]), [selected], [], _temp.FullName, Token);
        var record = Path.Combine(Records, "0001.json"); var victim = Path.Combine(_temp.FullName, "victim");
        File.Move(record, victim); File.CreateSymbolicLink(record, victim);
        var bytes = File.ReadAllBytes(victim);
        Assert.NotNull(Store().Read([], Token).Diagnostic);
        Assert.Throws<IOException>(() => Store().Save(saved, [], [], _temp.FullName, Token));
        Assert.Equal(bytes, File.ReadAllBytes(victim));
        var inside = new LocationPreferences(Path.Combine(selected.Path, "state"));
        Assert.Throws<IOException>(() => inside.Save(new([]), [selected], [selected.Path], _temp.FullName, Token));
        Assert.False(Directory.Exists(inside.Root));
    }

    [Fact]
    public void BoundsCancellationAndDirectoryValidationPrecedeStateWrites()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Store().Save(new([]), [], [], _temp.FullName, canceled.Token));
        Assert.False(Directory.Exists(Root));
        Assert.Throws<IOException>(() => LocationPreferences.ValidateNew(new("workspace", "missing"), _temp.FullName, Token));
        Assert.Throws<IOException>(() => LocationPreferences.ValidateNew(new("workspace", "bad\npath"), _temp.FullName, Token));
        var file = Path.Combine(_temp.FullName, "regular"); File.WriteAllText(file, "untouched");
        Assert.Throws<IOException>(() => LocationPreferences.ValidateNew(new("workspace", file), _temp.FullName, Token));
        Assert.Throws<IOException>(() => LocationPreferences.ValidateShape(Enumerable.Range(0, 33).Select(i => new SelectedLocation("workspace", PathIdentity.Normalize(Path.Combine(_temp.FullName, "p" + i))))));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void CapacityCanCompactSavedSelectionAndResetWithVerifiedRawBackup()
    {
        var first = Selection(); var second = Selection("workspace", "second");
        var store = Store(); var read = new LocationRead([]);
        for (var i = 1; i <= LocationPreferences.MaxRecords; i++)
            read = store.Save(read, [i % 2 == 0 ? second : first], [], _temp.FullName, Token);
        Assert.Equal(LocationPreferences.MaxRecords, read.Sequence);
        var full = Assert.Throws<IOException>(() => store.Save(read, [first], [], _temp.FullName, Token));
        Assert.Contains("64/64", full.Message); Assert.Contains("Recovery", full.Message); Assert.Contains(Root, full.Message);
        var old = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var lockPath = Path.Combine(Root, "writer.lock");
        var beforeLock = Inspect(lockPath);
        var review = store.PrepareReset([], Token);
        Assert.True(review.CanReset); Assert.True(review.CanCompact); Assert.Contains("75%", review.Explanation);
        var compacted = store.Reset(review, true, [], Token);
        Assert.Equal([second], compacted.Locations); Assert.Equal(1, compacted.Sequence);
        Assert.NotEqual(read.RecordsName, compacted.RecordsName); Assert.NotEqual(read.Fingerprint, compacted.Fingerprint);
        var backup = Directory.GetDirectories(Root, "backup-*").Single();
        foreach (var item in old) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(backup, item.Key!)));
        Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
        var afterLock = Inspect(lockPath); Assert.Equal(beforeLock, afterLock);
        var blocked = store.PrepareReset([], Token); Assert.False(blocked.CanReset); Assert.False(blocked.CanCompact);
        Assert.Contains(backup, blocked.Explanation); Assert.Contains("external archive", blocked.Explanation);
        Assert.Throws<IOException>(() => store.Reset(blocked, false, [], Token));
        // The explicit external archive procedure preserves the backup; product
        // code never moves/deletes it. A later reset is possible afterwards.
        Directory.Move(backup, Path.Combine(_temp.FullName, "external-archive"));
        var cleared = store.Reset(store.PrepareReset([], Token), false, [], Token);
        Assert.Empty(cleared.Locations); Assert.True(Directory.Exists(Root)); Assert.True(File.Exists(lockPath));
        Assert.Empty(Directory.GetDirectories(Root, "records-*"));
    }

    [Fact]
    public void PreferencePathUsesExactCapturedChildContext()
    {
        var logger = new Logger();
        var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["HOME"] = _temp.FullName, ["USERPROFILE"] = _temp.FullName, ["SKILLVIEW_STATE_HOME"] = "chosen-state" }, defaultWorkingDirectory: _temp.FullName);
        var store = LocationPreferences.For(runner);
        Assert.Equal(Path.Combine(_temp.FullName, "chosen-state", "SkillView", "locations"), store.Root);
    }
}
