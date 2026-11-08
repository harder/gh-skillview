using SkillView.Inventory;
using SkillView.Preferences;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed partial class LocationPreferencesTests
{
    private SkillView.Inventory.SecureFileIdentity Inspect(string path)
    {
        SkillView.Inventory.SecureFileIdentity identity = default;
        RegularFileInspection.Capture(path, item =>
        {
            identity = item.Identity; var bytes = new byte[checked((int)item.Content!.Length)]; item.Content.ReadExactly(bytes);
            return new PayloadFileEvidence(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length);
        }, Token);
        return identity;
    }

    [Theory]
    [InlineData("save-created-generation")]
    [InlineData("save-created-file")]
    [InlineData("save-written")]
    public void FailedOrPartialSaveHasExplicitRawBackupReset(string phase)
    {
        var selected = Selection(); var store = Store();
        store.Checkpoint = step => { if (step == phase) throw new IOException("injected write failure"); };
        Assert.Throws<IOException>(() => store.Save(new([]), [selected], [], _temp.FullName, Token));
        store.Checkpoint = null;
        var original = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var review = store.PrepareReset([], Token); Assert.True(review.CanReset, review.Explanation);
        var result = store.Reset(review, false, [], Token); Assert.Empty(result.Locations);
        var backup = Directory.GetDirectories(Root, "backup-*").Single();
        foreach (var item in original) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(backup, item.Key!)));
        Assert.Null(store.Read([], Token).Diagnostic);
        var again = store.Save(result, [selected], [], _temp.FullName, Token); Assert.Equal([selected], again.Locations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{partial")]
    public void MalformedKnownRawRecordsResetWithoutInterpretingAndCannotCompact(string bytes)
    {
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        File.WriteAllText(Path.Combine(Records, "0001.json"), bytes);
        var read = store.Read([], Token); Assert.NotNull(read.Diagnostic);
        var review = store.PrepareReset([], Token); Assert.True(review.CanReset); Assert.False(review.CanCompact);
        Assert.Throws<IOException>(() => store.Reset(review, true, [], Token));
        store.Reset(review, false, [], Token);
        Assert.Equal(bytes, File.ReadAllText(Path.Combine(Directory.GetDirectories(Root, "backup-*").Single(), "0001.json")));
    }

    [Theory]
    [InlineData("backup-created")]
    [InlineData("backup-file")]
    [InlineData("before-manifest")]
    [InlineData("backup-verified")]
    [InlineData("before-remove")]
    [InlineData("before-remove-file")]
    [InlineData("removed-file")]
    [InlineData("before-remove-directory")]
    [InlineData("records-removed")]
    public void InterruptedResetReopensAndResumesOnlyVerifiedNativeSurvivors(string phase)
    {
        var first = Selection(); var second = Selection("workspace", "second"); var store = Store();
        var saved = store.Save(new([]), [first], [], _temp.FullName, Token);
        store.Save(saved, [second], [], _temp.FullName, Token);
        var originals = Directory.GetFiles(Records).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        using var cancel = new CancellationTokenSource(); var triggered = false;
        store.Checkpoint = step => { if (!triggered && step == phase) { triggered = true; cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reset(store.PrepareReset([], Token), false, [], cancel.Token));
        Assert.True(triggered);
        store = Store(); var review = store.PrepareReset([], Token);
        if (review.CanReset) store.Reset(review, false, [], Token);
        Assert.Empty(Directory.GetDirectories(Root, "records-*")); Assert.Null(store.Read([], Token).Diagnostic);
        var backup = Directory.GetDirectories(Root, "backup-*").Single();
        foreach (var item in originals) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(backup, item.Key!)));
    }

    [Theory]
    [InlineData("compact-created-generation")]
    [InlineData("compact-created-file")]
    [InlineData("records-removed")]
    public void InterruptedCompactionResumesValidSavedSnapshotWithoutOverwritingPrefix(string phase)
    {
        var selected = Selection(); var store = Store(); store.Save(new([]), [selected], [], _temp.FullName, Token);
        using var cancel = new CancellationTokenSource();
        store.Checkpoint = step => { if (step == phase) { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reset(store.PrepareReset([], Token), true, [], cancel.Token));
        store = Store(); var review = store.PrepareReset([], Token); Assert.True(review.CanCompact, review.Explanation);
        var resumed = store.Reset(review, true, [], Token); Assert.Equal([selected], resumed.Locations); Assert.Equal(1, resumed.UsedRecords);
        Assert.Single(Directory.GetDirectories(Root, "backup-*")); Assert.Single(Directory.GetDirectories(Root, "records-*"));
    }

    [Theory]
    [InlineData("stray")]
    [InlineData("subfolder")]
    [InlineData("oversize")]
    [InlineData("link")]
    public void ProtectedEntriesRefuseResetWithoutChangingAnyBytes(string kind)
    {
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        var record = Path.Combine(Records, "0001.json"); var before = File.ReadAllBytes(record);
        var path = Path.Combine(Records, kind == "oversize" || kind == "link" ? "0002.json" : "stray");
        if (kind == "subfolder") Directory.CreateDirectory(path);
        else if (kind == "link")
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Local fixture requires symlink rights; native Windows proof belongs to CI.");
            File.CreateSymbolicLink(path, record);
        }
        else File.WriteAllBytes(path, new byte[kind == "oversize" ? LocationPreferences.MaxRecordBytes + 1 : 1]);
        var review = store.PrepareReset([], Token); Assert.False(review.CanReset); Assert.False(review.CanCompact);
        Assert.Contains(path, review.Explanation); Assert.Throws<IOException>(() => store.Reset(review, false, [], Token));
        Assert.Equal(before, File.ReadAllBytes(record)); Assert.True(File.Exists(path) || Directory.Exists(path));
        Assert.Empty(Directory.GetDirectories(Root, "backup-*"));
    }

    [Fact]
    public void PartialBackupFilesAndManifestResumeWithoutOverwritingRetainedPrefixes()
    {
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        store.Checkpoint = step => { if (step == "backup-verified") throw new IOException("injected pre-remove stop"); };
        Assert.Throws<IOException>(() => store.Reset(store.PrepareReset([], Token), false, [], Token));
        store.Checkpoint = null;
        var backup = Directory.GetDirectories(Root, "backup-*").Single(); var manifest = Path.Combine(backup, "manifest.json");
        var completeManifest = File.ReadAllBytes(manifest);
        using (var file = new FileStream(manifest, FileMode.Open, FileAccess.Write)) file.SetLength(completeManifest.Length / 2);
        var raw = Path.Combine(backup, "0001.json"); var completeRaw = File.ReadAllBytes(raw);
        using (var file = new FileStream(raw, FileMode.Open, FileAccess.Write)) file.SetLength(completeRaw.Length / 2);
        Assert.NotNull(store.Read([], Token).Diagnostic);
        var review = store.PrepareReset([], Token); Assert.True(review.CanReset, review.Explanation);
        store.Reset(review, false, [], Token);
        Assert.Equal(completeManifest, File.ReadAllBytes(manifest)); Assert.Equal(completeRaw, File.ReadAllBytes(raw));
    }

    [Fact]
    public void HardLinkedRecordIsProtectedWithoutChangingItsOutsidePeer()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX hard-link fixture; Windows sharing/topology is exercised by native CI.");
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        var record = Path.Combine(Records, "0001.json"); var peer = Path.Combine(_temp.FullName, "outside-peer");
        var info = new System.Diagnostics.ProcessStartInfo("/bin/ln") { UseShellExecute = false };
        info.ArgumentList.Add(record); info.ArgumentList.Add(peer);
        using var child = System.Diagnostics.Process.Start(info)!; child.WaitForExit(); Assert.Equal(0, child.ExitCode);
        var before = Inspect(peer); var bytes = File.ReadAllBytes(peer);
        var review = store.PrepareReset([], Token); Assert.False(review.CanReset); Assert.Contains(record, review.Explanation);
        Assert.Throws<IOException>(() => store.Reset(review, false, [], Token));
        Assert.Equal(before, Inspect(peer)); Assert.Equal(bytes, File.ReadAllBytes(peer)); Assert.True(File.Exists(record));
    }

    [Fact]
    public void ResetEpochAndRootNativeContextRejectStaleWriterAndStaleReview()
    {
        var selected = Selection(); var store = Store(); var saved = store.Save(new([]), [selected], [], _temp.FullName, Token);
        var oldReview = store.PrepareReset([], Token); var reset = store.Reset(oldReview, false, [], Token);
        Assert.Throws<IOException>(() => store.Save(saved, [], [], _temp.FullName, Token));
        var fresh = store.Save(reset, [selected], [], _temp.FullName, Token);
        Assert.NotEqual(saved.Fingerprint, fresh.Fingerprint); Assert.NotEqual(saved.RecordsName, fresh.RecordsName);
        Assert.Throws<IOException>(() => store.Reset(oldReview, false, [], Token));
        var otherContext = new LocationPreferences(Root, "different HOME/CWD");
        Assert.NotNull(otherContext.Read([], Token).Diagnostic);
        Assert.False(otherContext.PrepareReset([], Token).CanReset);
    }

    [Fact]
    public void ChangedReviewOrBackupStopsAtFirstMismatchAndPreservesOutsideNativeObjects()
    {
        var selected = Selection(); var outside = Path.Combine(selected.Path, "private"); File.WriteAllText(outside, "untouched");
        var before = Inspect(outside);
        var store = Store(); var saved = store.Save(new([]), [selected], [], _temp.FullName, Token);
        var review = store.PrepareReset([], Token);
        File.AppendAllText(Path.Combine(Records, "0001.json"), " ");
        Assert.Throws<IOException>(() => store.Reset(review, false, [], Token));
        var fresh = store.PrepareReset([], Token);
        store.Checkpoint = step => { if (step == "backup-verified") File.AppendAllText(Path.Combine(Records, "0001.json"), " "); };
        Assert.Throws<IOException>(() => store.Reset(fresh, false, [], Token));
        Assert.True(Directory.Exists(Records)); Assert.NotEmpty(Directory.GetDirectories(Root, "backup-*"));
        Assert.Equal("untouched", File.ReadAllText(outside));
        var after = Inspect(outside); Assert.Equal(before, after);
    }

    [Fact]
    public void WindowsBlockingHandleRefusesThenCloseAllowsExplicitResume()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires actual Windows sharing/deletion behavior; not a local cross-OS claim.");
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        var record = Path.Combine(Records, "0001.json");
        var reviewed = store.PrepareReset([], Token); var bytes = File.ReadAllBytes(record);
        using (var blocked = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => store.Reset(reviewed, false, [], Token));
            Assert.Equal(bytes, File.ReadAllBytes(record));
        }
        var fresh = store.PrepareReset([], Token); Assert.True(fresh.CanReset, fresh.Explanation);
        store.Reset(fresh, false, [], Token);
        Assert.Empty(Directory.GetDirectories(Root, "records-*"));
    }

    [Fact]
    public void CanceledReviewNeverCreatesBackupAndLockAlwaysSurvives()
    {
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        var review = store.PrepareReset([], Token); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reset(review, false, [], canceled.Token));
        Assert.Empty(Directory.GetDirectories(Root, "backup-*")); Assert.True(File.Exists(Path.Combine(Root, "writer.lock")));
        Assert.Equal(0, new FileInfo(Path.Combine(Root, "writer.lock")).Length);
    }

    [Fact]
    public void FullLogAtLongCanonicalAddressHasBoundedManifestAndRemainsRecoverable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Long POSIX address fixture; Windows path policy belongs to native CI.");
        var parent = _temp.FullName;
        // Darwin's PATH_MAX is 1024 including the existing temp prefix. Stay
        // below it while retaining a long canonical address in every manifest.
        var components = OperatingSystem.IsMacOS() ? 4 : 9;
        for (var i = 0; i < components; i++) parent = Directory.CreateDirectory(Path.Combine(parent, new string('x', 180) + i)).FullName;
        Assert.True(parent.Length >= 700);
        var store = new LocationPreferences(Path.Combine(parent, "locations"));
        var first = Selection(); var second = Selection("workspace", "other"); var read = store.Read([], Token);
        for (var i = 0; i < LocationPreferences.MaxRecords; i++)
            read = store.Save(read, [i % 2 == 0 ? first : second], [], _temp.FullName, Token);
        var review = store.PrepareReset([], Token); Assert.True(review.CanCompact, review.Explanation);
        var compact = store.Reset(review, true, [], Token); Assert.Equal([second], compact.Locations); Assert.Equal(1, compact.UsedRecords);
        var backup = Directory.GetDirectories(store.Root, "backup-*").Single();
        Assert.InRange(new FileInfo(Path.Combine(backup, "manifest.json")).Length, 1, LocationPreferences.MaxRecordBytes);
        Assert.Equal(LocationPreferences.MaxRecords, Directory.GetFiles(backup, "0*.json").Length);
    }

    private string InterruptAfterLastUnlink()
    {
        var store = Store(); store.Save(new([]), [Selection()], [], _temp.FullName, Token);
        var records = Records;
        store.Checkpoint = step => { if (step == "removed-last-file-before-empty-witness") throw new IOException("injected death before empty receipt"); };
        Assert.Throws<IOException>(() => store.Reset(store.PrepareReset([], Token), false, [], Token));
        Assert.Empty(Directory.GetFileSystemEntries(records));
        Assert.False(File.Exists(Path.Combine(Directory.GetDirectories(Root, "backup-*").Single(), "empty.json")));
        return records;
    }

    [Fact]
    public void LastUnlinkWithoutEmptyWitnessReopensWithActionableResetAndPreservesBackupAndPeers()
    {
        var outside = Path.Combine(_temp.FullName, "outside-peer"); File.WriteAllText(outside, "private peer");
        var before = Inspect(outside); var records = InterruptAfterLastUnlink();
        var backup = Directory.GetDirectories(Root, "backup-*").Single();
        var raw = Directory.GetFiles(backup).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var store = Store(); var read = store.Read([], Token);
        Assert.Contains("records directory is empty; backup verified; run Reset to finish", read.Diagnostic);
        Assert.Contains(records, read.Diagnostic);
        var review = store.PrepareReset([], Token); Assert.True(review.CanReset, review.Explanation);
        var result = store.Reset(review, false, [], Token); Assert.Null(result.Diagnostic);
        Assert.False(Directory.Exists(records));
        foreach (var item in raw) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(backup, item.Key)));
        Assert.Equal(before, Inspect(outside)); Assert.Equal("private peer", File.ReadAllText(outside));
        Assert.True(File.Exists(Path.Combine(Root, "writer.lock"))); Assert.Equal(0, new FileInfo(Path.Combine(Root, "writer.lock")).Length);
    }

    [Theory]
    [InlineData("different-native-child")]
    [InlineData("nonempty-known-record")]
    public void EmptyRecoveryNeverAdmitsDifferentNativeChildOrNonemptyReplacement(string kind)
    {
        var records = InterruptAfterLastUnlink(); var backup = Directory.GetDirectories(Root, "backup-*").Single();
        var raw = File.ReadAllBytes(Path.Combine(backup, "0001.json"));
        if (kind == "different-native-child")
        {
            Directory.Move(records, Path.Combine(_temp.FullName, "preserved-original-empty-child"));
            using var replacement = NativeStateDirectory.OpenOrCreate(records, [], Token);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(records, "0001.json"), raw);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(records, "0001.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var store = Store(); Assert.NotNull(store.Read([], Token).Diagnostic);
        var review = store.PrepareReset([], Token);
        if (kind == "different-native-child") Assert.False(review.CanReset);
        Assert.Throws<IOException>(() => store.Reset(review, false, [], Token));
        Assert.True(Directory.Exists(records)); Assert.Equal(raw, File.ReadAllBytes(Path.Combine(backup, "0001.json")));
        if (kind == "nonempty-known-record") Assert.Equal(raw, File.ReadAllBytes(Path.Combine(records, "0001.json")));
    }

    [Theory]
    [InlineData("before-empty-remove")]
    [InlineData("before-empty-remove-directory")]
    public void EmptyOnlyCompletionCancellationLeavesNativeChildAndBackupForExplicitRetry(string phase)
    {
        var records = InterruptAfterLastUnlink(); var store = Store(); var review = store.PrepareReset([], Token);
        using var cancel = new CancellationTokenSource();
        store.Checkpoint = step => { if (step == phase) cancel.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reset(review, false, [], cancel.Token));
        Assert.True(Directory.Exists(records)); Assert.Empty(Directory.GetFileSystemEntries(records));
        store = Store(); store.Reset(store.PrepareReset([], Token), false, [], Token);
        Assert.False(Directory.Exists(records));
    }

    [Fact]
    public void EmptyOnlyCompletionRefusesAnyEntryAddedImmediatelyBeforeDirectoryDeletion()
    {
        var records = InterruptAfterLastUnlink(); var store = Store(); var review = store.PrepareReset([], Token);
        var protectedFile = Path.Combine(records, "stray");
        store.Checkpoint = step => { if (step == "before-empty-remove-directory") File.WriteAllText(protectedFile, "never delete"); };
        Assert.Throws<IOException>(() => store.Reset(review, false, [], Token));
        Assert.Equal("never delete", File.ReadAllText(protectedFile)); Assert.True(Directory.Exists(records));
    }

    [Fact]
    public void RootCreatedBeforeLockReadsAsMissingWithoutWritingAndSaveBootstrapsSameNativeRoot()
    {
        var selected = Selection(); var store = Store();
        store.Checkpoint = step => { if (step == "save-created-root-before-lock") throw new IOException("injected death before lock"); };
        Assert.Throws<IOException>(() => store.Save(new([]), [selected], [], _temp.FullName, Token));
        Assert.Empty(Directory.GetFileSystemEntries(Root));
        Assert.True(SecureRemovalBackend.TryObserveIdentity(Root, out var before, out _));
        store = Store(); var read = store.Read([], Token); Assert.Null(read.Diagnostic); Assert.Equal("missing", read.Fingerprint);
        Assert.Contains("Save your selection", read.RecoveryHint); Assert.Empty(Directory.GetFileSystemEntries(Root));
        Assert.True(SecureRemovalBackend.TryObserveIdentity(Root, out var afterRead, out _)); Assert.Equal(before, afterRead);
        var saved = store.Save(read, [selected], [], _temp.FullName, Token); Assert.Equal([selected], saved.Locations);
        Assert.Equal(saved.Fingerprint, Store().Read([], Token).Fingerprint);
        Assert.True(SecureRemovalBackend.TryObserveIdentity(Root, out var afterSave, out _)); Assert.Equal(before.FileIdLow, afterSave.FileIdLow); Assert.Equal(before.Volume, afterSave.Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootWithoutLockProtectsStrayRegularOrLinkedEntries(bool linked)
    {
        using (var state = NativeStateDirectory.OpenOrCreate(Root, [], Token)) { }
        var entry = Path.Combine(Root, "stray"); var peer = Path.Combine(_temp.FullName, "peer"); File.WriteAllText(peer, "untouched");
        if (linked)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX link fixture; no Windows privilege claim.");
            File.CreateSymbolicLink(entry, peer);
        }
        else File.WriteAllText(entry, "stray");
        var before = Inspect(peer); var read = Store().Read([], Token);
        Assert.NotNull(read.Diagnostic); Assert.Contains(entry, read.Diagnostic);
        Assert.Throws<IOException>(() => Store().Save(read, [Selection()], [], _temp.FullName, Token));
        Assert.False(File.Exists(Path.Combine(Root, "writer.lock"))); Assert.Equal(before, Inspect(peer));
    }

    [Fact]
    public async Task TwoMissingBootstrapsSerializeAndSecondCannotReuseMissingAfterPublication()
    {
        using (var state = NativeStateDirectory.OpenOrCreate(Root, [], Token)) { }
        var first = Store(); var second = Store(); var expectedFirst = first.Read([], Token); var expectedSecond = second.Read([], Token);
        Assert.Equal("missing", expectedFirst.Fingerprint); Assert.Equal("missing", expectedSecond.Fingerprint);
        var selected = Selection(); var other = Selection("workspace", "other-bootstrap");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        first.Checkpoint = step =>
        {
            if (step != "save-created-generation") return;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15), Token)) throw new TimeoutException("Bootstrap test did not release writer.");
        };
        var writer = Task.Run(() => first.Save(expectedFirst, [selected], [], _temp.FullName, Token), Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Token));
            Assert.Throws<IOException>(() => second.Save(expectedSecond, [other], [], _temp.FullName, Token));
        }
        finally { release.Set(); }
        var saved = await writer;
        Assert.Throws<IOException>(() => second.Save(expectedSecond, [other], [], _temp.FullName, Token));
        Assert.Equal([selected], second.Read([], Token).Locations); Assert.Equal(1, saved.UsedRecords);
        Assert.Single(Directory.GetFiles(Records)); Assert.Single(Directory.GetDirectories(Root, "records-*"));
    }
}
