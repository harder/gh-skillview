using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class QuarantineStoreTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-quarantine-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary.FullName, "state");
    private QuarantineStore Store => new(State, new Logger());
    public QuarantineStoreTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), "---\nname: demo\ndescription: Fixture\n---\nBody\n");
        Directory.CreateDirectory(Path.Combine(Payload, "empty"));
        File.WriteAllBytes(Path.Combine(Payload, "data"), [0, 255, 3]);
    }
    public void Dispose() => temporary.Delete(true);
    private InventorySnapshot Inventory() => InventorySnapshot.Empty with
    {
        Skills = new LocalSkillScanner(new Logger()).Scan([new(Skills, Scope.Custom, null)]).ToImmutableArray(),
        ScannedRoots = [new(Skills, Scope.Custom, null)],
        ConfiguredRoots = [new(Skills, Scope.Custom, null)]
    };
    private QuarantineResult Apply()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var review = Store.Preview(target, inventory, Token);
        var result = Store.Apply(target, inventory, review.ReviewFingerprint, true, Token);
        Assert.True(result.Succeeded, result.Message); return result;
    }

    [Fact]
    public void PreviewIsReadOnlyAndRepeatedHeldCapturesAreComplete()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        var first = Store.Preview(target, inventory, Token); var second = Store.Preview(target, inventory, Token);
        Assert.Equal(3, first.EntryCount); Assert.Equal(first.ReviewFingerprint, second.ReviewFingerprint);
        Assert.False(Directory.Exists(State));
        using var lease = NativeDirectoryMoveLease.Open(Skills, Payload, Token);
        var before = lease.Capture(Token); lease.Revalidate(Token); var after = lease.Capture(Token);
        Assert.Equal(3, after.Entries.Length); Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, after.ObjectStateFingerprint);
    }

    [Fact]
    public void ApplyAndUndoRetainOriginalNativeDirectoryAndAllPayloadEvidence()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Payload, "data"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var result = Apply(); Assert.False(Directory.Exists(Payload)); Assert.True(Directory.Exists(result.RetainedPath));
        var retained = PayloadSnapshot.Capture(result.RetainedPath, Token);
        Assert.Equal(before.Fingerprint, retained.Fingerprint);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, retained.RootIdentity!.Value));
        Assert.Equal("quarantined", Assert.Single(Store.History(Inventory(), Token)).Status);
        var preview = Store.Undo(result.Id, Inventory(), false, Token); Assert.True(preview.Succeeded, preview.Message);
        Assert.False(Directory.Exists(Payload));
        var restored = Store.Undo(result.Id, Inventory(), true, Token); Assert.True(restored.Succeeded, restored.Message);
        Assert.True(restored.MutationObserved); Assert.False(Directory.Exists(result.RetainedPath));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        var again = Store.Undo(result.Id, Inventory(), true, Token); Assert.True(again.Succeeded, again.Message); Assert.False(again.MutationObserved);
        Assert.Equal("restored", Assert.Single(Store.History(Inventory(), Token)).Status);
    }

    [Fact]
    public void StaleReviewAndMissingStandaloneConfirmationDoNotCreateState()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills); var review = Store.Preview(target, inventory, Token);
        Assert.Throws<IOException>(() => Store.Apply(target, inventory, review.ReviewFingerprint, false, Token));
        File.AppendAllText(Path.Combine(Payload, "data"), "changed");
        Assert.Throws<IOException>(() => Store.Apply(target, inventory, review.ReviewFingerprint, true, Token));
        Assert.True(Directory.Exists(Payload)); Assert.False(Directory.Exists(State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccupiedOriginalOrModifiedRetainedPayloadRefusesUndoWithoutMutation(bool modified)
    {
        var result = Apply();
        if (modified) File.AppendAllText(Path.Combine(result.RetainedPath, "data"), "changed");
        else { Directory.CreateDirectory(Payload); File.WriteAllText(Path.Combine(Payload, "replacement"), "keep"); }
        Assert.Throws<IOException>(() => Store.Undo(result.Id, Inventory(), true, Token));
        Assert.True(Directory.Exists(result.RetainedPath));
        Assert.False(File.Exists(Path.Combine(State, result.Id, "undo-prepared.json")));
        if (!modified) Assert.Equal("keep", File.ReadAllText(Path.Combine(Payload, "replacement")));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".skill-lock.json")]
    public void RepositoryAndOwnerMarkersRefusePreview(string marker)
    {
        File.WriteAllText(Path.Combine(Payload, marker), marker == ".git" ? "gitdir: absent" : "{broken");
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        Assert.Throws<IOException>(() => Store.Preview(target, inventory, Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void PartialInventoryAndStateInsideSkillsRefuse()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        Assert.Throws<IOException>(() => Store.Preview(target, inventory with { ConfiguredRootsComplete = false }, Token));
        Assert.Throws<IOException>(() => new QuarantineStore(Path.Combine(Payload, "state"), new Logger()).Preview(target, inventory, Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void CanceledAdmissionDoesNotMoveOrCreateState()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills); var review = Store.Preview(target, inventory, Token);
        Assert.Throws<OperationCanceledException>(() => Store.Apply(target, inventory, review.ReviewFingerprint, true, new CancellationToken(true)));
        Assert.True(Directory.Exists(Payload)); Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void IncompleteOrCorruptJournalIsVisibleAndNeverGuessesRecovery()
    {
        var result = Apply(); File.Delete(Path.Combine(State, result.Id, "quarantined.json"));
        Assert.Equal("prepared", Assert.Single(Store.History(Inventory(), Token)).Status);
        var undo = Store.Undo(result.Id, Inventory(), true, Token); Assert.False(undo.Succeeded); Assert.False(undo.MutationObserved);
        File.WriteAllText(Path.Combine(State, result.Id, "prepared.json"), "{}");
        Assert.Equal("needs-inspection", Assert.Single(Store.History(Inventory(), Token)).Status);
        Assert.True(Directory.Exists(result.RetainedPath)); Assert.False(Directory.Exists(Payload));
    }
    [Fact]
    public void NativeMoveRefusesOccupiedDestinationAndKeepsBothDirectories()
    {
        using var destination = NativeStateDirectory.OpenOrCreate(State, [Skills], Token);
        Directory.CreateDirectory(Path.Combine(State, "payload"));
        using var lease = NativeDirectoryMoveLease.Open(Skills, Payload, Token);
        var before = lease.Capture(Token);
        Assert.Throws<IOException>(() => lease.MoveToState(destination, "payload", Token));
        Assert.False(lease.MutationCompleted); Assert.True(Directory.Exists(Payload));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(State, "payload")));
    }

    [Fact]
    public void ChangedOriginalParentGenerationRefusesUndo()
    {
        var result = Apply(); File.WriteAllText(Path.Combine(Skills, "new-entry"), "keep");
        Assert.Throws<IOException>(() => Store.Undo(result.Id, Inventory(), true, Token));
        Assert.True(Directory.Exists(result.RetainedPath)); Assert.False(Directory.Exists(Payload));
    }

    [Fact]
    public void FailureAfterPreparedPublicationRetainsSourceAndExplicitIncompleteHistory()
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills); var review = Store.Preview(target, inventory, Token);
        var failing = new QuarantineStore(State, new Logger(), name => { if (name == "prepared.json") throw new IOException("fixture journal failure"); });
        Assert.Throws<IOException>(() => failing.Apply(target, inventory, review.ReviewFingerprint, true, Token));
        Assert.True(Directory.Exists(Payload));
        Assert.Equal("prepared", Assert.Single(Store.History(Inventory(), Token)).Status);
        Assert.Throws<IOException>(() => Store.Apply(target, inventory, review.ReviewFingerprint, true, Token));
        Assert.True(Directory.Exists(Payload));
    }

    [Fact]
    public void CancellationAfterUndoJournalLeavesRetainedPayloadAndRefusesAutomaticRetry()
    {
        var result = Apply(); using var cancellation = new CancellationTokenSource();
        var canceling = new QuarantineStore(State, new Logger(), name => { if (name == "undo-prepared.json") cancellation.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => canceling.Undo(result.Id, Inventory(), true, cancellation.Token));
        Assert.True(Directory.Exists(result.RetainedPath)); Assert.False(Directory.Exists(Payload));
        Assert.Equal("undo-prepared", Assert.Single(Store.History(Inventory(), Token)).Status);
        var retry = Store.Undo(result.Id, Inventory(), true, Token); Assert.False(retry.Succeeded); Assert.False(retry.MutationObserved);
    }

    [Fact]
    public void DuplicateAndOversizedJournalPropertiesAreRefusedWithoutMovingData()
    {
        var result = Apply(); var journal = Path.Combine(State, result.Id, "prepared.json");
        var valid = File.ReadAllText(journal);
        File.WriteAllText(journal, valid.Insert(valid.IndexOf('{') + 1, "\"schemaVersion\":1,"));
        Assert.Equal("needs-inspection", Assert.Single(Store.History(Inventory(), Token)).Status);
        Assert.Throws<IOException>(() => Store.Undo(result.Id, Inventory(), true, Token));
        using (var file = new FileStream(journal, FileMode.Open)) file.SetLength(1024 * 1024 + 1);
        Assert.Equal("needs-inspection", Assert.Single(Store.History(Inventory(), Token)).Status);
        Assert.True(Directory.Exists(result.RetainedPath));
    }

    [Fact]
    public void NewlyDiscoveredConsumerOfRetainedDirectoryRefusesUndo()
    {
        var result = Apply();
        var inventory = Inventory();
        var retained = Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(Path.GetDirectoryName(result.RetainedPath)!, Scope.User, "claude")]));
        inventory = inventory with { Skills = [retained] };
        Assert.Throws<IOException>(() => Store.Undo(result.Id, inventory, true, Token));
        Assert.True(Directory.Exists(result.RetainedPath)); Assert.False(Directory.Exists(Payload));
        Assert.False(File.Exists(Path.Combine(State, result.Id, "undo-prepared.json")));
    }

}
