using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed class IntentionalDifferenceResetTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skillview-choice-reset-");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private InventorySnapshot Inventory => InventorySnapshot.Empty;
    private const string Context = "captured home/state/cwd fixture";
    private string Root => Path.Combine(_temp.FullName, "choices");
    private IntentionalDifferenceStore Store(int count = 512, Action<string>? checkpoint = null) => new(Root, count) { Checkpoint = checkpoint };
    private DifferenceBinding Binding(int evidence = 1) => new(new('a', 64), Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(evidence))));
    private DifferenceRead Read(IntentionalDifferenceStore? store = null) => (store ?? Store()).Read([], Token);
    private void Set(int evidence = 1, IntentionalDifferenceStore? store = null)
    { store ??= Store(); store.Set(Binding(evidence), Read(store).Version, false, Inventory, Token); }
    private static SecureFileIdentity FileIdentity(string path)
    {
        SecureFileIdentity result = default;
        RegularFileInspection.Capture(path, item => { result = item.Identity; return new(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), 0); }, Token);
        return result;
    }
    private string Records => Directory.GetDirectories(Root, "records-*").Single();
    private string Backup => Directory.GetDirectories(Root, "backup-*").Single();
    private static void WritePrivateFile(string path, byte[] bytes)
    {
        if (File.Exists(path)) { File.WriteAllBytes(path, bytes); return; }
        using var parent = NativeStateDirectory.OpenOrCreate(Path.GetDirectoryName(path)!, [], Token, allowCreate: false);
        using var file = parent.OpenFile(Path.GetFileName(path), false, Token);
        file.Write(bytes); file.Flush(true);
    }
    public void Dispose() => _temp.Delete(true);

    [Fact]
    public void ChangedEvidenceSupersedesFamilyAndReservationWithoutCarryingIntent()
    {
        var store = Store(10);
        for (var index = 1; index <= 9; index++)
        {
            var before = Read(store); Assert.False(before.Active(Binding(index)));
            store.Set(Binding(index), before.Version, false, Inventory, Token);
            var after = Read(store); Assert.True(after.Active(Binding(index)));
            for (var old = 1; old < index; old++) Assert.False(after.Active(Binding(old)));
            if (index >= 7) Assert.Contains("active families and reserved clear slots: 1", after.CapacityNotice!);
        }
        var full = Assert.Throws<IOException>(() => Set(10, store));
        Assert.Contains("Retained records: 9/10", full.Message); Assert.Contains(Root, full.Message);
        Assert.Contains("refreshing cannot free capacity", full.Message); Assert.Contains("Shift+K", full.Message);
        store.Set(Binding(9), Read(store).Version, true, Inventory, Token);
        Assert.False(Read(store).Active(Binding(9))); Assert.Equal(10, Read(store).Records.Length);
    }

    [Fact]
    public void ResetPreservesRawMalformedBytesPermanentLockAndRejectsStaleWriter()
    {
        Set(); Set(2); var store = Store(); var before = Read();
        var lockPath = Path.Combine(Root, "writer.lock"); var heldLock = FileIdentity(lockPath);
        var malformed = Directory.GetFiles(Records).Order(StringComparer.Ordinal).Last(); File.WriteAllBytes(malformed, [0, 255, 123, 42]);
        var originals = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.NotNull(Read().Diagnostic);
        var review = store.InspectReset(Inventory, Context, Token); Assert.Equal(2, review.Files.Length);
        var result = store.Reset(review, Inventory, Context, Token);
        Assert.Equal("reset", result.State); Assert.Contains(Backup, result.Message);
        Assert.All(originals, pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Backup, pair.Key!))));
        Assert.True(File.Exists(Path.Combine(Backup, "manifest.json"))); Assert.Empty(Directory.GetFiles(Records));
        var reset = Read(); Assert.Null(reset.Diagnostic); Assert.False(reset.Active(Binding(2))); Assert.NotEqual(before.Version, reset.Version);
        Assert.Throws<IOException>(() => store.Set(Binding(3), before.Version, false, Inventory, Token));
        var afterLock = FileIdentity(lockPath); Assert.Equal(heldLock, afterLock); Assert.Equal(0, new FileInfo(lockPath).Length);
        Set(3); Assert.True(Read().Active(Binding(3)));
        var refusal = Assert.Throws<IOException>(() => store.InspectReset(Inventory, Context, Token));
        Assert.Contains(Backup, refusal.Message); Assert.Contains("archive", refusal.Message); Assert.Contains("second backup", refusal.Message);
        Assert.Contains(Backup, Read().CapacityNotice!);
    }

    [Theory]
    [InlineData("backup-created")]
    [InlineData("backup-file:000000000001.json")]
    [InlineData("before-manifest")]
    [InlineData("manifest-published")]
    [InlineData("backup-verified")]
    [InlineData("before-removal")]
    [InlineData("deleting:000000000001.json")]
    [InlineData("deleted:000000000001.json")]
    [InlineData("records-removed")]
    [InlineData("before-fresh-generation")]
    [InlineData("fresh-generation-published")]
    public void CancellationAtEveryBoundaryPreservesBackupAndExplicitlyResumes(string phase)
    {
        Set(); Set(2); var originals = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        using var canceled = new CancellationTokenSource();
        var hit = false; var store = Store(checkpoint: actual => { if (actual == phase) { hit = true; canceled.Cancel(); canceled.Token.ThrowIfCancellationRequested(); } });
        var review = store.InspectReset(Inventory, Context, Token);
        Assert.Throws<OperationCanceledException>(() => store.Reset(review, Inventory, Context, canceled.Token)); Assert.True(hit);
        store = Store();
        if (phase != "fresh-generation-published")
        {
            Assert.NotNull(Read().Diagnostic);
            store.Reset(store.InspectReset(Inventory, Context, Token), Inventory, Context, Token);
        }
        Assert.Null(Read().Diagnostic); Assert.Empty(Read().Records);
        Assert.All(originals, pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Backup, pair.Key!))));
        Assert.Single(Directory.GetDirectories(Root, "records-*")); Assert.Single(Directory.GetDirectories(Root, "backup-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TornRawCopyOrManifestResumesOnlyAnExactPrefix(bool manifest)
    {
        Set(); var record = Directory.GetFiles(Records).Single();
        using var canceled = new CancellationTokenSource();
        var store = Store(checkpoint: phase =>
        {
            if (!manifest && phase == "backup-created")
            { var path = Path.Combine(Backup, Path.GetFileName(record)); WritePrivateFile(path, File.ReadAllBytes(record)[..12]); canceled.Cancel(); canceled.Token.ThrowIfCancellationRequested(); }
            if (manifest && phase == "manifest-published")
            { var path = Path.Combine(Backup, "manifest.json"); File.WriteAllBytes(path, File.ReadAllBytes(path)[..24]); canceled.Cancel(); canceled.Token.ThrowIfCancellationRequested(); }
        });
        Assert.Throws<OperationCanceledException>(() => store.Reset(store.InspectReset(Inventory, Context, Token), Inventory, Context, canceled.Token));
        store = Store(); store.Reset(store.InspectReset(Inventory, Context, Token), Inventory, Context, Token);
        Assert.Empty(Read().Records); Assert.Null(Read().Diagnostic);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("directory")]
    [InlineData("oversize")]
    [InlineData("link")]
    public void UnsafeOrStrayStateRefusesWithoutDeletingAnyBytes(string kind)
    {
        Set(); var path = Path.Combine(Records, kind == "unknown" ? "unrecognized.txt" : "000000000099.json");
        if (kind == "directory") Directory.CreateDirectory(path);
        else if (kind == "link")
        {
            if (OperatingSystem.IsWindows()) return;
            var outside = Path.Combine(_temp.FullName, "outside"); File.WriteAllText(outside, "Preserve outside bytes"); File.CreateSymbolicLink(path, outside);
        }
        else File.WriteAllBytes(path, new byte[kind == "oversize" ? 2049 : 4]);
        var store = Store(); var ex = Assert.Throws<IOException>(() => store.InspectReset(Inventory, Context, Token));
        Assert.Contains(path, ex.Message); Assert.Empty(Directory.GetDirectories(Root, "backup-*"));
        Assert.True(File.Exists(Path.Combine(Records, "000000000001.json")));
    }

    [Fact]
    public void ChangedContextAndStaleReviewRefuseBeforeBackupOrRemoval()
    {
        Set(); var store = Store(); var review = store.InspectReset(Inventory, Context, Token);
        Assert.Throws<IOException>(() => store.Reset(review, Inventory, "different HOME/state/cwd", Token));
        Set(2); Assert.Throws<IOException>(() => store.Reset(review, Inventory, Context, Token));
        Assert.Empty(Directory.GetDirectories(Root, "backup-*")); Assert.Equal(2, Directory.GetFiles(Records).Length);
    }

    [Fact]
    public void ResetThroughAncestorAliasReopensCanonicalBackupAndRejectsOldApproval()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix ancestor-alias regression, including macOS /var.");
        var actual = Directory.CreateDirectory(Path.Combine(_temp.FullName, "actual")).FullName;
        var alias = Path.Combine(_temp.FullName, "alias"); Directory.CreateSymbolicLink(alias, actual);
        var store = new IntentionalDifferenceStore(Path.Combine(alias, "choices"));
        store.Set(Binding(), store.Read([], Token).Version, false, Inventory, Token);
        var review = store.InspectReset(Inventory, Context, Token);
        store.Reset(review, Inventory, Context, Token);
        var reopened = new IntentionalDifferenceStore(Path.Combine(alias, "choices")).Read([], Token);
        Assert.Null(reopened.Diagnostic); Assert.Empty(reopened.Records);
        Assert.Single(Directory.GetDirectories(Path.Combine(actual, "choices"), "backup-*"));
        Assert.Throws<IOException>(() => store.Reset(review, Inventory, Context, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPermanentLockBesideRetainedGenerationCannotBeRecreatedOrReviveChoices(bool emptyGeneration)
    {
        Set(); if (emptyGeneration) File.Delete(Directory.GetFiles(Records).Single());
        var lockPath = Path.Combine(Root, "writer.lock"); File.Delete(lockPath); // Fault injection, never a product operation.
        var original = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var read = Read(); Assert.NotNull(read.Diagnostic); Assert.False(read.Active(Binding()));
        Assert.Throws<IOException>(() => Store().Set(Binding(2), read.Version, false, Inventory, Token));
        Assert.False(File.Exists(lockPath)); Assert.NotNull(Read().Diagnostic); Assert.False(Read().Active(Binding()));
        Assert.All(original, pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Records, pair.Key!))));
        Assert.Single(Directory.GetDirectories(Root));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("entry")]
    [InlineData("files")]
    [InlineData("records")]
    [InlineData("digest")]
    [InlineData("store-path")]
    [InlineData("source-path")]
    [InlineData("source-kind")]
    public void NullManifestMembersRefuseReadsAndWritesWithoutRevivingIntentOrOverwritingBytes(string field)
    {
        Set(); var store = Store(); store.Reset(store.InspectReset(Inventory, Context, Token), Inventory, Context, Token);
        Set(2); var version = Read().Version; Assert.True(Read().Active(Binding(2)));
        var path = Path.Combine(Backup, "manifest.json"); var envelope = JsonNode.Parse(File.ReadAllBytes(path))!;
        var body = JsonNode.Parse(Convert.FromBase64String(envelope["body"]!.GetValue<string>()))!;
        switch (field)
        {
            case "name": body["files"]![0]!["name"] = null; break;
            case "entry": body["files"]![0] = null; break;
            case "files": body["files"] = null; break;
            case "records": body["records"] = null; break;
            case "digest": body["files"]![0]!["sha256"] = null; break;
            case "source-path": body["files"]![0]!["sourceIdentity"]!["canonicalPath"] = null; break;
            case "source-kind": body["files"]![0]!["sourceIdentity"]!["isDirectory"] = true; break;
            default: body["storeIdentity"]!["canonicalPath"] = null; break;
        }
        var raw = Encoding.UTF8.GetBytes(body.ToJsonString()); envelope["body"] = Convert.ToBase64String(raw);
        envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)); File.WriteAllText(path, envelope.ToJsonString());
        var preserved = File.ReadAllBytes(path); var read = Read();
        Assert.NotNull(read.Diagnostic); Assert.False(read.Active(Binding(2)));
        Assert.ThrowsAny<Exception>(() => store.Set(Binding(3), version, false, Inventory, Token));
        Assert.Equal(preserved, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(Records));
    }

    [Fact]
    public void MaximumOrdinaryMalformedRecordsRemainRecoverableWithinManifestBound()
    {
        Set(); var records = Records;
        var bytes = Enumerable.Repeat((byte)255, 2048).ToArray();
        for (var index = 1; index <= 512; index++)
        {
            var path = Path.Combine(records, index.ToString("D12") + ".json"); WritePrivateFile(path, bytes);
        }
        var store = Store(); var review = store.InspectReset(Inventory, Context, Token);
        Assert.Equal(512, review.Files.Length); store.Reset(review, Inventory, Context, Token);
        Assert.True(new FileInfo(Path.Combine(Backup, "manifest.json")).Length <= 512 * 1024);
        Assert.All(Directory.GetFiles(Backup, "0*.json"), path => Assert.Equal(bytes, File.ReadAllBytes(path)));
        Assert.Null(Read().Diagnostic); Assert.Empty(Read().Records);
    }

    [Fact]
    public void UnexpectedEntryDuringRemovalStopsImmediatelyAndLeavesProtectedPeerUntouched()
    {
        Set(); Set(2); var records = Records; var outside = Path.Combine(_temp.FullName, "payload"); File.WriteAllText(outside, "Never touch payload");
        var store = Store(checkpoint: phase => { if (phase == "before-removal") File.WriteAllText(Path.Combine(records, "intruder"), "Protected unknown bytes"); });
        Assert.Throws<IOException>(() => store.Reset(store.InspectReset(Inventory, Context, Token), Inventory, Context, Token));
        Assert.Equal("Never touch payload", File.ReadAllText(outside)); Assert.True(File.Exists(Path.Combine(records, "intruder")));
        Assert.NotNull(Read().Diagnostic); Assert.True(File.Exists(Path.Combine(Backup, "manifest.json")));
    }
}
