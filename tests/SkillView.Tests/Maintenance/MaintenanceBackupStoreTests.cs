using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class MaintenanceBackupStoreTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-backup-");
    public void Dispose() => _temporary.Delete(true);
    private string State => Path.Combine(_temporary.FullName, "state");
    private InstalledSkill Target(string? markdown = null)
    {
        var skills = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "demo")).FullName;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), markdown ?? "---\nname: demo\ndescription: Test\n---\nBody\n");
        return Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(skills, Scope.Custom, null)]));
    }

    [Fact]
    public void ArchivePreservesBytesModesEmptyDirectoriesAndLegacyExclusionsWithoutMutatingSource()
    {
        var target = Target();
        var nested = Directory.CreateDirectory(Path.Combine(target.ResolvedPath, "scripts")).FullName;
        var script = Path.Combine(nested, "run.sh");
        File.WriteAllBytes(script, [0, 1, 255, 4]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(target.ResolvedPath, "empty"));
        File.WriteAllText(Path.Combine(target.ResolvedPath, ".skillview-ignore"), "keep");
        var before = PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken);
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        Assert.Equal(before.Fingerprint, result.BeforeFingerprint);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken).Fingerprint);
        Assert.Equal("captured", result.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.ArchivePath))), result.ArchiveHash);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var row in manifest.RootElement.GetProperty("entries").EnumerateArray())
        {
            var entry = Assert.Single(before.Entries, entry => entry.Path == row.GetProperty("path").GetString());
            Assert.Equal(entry.Kind, row.GetProperty("kind").GetString());
            if (entry.Kind == "directory") continue;
            using var stream = archive.GetEntry(row.GetProperty("blob").GetString()!)!.Open();
            Assert.Equal(entry.ContentHash, Convert.ToHexString(SHA256.HashData(stream)));
            if (entry.UnixMode is { } mode) Assert.Equal(mode, row.GetProperty("unixMode").GetInt32());
        }
        using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, result.Id + ".captured.json")));
        Assert.Equal(result.ArchiveHash, journal.RootElement.GetProperty("archiveHash").GetString());
        Assert.True(File.Exists(Path.Combine(State, result.Id + ".creating.json")));
    }

    [Fact]
    public void NativeLinksAreArchivedAsLeafBytesRatherThanFollowingTheirTargets()
    {
        var target = Target();
        var outside = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(target.ResolvedPath, "link"), outside);
        var record = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(record.ArchivePath);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var rows = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.DoesNotContain(rows, row => row.GetProperty("path").GetString()!.Contains("secret", StringComparison.Ordinal));
        var link = Assert.Single(rows, row => row.GetProperty("kind").GetString() == "link");
        Assert.True(link.GetProperty("blobBytes").GetInt64() > 0);
        Assert.Equal(archive.GetEntry(link.GetProperty("blob").GetString()!)!.Length, link.GetProperty("blobBytes").GetInt64());
        using var stream = archive.GetEntry(link.GetProperty("blob").GetString()!)!.Open();
        Assert.Equal(link.GetProperty("hash").GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "secret")));
    }

    [Fact]
    public void InvalidSkillMarkdownCanStillBeCapturedAsRawRecoveryEvidence()
    {
        var target = Target("damaged frontmatter\n");
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        Assert.Equal("captured", result.Status);
        Assert.Equal("damaged frontmatter\n", File.ReadAllText(Path.Combine(target.ResolvedPath, "SKILL.md")));
    }

    [Fact]
    public void FlowTrackingMetadataIsArchivedWithoutComparisonNormalization()
    {
        const string markdown = "---\nname: demo\ndescription: Test\nmetadata: {github-ref: main}\n---\nBody\n";
        var target = Target(markdown);
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        Assert.Equal("captured", result.Status);
        Assert.Equal(markdown, File.ReadAllText(Path.Combine(target.ResolvedPath, "SKILL.md")));
        Assert.Null(PayloadSnapshot.CaptureEvidence(target.ResolvedPath, TestContext.Current.CancellationToken).FrontMatter);
    }

    [Fact]
    public void InvalidUtf8IsArchivedAsRawBytes()
    {
        var target = Target();
        byte[] expected = [0xFF, 0xFE, 0, 42];
        File.WriteAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md"), expected);
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var row = Assert.Single(manifest.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("path").GetString() == "SKILL.md");
        using var blob = archive.GetEntry(row.GetProperty("blob").GetString()!)!.Open();
        using var content = new MemoryStream(); blob.CopyTo(content);
        Assert.Equal(expected, content.ToArray());
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target.ResolvedPath, "SKILL.md")));
    }

    [Fact]
    public void RawMarkdownMayExceedTheComparisonParserLimitWithinTheAggregatePayloadBudget()
    {
        var target = Target();
        var path = Path.Combine(target.ResolvedPath, "SKILL.md");
        using (var source = new FileStream(path, FileMode.Append)) source.SetLength(PayloadSnapshot.MaxSkillBytes + 2048L);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var row = Assert.Single(manifest.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("path").GetString() == "SKILL.md");
        using var blob = archive.GetEntry(row.GetProperty("blob").GetString()!)!.Open();
        Assert.Equal(before, SHA256.HashData(blob));
        Assert.Equal(PayloadSnapshot.MaxSkillBytes + 2048L, row.GetProperty("blobBytes").GetInt64());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProspectiveMissingManagedRootIsExcludedThroughAnAliasBeforeCreatingParents(bool stateThroughAlias)
    {
        var target = Target();
        var home = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "future-home")).FullName;
        var alias = Path.Combine(_temporary.FullName, "home-alias");
        Directory.CreateSymbolicLink(alias, home);
        var managed = Path.Combine(alias, ".agents", "skills");
        var state = Path.Combine(stateThroughAlias ? alias : home, ".agents", "skills", "SkillView", "maintenance");
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(state).Capture(target, TestContext.Current.CancellationToken,
            managedRoots: [new(managed, Scope.User, "agents")]));
        Assert.False(Directory.Exists(Path.Combine(home, ".agents")));
    }

    [Fact]
    public void WindowsReadableNonDeletablePayloadCanBeBackedUp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Target();
        var payload = new DirectoryInfo(target.ResolvedPath);
        var parent = payload.Parent!;
        var originalPayload = payload.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var sourceFile = Path.Combine(payload.FullName, "SKILL.md");
        var originalBytes = File.ReadAllBytes(sourceFile);
        var originalParent = parent.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var originalFile = new FileInfo(sourceFile).GetAccessControl().GetSecurityDescriptorBinaryForm();
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User!;
        var payloadAcl = new DirectorySecurity();
        payloadAcl.SetAccessRuleProtection(true, false);
        payloadAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        payloadAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Delete, AccessControlType.Deny));
        var parentAcl = parent.GetAccessControl();
        parentAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
        try
        {
            parent.SetAccessControl(parentAcl); payload.SetAccessControl(payloadAcl);
            var reviewedAcl = payload.GetAccessControl().GetSecurityDescriptorBinaryForm();
            Assert.Equal(originalBytes, File.ReadAllBytes(sourceFile));
            Assert.False(SecureRemovalBackend.TryCaptureIdentity(payload.FullName, out _, out _));
            var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
            Assert.Equal("captured", result.Status);
            Assert.True(File.Exists(result.ArchivePath));
            Assert.Equal(reviewedAcl, payload.GetAccessControl().GetSecurityDescriptorBinaryForm());
            Assert.Equal(originalBytes, File.ReadAllBytes(sourceFile));
        }
        finally
        {
            // Rehydrate to mark DACL sections modified; an unchanged descriptor
            // returned by GetAccessControl can otherwise persist no changes.
            var restoreParent = new DirectorySecurity();
            restoreParent.SetSecurityDescriptorBinaryForm(originalParent, AccessControlSections.Access);
            parent.SetAccessControl(restoreParent);
            var restorePayload = new DirectorySecurity();
            restorePayload.SetSecurityDescriptorBinaryForm(originalPayload, AccessControlSections.Access);
            payload.SetAccessControl(restorePayload);
            var restoreFile = new FileSecurity();
            restoreFile.SetSecurityDescriptorBinaryForm(originalFile, AccessControlSections.Access);
            new FileInfo(sourceFile).SetAccessControl(restoreFile);
        }
    }

    [Fact]
    public void PreCanceledCaptureDoesNotCreateState()
    {
        var target = Target();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new MaintenanceBackupStore(State).Capture(target, canceled.Token));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void StateInsideThePayloadIsRefusedBeforeCreation()
    {
        var target = Target();
        var state = Path.Combine(target.ResolvedPath, "state");
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(state).Capture(target, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void SameContentReplacementBetweenInspectionAndArchive_RemainsIncomplete()
    {
        var target = Target();
        var file = Path.Combine(target.ResolvedPath, "SKILL.md");
        var expected = File.ReadAllBytes(file);
        var store = new MaintenanceBackupStore(State, _ => { }, () =>
        {
            var replacement = Path.Combine(_temporary.FullName, "replacement");
            File.WriteAllBytes(replacement, expected);
            File.Move(replacement, file, overwrite: true);
        });
        Assert.Throws<IOException>(() => store.Capture(target, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(State, "*.creating.json"));
        Assert.Empty(Directory.EnumerateFiles(State, "*.captured.json"));
        Assert.Equal(expected, File.ReadAllBytes(file));
    }

    [Fact]
    public void RootModeChangeBetweenInspectionAndArchive_RemainsIncomplete()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        var original = File.GetUnixFileMode(target.ResolvedPath);
        try
        {
            var store = new MaintenanceBackupStore(State, _ => { }, () =>
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target.ResolvedPath, original ^ UnixFileMode.OtherExecute);
            });
            Assert.Throws<IOException>(() => store.Capture(target, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFiles(State, "*.creating.json"));
            Assert.Empty(Directory.EnumerateFiles(State, "*.captured.json"));
        }
        finally { File.SetUnixFileMode(target.ResolvedPath, original); }
    }

    [Fact]
    public void ChangedSourceRetainsCreatingJournalAndNeverPublishesCapturedEvidence()
    {
        var target = Target();
        var store = new MaintenanceBackupStore(State, entry =>
        {
            if (entry == "SKILL.md") File.AppendAllText(Path.Combine(target.ResolvedPath, entry), "local edit");
        });
        Assert.Throws<IOException>(() => store.Capture(target, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(State, "*.creating.json"));
        Assert.Empty(Directory.EnumerateFiles(State, "*.captured.json"));
        Assert.Contains("local edit", File.ReadAllText(Path.Combine(target.ResolvedPath, "SKILL.md")));
    }

    [Fact]
    public void CooperativeWriterContentionRefusesBeforeStartingAnotherJournal()
    {
        var target = Target();
        new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        var count = Directory.EnumerateFiles(State, "*.json").Count();
        using var competing = new FileStream(Path.Combine(State, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken));
        Assert.Equal(count, Directory.EnumerateFiles(State, "*.json").Count());
    }

    [Fact]
    public void WriterLockCannotLeakIntoAChildThatOutlivesTheCapture()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        Process? child = null;
        try
        {
            var store = new MaintenanceBackupStore(State, _ =>
            {
                child ??= Process.Start(new ProcessStartInfo("/bin/sleep")
                { UseShellExecute = false, ArgumentList = { "20" } });
            });
            store.Capture(target, TestContext.Current.CancellationToken);
            Assert.NotNull(child);
            Assert.False(child.HasExited);
            using var nextWriter = new FileStream(Path.Combine(State, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(0, nextWriter.Length);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                Assert.True(child.WaitForExit(5000));
                child.Dispose();
            }
        }
    }

    [Fact]
    public void LinkedStateRootIsRefusedWithoutChangingItsTarget()
    {
        var target = Target();
        var outside = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "outside")).FullName;
        Directory.CreateSymbolicLink(State, outside);
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void StateResolutionUsesTheCapturedChildEnvironmentAndWorkingDirectory()
    {
        var runner = new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        { ["HOME"] = "home", ["USERPROFILE"] = "home", ["SKILLVIEW_STATE_HOME"] = "state" }, defaultWorkingDirectory: _temporary.FullName);
        Assert.Equal(Path.Combine(_temporary.FullName, "state", "SkillView", "maintenance"), MaintenanceBackupStore.ResolveRoot(runner));
    }

    [Fact]
    public void RetargetedStateAncestorCannotRedirectPublicationIntoThePayload()
    {
        var target = Target();
        var outside = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(target.ResolvedPath, "state"));
        var alias = Path.Combine(_temporary.FullName, "alias");
        Directory.CreateSymbolicLink(alias, outside);
        var changed = false;
        var store = new MaintenanceBackupStore(Path.Combine(alias, "state"), _ =>
        {
            if (changed) return;
            changed = true;
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, target.ResolvedPath);
        });
        var before = PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken);
        var result = store.Capture(target, TestContext.Current.CancellationToken);
        Assert.True(changed);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(PathIdentity.IsInside(result.ArchivePath, OwnershipPath.CanonicalCandidate(outside)!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(target.ResolvedPath, "state")));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken).Fingerprint);
    }

    [Fact]
    public void StateInsideAnotherKnownInstallationIsRefusedBeforeCreatingAnything()
    {
        var target = Target();
        var otherPath = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "another-payload")).FullName;
        var other = target with { ResolvedPath = otherPath };
        var state = Path.Combine(otherPath, "state");
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(state).Capture(target,
            TestContext.Current.CancellationToken, knownInstallations: [target, other]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(otherPath));
    }

    [Fact]
    public void UnlistedSkillBearingAncestorIsAlsoRefused()
    {
        var target = Target();
        var other = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "unlisted")).FullName;
        File.WriteAllText(Path.Combine(other, "SKILL.md"), "skill payload");
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(Path.Combine(other, "state")).Capture(target, TestContext.Current.CancellationToken));
        Assert.Equal(["SKILL.md"], Directory.EnumerateFileSystemEntries(other).Select(Path.GetFileName));
    }

    [Fact]
    public void UnrelatedSiblingChangesAreRefreshedRatherThanInvalidatingTheHeldAncestorChain()
    {
        var changed = false;
        using var state = NativeStateDirectory.OpenOrCreate(State, [], TestContext.Current.CancellationToken,
            beforeAncestorRefresh: () =>
            {
                if (changed) return;
                changed = true;
                File.WriteAllText(Path.Combine(_temporary.FullName, "unrelated"), "sibling");
            });
        Assert.True(changed);
        using var file = state.OpenFile("proof", false, TestContext.Current.CancellationToken);
        Assert.True(file.CanWrite);
    }

    [Fact]
    public void NewlyIntroducedAncestorMetadataRefusesBeforeStateCreation()
    {
        var changed = false;
        Assert.Throws<IOException>(() =>
        {
            using var state = NativeStateDirectory.OpenOrCreate(State, [], TestContext.Current.CancellationToken,
                beforeAncestorRefresh: () =>
                {
                    if (changed) return;
                    changed = true;
                    File.WriteAllText(Path.Combine(_temporary.FullName, "SKILL.md"), "new skill");
                });
        });
        Assert.True(changed);
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void EveryGeneratedUnixStateEntryHasExactPrivatePermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(State));
        foreach (var entry in Directory.EnumerateFiles(State))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(entry));
    }

    [Fact]
    public void RenamedKnownPayloadWithoutMetadataCannotBecomeState()
    {
        var known = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "known-payload")).FullName;
        File.WriteAllText(Path.Combine(known, "keep.txt"), "unchanged");
        var changed = false;
        Assert.Throws<IOException>(() =>
        {
            using var state = NativeStateDirectory.OpenOrCreate(State, [known], TestContext.Current.CancellationToken, path =>
            {
                if (changed) return;
                changed = true;
                Directory.Move(path, path + "-original");
                Directory.Move(known, path);
            });
            using var unexpected = state.OpenFile("writer.lock", true, TestContext.Current.CancellationToken);
        });
        Assert.True(changed);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(State).Select(Path.GetFileName));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(State, "keep.txt")));
    }

    [Fact]
    public void UnixDirectoryExchangeNeverRewritesReplacementPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var replacement = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "replacement")).FullName;
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(replacement, expected);
        Assert.Throws<IOException>(() =>
        {
            using var state = NativeStateDirectory.OpenOrCreate(State, [], TestContext.Current.CancellationToken, path =>
            {
                Directory.Delete(path);
                Directory.Move(replacement, path);
            });
        });
        Assert.Equal(expected, File.GetUnixFileMode(State));
        Assert.Empty(Directory.EnumerateFileSystemEntries(State));
    }

    [Fact]
    public void PhysicalStateMoveRetainsIncompleteEvidenceWithoutCapturedPublication()
    {
        var target = Target();
        var moved = Path.Combine(_temporary.FullName, "moved-state");
        var attempted = false;
        var moveSucceeded = false;
        var store = new MaintenanceBackupStore(State, _ =>
        {
            if (attempted) return;
            attempted = true;
            Directory.Move(State, moved);
            moveSucceeded = true;
        });
        var before = PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => store.Capture(target, TestContext.Current.CancellationToken));
        Assert.True(attempted);
        // Windows can refuse the rename while the exclusive archive handle is
        // open; Unix can move it, after which publication must refuse.
        var retained = moveSucceeded ? moved : State;
        Assert.True(Directory.Exists(retained));
        Assert.False(Directory.Exists(moveSucceeded ? State : moved));
        if (!moveSucceeded) Assert.True(OperatingSystem.IsWindows());
        Assert.Single(Directory.EnumerateFiles(retained, "*.creating.json"));
        Assert.Single(Directory.EnumerateFiles(retained, "*.svbackup"));
        Assert.Empty(Directory.EnumerateFiles(retained, "*.captured.json"));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken).Fingerprint);
    }

    [Fact]
    public void StoreQuotaRefusesBeforeAnotherJournalWithoutPruningHistory()
    {
        var target = Target();
        new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        using (var oversized = new FileStream(Path.Combine(State, "retained-evidence"), FileMode.CreateNew))
            oversized.SetLength(MaintenanceBackupStore.MaxStoredBytes);
        var previous = Directory.EnumerateFiles(State).Order().ToArray();
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken));
        Assert.Equal(previous, Directory.EnumerateFiles(State).Order());
        Assert.Equal(MaintenanceBackupStore.MaxStoredBytes, new FileInfo(Path.Combine(State, "retained-evidence")).Length);
    }

    [Fact]
    public void ExistingPermissiveWriterLockIsRefusedWithoutRewritingItsPermissions()
    {
        var target = Target();
        new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        var path = Path.Combine(State, "writer.lock");
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var before = OperatingSystem.IsWindows() ? new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()
            : BitConverter.GetBytes((int)File.GetUnixFileMode(path));
        var previous = Directory.EnumerateFiles(State).Order().ToArray();
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken));
        var after = OperatingSystem.IsWindows() ? new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()
            : BitConverter.GetBytes((int)File.GetUnixFileMode(path));
        Assert.Equal(before, after);
        Assert.Equal(previous, Directory.EnumerateFiles(State).Order());
    }

    [Fact]
    public void CanceledArchiveRetainsCreatingEvidenceWithoutPublishingCaptured()
    {
        var target = Target();
        using var canceled = new CancellationTokenSource();
        var before = PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken);
        var store = new MaintenanceBackupStore(State, _ => canceled.Cancel());
        Assert.Throws<OperationCanceledException>(() => store.Capture(target, canceled.Token));
        Assert.Single(Directory.EnumerateFiles(State, "*.creating.json"));
        Assert.Empty(Directory.EnumerateFiles(State, "*.captured.json"));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(target.ResolvedPath, TestContext.Current.CancellationToken).Fingerprint);
    }

    [Fact]
    public void WindowsStateFilesHavePrivateAclAndSharedStateIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Target();
        var result = new MaintenanceBackupStore(State).Capture(target, TestContext.Current.CancellationToken);
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User!;
        foreach (var path in new[] { State, result.ArchivePath, Path.Combine(State, result.Id + ".captured.json") })
        {
            FileSystemSecurity descriptor = path == State ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            Assert.True(descriptor.AreAccessRulesProtected);
            foreach (FileSystemAccessRule rule in descriptor.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.True(rule.IdentityReference == user || ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        }
        var shared = Directory.CreateDirectory(Path.Combine(_temporary.FullName, "shared-state"));
        var acl = shared.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        shared.SetAccessControl(acl);
        Assert.Throws<IOException>(() => new MaintenanceBackupStore(shared.FullName).Capture(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(shared.FullName));
    }
}
