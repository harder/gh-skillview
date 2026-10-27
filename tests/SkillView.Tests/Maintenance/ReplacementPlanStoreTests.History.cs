using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementPlanStoreTests
{
    [Fact]
    public void HistoryDoesNotCreateMissingStateAndCancellationWins()
    {
        var history = Store().ReadHistory(Token);
        Assert.Empty(history.Entries); Assert.Equal(0, history.ObservedCount); Assert.Null(history.Error);
        Assert.False(Directory.Exists(State));
        Assert.Throws<OperationCanceledException>(() => Store().ReadHistory(new CancellationToken(true)));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task HistoryHintsFollowActualReadyAppliedAndUndoneTopologyWithoutApprovals()
    {
        var plan = await Plan();
        var entry = Assert.Single(Store().ReadHistory(Token).Entries);
        Assert.Contains("Unapplied review (hint)", entry.Hint); Assert.False(entry.MayNeedRecovery);
        Assert.Equal(plan.Subject.Name, entry.Skill); Assert.Equal(plan.OriginalPath, entry.OriginalPath);
        Assert.Equal(plan.Subject.Metadata.Upstream, entry.Source);
        Assert.Contains("candidate", entry.Slots); Assert.True(entry.RecordedSlotBytes > 0);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var candidate = PayloadSnapshot.Capture(plan.CandidatePath, Token);
        _ = Store().ReadHistory(Token);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.Equal(candidate.ObjectStateFingerprint, PayloadSnapshot.Capture(plan.CandidatePath, Token).ObjectStateFingerprint);
        Apply(plan);
        entry = Assert.Single(Store().ReadHistory(Token).Entries);
        Assert.Contains("Applied / Undo data retained (hint)", entry.Hint);
        var observation = Store().Inspect(plan.Id, Inventory(), Token);
        var undone = Store().Recover(plan.Id, observation.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Contains("Undone (hint)", Assert.Single(Store().ReadHistory(Token).Entries).Hint);
    }

    [Theory]
    [InlineData("retain-moved")]
    [InlineData("install-moved")]
    [InlineData("displace-moved")]
    public async Task RestartHintsRetainInterruptedEvidenceAndNeverResume(string stop)
    {
        var plan = await Plan();
        if (stop == "displace-moved") Apply(plan);
        var store = Store(checkpoint: point => { if (point == stop) throw new IOException("Injected interruption"); });
        var result = stop == "displace-moved"
            ? store.Recover(plan.Id, Store().Inspect(plan.Id, Inventory(), Token).ReviewFingerprint, Inventory(), true, Token)
            : store.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.False(result.Succeeded);
        var paths = new[] { Payload, plan.CandidatePath, plan.RetainedPath, plan.DisplacedPath };
        var before = paths.Where(Directory.Exists).ToDictionary(path => path, path => PayloadSnapshot.Capture(path, Token).ObjectStateFingerprint);
        var history = Store().ReadHistory(Token);
        var entry = Assert.Single(history.Entries);
        if (stop != "install-moved") Assert.True(entry.MayNeedRecovery);
        Assert.Contains("Inspect to verify", entry.Hint);
        foreach (var pair in before) Assert.Equal(pair.Value, PayloadSnapshot.Capture(pair.Key, Token).ObjectStateFingerprint);
        Assert.Equal(before.Keys.Order(), paths.Where(Directory.Exists).Order());
    }

    [Theory]
    [InlineData("local-review")]
    [InlineData("merged-00")]
    [InlineData("undo-recovery")]
    [InlineData("owner-journal")]
    [InlineData("unknown-slot")]
    public async Task HistoricalMergeUnknownAndJournalHintsRemainProtected(string name)
    {
        var plan = await Plan();
        var path = Path.Combine(State, plan.Id, name);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)448);
        var entry = Assert.Single(Store().ReadHistory(Token).Entries);
        Assert.Contains("Inspect to verify", entry.Hint);
        if (name is "owner-journal" or "undo-recovery") Assert.True(entry.MayNeedRecovery);
        else Assert.StartsWith("Protected", entry.Hint);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ListingCannotVerifyPayloadChangesButExplicitInspectDoes()
    {
        var plan = await Plan();
        var bytes = File.ReadAllBytes(plan.PlanPath);
        File.WriteAllText(Path.Combine(plan.CandidatePath, "script.sh"), "Changed after review");
        var entry = Assert.Single(Store().ReadHistory(Token).Entries);
        Assert.Contains("Unapplied review (hint)", entry.Hint);
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Equal(bytes, File.ReadAllBytes(plan.PlanPath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("oversize")]
    [InlineData("unknown-schema")]
    public async Task DamagedPlansStayListedBesideReadablePeers(string damage)
    {
        var damaged = await Plan();
        var peer = await Plan();
        if (damage == "missing") File.Delete(damaged.PlanPath);
        else if (damage == "oversize") { using var file = File.OpenWrite(damaged.PlanPath); file.SetLength(16 * 1024 * 1024 + 1); }
        else if (damage == "unknown-schema") File.WriteAllText(damaged.PlanPath, File.ReadAllText(damaged.PlanPath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));
        else File.WriteAllText(damaged.PlanPath, "{unfinished");
        var history = Store().ReadHistory(Token);
        Assert.Equal(2, history.ObservedCount); Assert.Null(history.Error);
        Assert.StartsWith("Protected", history.Entries.Single(entry => entry.Id == damaged.Id).Hint);
        Assert.Contains("Unapplied", history.Entries.Single(entry => entry.Id == peer.Id).Hint);
    }

    [Fact]
    public async Task UnknownAndUnsafeRootEntriesDoNotHideVerifiedPeers()
    {
        var plan = await Plan();
        File.WriteAllText(Path.Combine(State, "unknown"), "preserved");
        var unsafeId = new string('e', 32);
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(State, unsafeId), Payload);
        else File.WriteAllText(Path.Combine(State, unsafeId), "not an operation");
        var history = Store().ReadHistory(Token);
        Assert.Equal(3, history.ObservedCount); Assert.Equal(3, history.Entries.Length);
        Assert.Contains("Unapplied", history.Entries.Single(entry => entry.Id == plan.Id).Hint);
        Assert.All(history.Entries.Where(entry => entry.Id != plan.Id), entry =>
        {
            Assert.StartsWith("Protected", entry.Hint);
            Assert.False(entry.MayNeedRecovery);
        });
        var refused = await Assert.ThrowsAsync<IOException>(() => Plan());
        Assert.Contains("unknown retained state entries", refused.Message);
        Assert.Equal(3, Store().ReadHistory(Token).ObservedCount);
        Assert.True(File.Exists(Path.Combine(Payload, "SKILL.md")));
    }

    [Fact]
    public async Task SixteenOperationsAndOverflowAreHonestAndPreflightPrecedesNetworkOrStaging()
    {
        for (var index = 0; index < 16; index++) await Plan();
        var full = Store().ReadHistory(Token);
        Assert.Equal(16, full.ObservedCount); Assert.Equal(16, full.Entries.Length); Assert.False(full.HasOmissions);
        Assert.Contains("cannot retire", full.Summary);
        var inventory = Inventory();
        var target = Assert.Single(inventory.Skills);
        var staged = false;
        var exception = await Assert.ThrowsAsync<IOException>(() => Store().PlanAsync(target, inventory, (_, _) => { staged = true; return Task.CompletedTask; }, Token));
        Assert.False(staged); Assert.Contains("read-only limit of 16", exception.Message);
        var network = await Assert.ThrowsAsync<IOException>(() => Store().PlanLatestAsync("must-never-be-executed", target, inventory, false, Token));
        Assert.Contains("read-only limit of 16", network.Message);
        var local = await Assert.ThrowsAsync<IOException>(() => Store().PlanLocalReimportAsync("must-never-be-executed", target, inventory, Token));
        Assert.Contains("read-only limit of 16", local.Message);
        Assert.False(Directory.Exists(State + "-local-previews"));
        Directory.CreateDirectory(Path.Combine(State, new string('f', 32)));
        var overflow = Store().ReadHistory(Token);
        Assert.Equal(16, overflow.Entries.Length); Assert.Equal(17, overflow.ObservedCount); Assert.True(overflow.HasOmissions);
        Assert.Contains("omitted", overflow.Summary);
    }

    [Fact]
    public async Task ContendedOrMissingLocksReportUnknownWithoutCreatingAnything()
    {
        var plan = await Plan();
        using (var state = NativeStateDirectory.OpenOrCreate(State, [], Token, allowCreate: false))
        using (var writer = state.OpenExistingFile("writer.lock", true, Token))
        {
            var history = Store().ReadHistory(Token);
            Assert.NotNull(history.Error); Assert.Empty(history.Entries);
        }
        File.Delete(Path.Combine(State, plan.Id, "writer.lock"));
        Assert.StartsWith("Protected", Assert.Single(Store().ReadHistory(Token).Entries).Hint);
        Assert.False(File.Exists(Path.Combine(State, plan.Id, "writer.lock")));
        File.Delete(Path.Combine(State, "writer.lock"));
        Assert.NotNull(Store().ReadHistory(Token).Error);
        Assert.False(File.Exists(Path.Combine(State, "writer.lock")));
    }

    [Fact]
    public async Task AccessFailuresRemainUnavailableOrProtectedRatherThanEmpty()
    {
        if (OperatingSystem.IsWindows()) return;
        var plan = await Plan();
        File.SetUnixFileMode(plan.PlanPath, 0);
        try
        {
            var entry = Assert.Single(Store().ReadHistory(Token).Entries);
            Assert.StartsWith("Protected", entry.Hint); Assert.NotNull(entry.Detail);
        }
        finally { File.SetUnixFileMode(plan.PlanPath, (UnixFileMode)384); }
        File.SetUnixFileMode(State, 0);
        try { Assert.NotNull(Store().ReadHistory(Token).Error); }
        finally { File.SetUnixFileMode(State, (UnixFileMode)448); }
        Assert.True(File.Exists(plan.PlanPath));
    }

    [Fact]
    public async Task SymlinkPayloadSlotIsProtectedWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var plan = await Plan();
        Directory.Move(plan.CandidatePath, plan.CandidatePath + "-held");
        Directory.CreateSymbolicLink(plan.CandidatePath, Payload);
        Assert.StartsWith("Protected", Assert.Single(Store().ReadHistory(Token).Entries).Hint);
        Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }
}
