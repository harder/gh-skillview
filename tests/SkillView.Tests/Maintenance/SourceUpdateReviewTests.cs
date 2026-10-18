using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SourceUpdateReviewTests : IDisposable
{
    private readonly string temporary = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/var/tmp", "skillview-source-review-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Skills => Path.Combine(temporary, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string State => Path.Combine(temporary, "state");
    private const string Metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/repo\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";
    public SourceUpdateReviewTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), Metadata + "Old instructions\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Payload, (UnixFileMode)448);
    }
    public void Dispose() => Directory.Delete(temporary, true);
    private ReplacementPlanStore Store(bool required = false, Action<string>? checkpoint = null) => new(State, new Logger(), checkpoint,
        new ProcessRunner(new Logger(), environment: new Dictionary<string, string>
        {
            ["HOME"] = temporary,
            ["USERPROFILE"] = temporary,
            ["XDG_STATE_HOME"] = Path.Combine(temporary, "xdg-state"),
            ["GIT_DIR"] = Path.Combine(temporary, "private.git"),
            ["GIT_WORK_TREE"] = temporary,
            ["GIT_COMMON_DIR"] = Path.Combine(temporary, "private.git"),
            ["GIT_CONFIG_COUNT"] = "0"
        }, defaultWorkingDirectory: temporary), sourceReviewRequired: required);
    private InventorySnapshot Inventory()
    {
        ScanRoot[] roots = [new(Skills, Scope.Custom, null)];
        return InventorySnapshot.Empty with { Skills = new LocalSkillScanner(new Logger()).Scan(roots).ToImmutableArray(), ScannedRoots = [.. roots], ConfiguredRoots = [.. roots] };
    }
    private async Task<ReplacementResult> Plan(bool candidateHasLink = false)
    {
        var inventory = Inventory();
        return await Store(true).PlanAsync(Assert.Single(inventory.Skills), inventory, (destination, _) =>
        {
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), Metadata + "New instructions https://new.example/api\n");
            Directory.CreateDirectory(Path.Combine(destination.CanonicalPath, "scripts"));
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "scripts", "never-run.sh"), "#!/bin/sh\nexit 99\n");
            File.WriteAllText(Path.Combine(destination.CanonicalPath, "package.json"), "{\"dependencies\":{\"fixture\":\"2\"}}");
            if (candidateHasLink)
            {
                File.WriteAllBytes(Path.Combine(destination.CanonicalPath, "candidate-extra"), [5, 6, 7]);
                File.CreateSymbolicLink(Path.Combine(destination.CanonicalPath, "user-link"), "candidate-extra");
            }
            return Task.CompletedTask;
        }, Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLinkReviewRetainsFullArchiveModesAndHistoricalPlanEvidence(bool candidateHasLink)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix link-mode hash domain; Windows link metadata has a separate contract."); return; }
        Assert.SkipUnless(SecureRemovalBackend.IsSupported, SecureRemovalBackend.UnsupportedReason ?? "Native directory capture is unavailable.");
        File.WriteAllBytes(Path.Combine(Payload, "user-extra"), [0, 255, 42]);
        File.SetUnixFileMode(Path.Combine(Payload, "user-extra"), (UnixFileMode)320);
        File.CreateSymbolicLink(Path.Combine(Payload, "user-link"), "user-extra");
        var original = PayloadSnapshot.CaptureEvidence(Payload, Token);
        Assert.Null(original.Entries.Single(entry => entry.Path == "user-link").UnixMode);
        var plan = await Plan(candidateHasLink);
        var savedBytes = File.ReadAllBytes(plan.PlanPath);
        var saved = JsonSerializer.Deserialize(savedBytes, ReplacementJsonContext.Default.ReplacementPlan)!;
        Assert.Equal(original.Fingerprint, saved.Original.Payload);
        Assert.Null(saved.Original.Entries.Single(entry => entry.Path == "user-link").UnixMode);
        var review = Assert.IsType<SourceReviewEvidence>(plan.SourceReview);
        var directory = Path.GetDirectoryName(plan.PlanPath)!;
        using (var beforeState = NativeStateDirectory.OpenOrCreate(Path.Combine(directory, review.InstalledArchive), [], Token, allowCreate: false))
        using (var afterState = NativeStateDirectory.OpenOrCreate(Path.Combine(directory, review.CandidateArchive), [], Token, allowCreate: false))
        {
            var before = CapturedPayloadArchive.Load(beforeState, review.InstalledManifest, Token);
            var after = CapturedPayloadArchive.Load(afterState, review.CandidateManifest, Token);
            var link = before.Archive.Entries.Single(item => item.Entry.Path == "user-link");
            Assert.NotNull(link.Entry.UnixMode);
            Assert.NotEqual(saved.Original.Payload, before.Archive.Snapshot.Fingerprint);
            Assert.Equal(review.InstalledManifest, before.Fingerprint);
            Assert.Equal(link.Entry, review.Report.Files.Single(file => file.Path == "user-link").Before);
            using var raw = new MemoryStream(); before.Archive.CopyEntry(beforeState, "user-link", raw, Token);
            Assert.Equal(Encoding.UTF8.GetBytes("user-extra"), raw.ToArray());
            if (candidateHasLink)
            {
                var candidateLink = after.Archive.Entries.Single(item => item.Entry.Path == "user-link");
                Assert.NotNull(candidateLink.Entry.UnixMode);
                Assert.NotEqual(saved.Candidate.Payload, after.Archive.Snapshot.Fingerprint);
                Assert.Equal("candidate-extra", candidateLink.Entry.LinkTarget);
            }
        }
        using (var candidate = NativeDirectoryMoveLease.Open(directory, plan.CandidatePath, Token))
            Assert.Equal(saved.Candidate.Payload, candidate.Capture(Token).Fingerprint);
        var ready = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.False(ready.SourceReviewAcknowledged);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, review.Fingerprint, Inventory(), Token);
        Assert.True(Store().Inspect(plan.Id, Inventory(), Token).SourceReviewAcknowledged);
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(applied.Succeeded, applied.Message);
        var installed = Store().Inspect(plan.Id, Inventory(), Token);
        var undone = Store().Recover(plan.Id, installed.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        var restored = PayloadSnapshot.CaptureEvidence(Payload, Token);
        Assert.Equal(original.Fingerprint, restored.Fingerprint);
        Assert.True(NativeDirectoryMoveLease.SameObject(original.RootIdentity!.Value, restored.RootIdentity!.Value));
        Assert.Equal("user-extra", new FileInfo(Path.Combine(Payload, "user-link")).LinkTarget);
        Assert.Equal(savedBytes, File.ReadAllBytes(plan.PlanPath));
    }

    [Fact]
    public async Task NativeLinkRawArchiveCorruptionStillCannotAcquireSourceReceipt()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix link-mode hash domain; Windows link metadata has a separate contract."); return; }
        Assert.SkipUnless(SecureRemovalBackend.IsSupported, SecureRemovalBackend.UnsupportedReason ?? "Native directory capture is unavailable.");
        File.WriteAllBytes(Path.Combine(Payload, "user-extra"), [0, 255, 42]);
        File.CreateSymbolicLink(Path.Combine(Payload, "user-link"), "user-extra");
        var original = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var plan = await Plan(); var review = plan.SourceReview!;
        var archive = Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, review.InstalledArchive);
        string blob;
        using (var state = NativeStateDirectory.OpenOrCreate(archive, [], Token, allowCreate: false))
            blob = CapturedPayloadArchive.Load(state, review.InstalledManifest, Token).Archive.Entries.Single(item => item.Entry.Path == "user-link").Blob!;
        var raw = File.ReadAllBytes(Path.Combine(archive, blob)); raw[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(archive, blob), raw);
        Assert.Throws<IOException>(() => Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, review.Fingerprint, Inventory(), Token));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "source-review-" + plan.PlanFingerprint + ".json")));
        Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
    }

    [Fact]
    public async Task PersistedContractGatesApplyAndNeverStartedRecoveryAcrossLegacyStoreInstances()
    {
        var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan(); var source = Assert.IsType<SourceReviewEvidence>(plan.SourceReview);
        Assert.Contains("-Old instructions", source.Report.Files.Single(file => file.Path == "SKILL.md").TextDiff);
        Assert.Contains("+New instructions", source.Report.Files.Single(file => file.Path == "SKILL.md").TextDiff);
        Assert.Contains(source.Report.NewlyObserved, delta => delta.Observation.Value == "https://new.example/api");
        Assert.Contains(source.Report.NewlyObserved, delta => delta.Observation.Value == "fixture@2");
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.False(inspection.SourceReviewAcknowledged);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), false, Token));
        Assert.Throws<IOException>(() => Store().AcknowledgeSourceReview(plan.Id, new('0', 64), source.Fingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, new('0', 64), Inventory(), Token));
        Assert.Equal(original.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        var acknowledged = Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, source.Fingerprint, Inventory(), Token);
        Assert.True(acknowledged.SourceReviewAcknowledged); Assert.False(acknowledged.MutationObserved);
        Assert.True(Store().Inspect(plan.Id, Inventory(), Token).SourceReviewAcknowledged);
        var applied = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal("#!/bin/sh\nexit 99\n", File.ReadAllText(Path.Combine(Payload, "scripts", "never-run.sh")));
        var installed = Store().Inspect(plan.Id, Inventory(), Token);
        // Undo has independent physical review authority even if an acknowledgement is lost.
        File.Delete(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "source-review-" + plan.PlanFingerprint + ".json"));
        Assert.True(Store().Recover(plan.Id, installed.ReviewFingerprint, Inventory(), true, Token).Succeeded);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
    }
    [Fact]
    public async Task ReceiptCannotAuthorizeAnotherContextAndCorruptArchiveCannotBeAcknowledged()
    {
        var plan = await Plan(); var source = plan.SourceReview!;
        Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, source.Fingerprint, Inventory(), Token);
        var receiptPath = Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "source-review-" + plan.PlanFingerprint + ".json");
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!;
        receipt["contextFingerprint"] = new string('0', 64); File.WriteAllText(receiptPath, receipt.ToJsonString());
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.False(inspected.SourceReviewAcknowledged); Assert.NotNull(inspected.SourceReviewWarning);
        File.Delete(receiptPath);
        var archive = Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, source.CandidateArchive);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(archive, "manifest.json")))!;
        var blob = manifest["entries"]!.AsArray().First(item => item!["entry"]!["path"]!.GetValue<string>() == "SKILL.md")!["blob"]!.GetValue<string>();
        var raw = File.ReadAllBytes(Path.Combine(archive, blob)); raw[^2] ^= 1; File.WriteAllBytes(Path.Combine(archive, blob), raw);
        Assert.Throws<IOException>(() => Store().AcknowledgeSourceReview(plan.Id, plan.PlanFingerprint, source.Fingerprint, Inventory(), Token));
        Assert.False(File.Exists(receiptPath)); Assert.False(Directory.Exists(plan.RetainedPath));
    }
    [Fact]
    public async Task LegacyPlanKeepsOmittedContractAndNeverAcquiresInventedReviewEvidence()
    {
        var inventory = Inventory();
        var legacy = await Store().PlanAsync(Assert.Single(inventory.Skills), inventory, (state, _) =>
        { File.WriteAllText(Path.Combine(state.CanonicalPath, "SKILL.md"), Metadata + "Legacy replacement\n"); return Task.CompletedTask; }, Token);
        Assert.Null(legacy.SourceReview);
        Assert.DoesNotContain("sourceReview", File.ReadAllText(legacy.PlanPath));
        Assert.Throws<IOException>(() => Store(true).AcknowledgeSourceReview(legacy.Id, legacy.PlanFingerprint, new('a', 64), Inventory(), Token));
        Assert.True(Store(true).Apply(legacy.Id, legacy.PlanFingerprint, Inventory(), Token).Succeeded);
    }
    [Fact]
    public void BoundedTextAndUnknownOldCoverageCannotProveNewIntroduction()
    {
        var old = new BytesReader(("SKILL.md", Encoding.UTF8.GetBytes(Metadata)), ("scripts/old.sh", new byte[] { 255 }));
        var current = new BytesReader(("SKILL.md", Encoding.UTF8.GetBytes(Metadata + string.Join('\n', Enumerable.Range(0, 300).Select(i => "Changed " + i)) + "\nhttps://new.example/x")),
            ("policy.json", Encoding.UTF8.GetBytes("{\"permissions\":\"review me\"}")));
        var report = SourceUpdateReview.Analyze(old.Snapshot, old, current.Snapshot, current, Token);
        Assert.Equal("partial", report.Before.Coverage);
        Assert.All(report.NewlyObserved, delta => Assert.StartsWith("newly-observed;", delta.Evidence));
        Assert.True(report.Files.Single(file => file.Path == "SKILL.md").OmittedLines > 0);
        Assert.Contains("permissions", report.Files.Single(file => file.Path == "policy.json").TextDiff);
        Assert.Contains(report.Files, file => file.Path == "scripts/old.sh" && file.Presentation.Contains("binary"));
        Assert.Equal(SourceUpdateReview.Hash(report), SourceUpdateReview.Hash(SourceUpdateReview.Analyze(old.Snapshot, old, current.Snapshot, current, Token)));
    }
    [Fact]
    public void SaturatedDiffBudgetIncludesHeadersAndRetainsTextOmissionCounts()
    {
        var old = new BytesReader(Enumerable.Range(0, 128).Select(i => ($"scripts/{i:D3}.sh", Encoding.UTF8.GetBytes("Old"))).ToArray());
        var current = new BytesReader(Enumerable.Range(0, 128).Select(i => ($"scripts/{i:D3}.sh",
            Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 160).Select(line => "Changed " + line))))).ToArray());
        var report = SourceUpdateReview.Analyze(old.Snapshot, old, current.Snapshot, current, Token);
        Assert.Equal(1024, report.Files.Sum(file => file.TextDiff.Count(character => character == '\n')));
        Assert.Equal(128, report.Files.Length);
        Assert.All(report.Files.Skip(8), file => { Assert.Empty(file.TextDiff); Assert.Equal(161, file.OmittedLines); });
        Assert.Equal(161 * 128, report.Files.Sum(file => file.OmittedLines + file.TextDiff.Split('\n').Count(line => line.StartsWith('+') || line.StartsWith('-'))));
    }

    private sealed class BytesReader(params (string Path, byte[] Data)[] files) : IPayloadEvidenceReader
    {
        public PayloadSnapshot Snapshot => new("/fixture", "not-authority", "not-authority", files.Select(item => Entry(item.Path)).ToImmutableArray());
        public PayloadEntry Entry(string path)
        {
            var bytes = files.Single(item => item.Path == path).Data; var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new(path, "file", hash, hash, bytes.Length, 420, null);
        }
        public PayloadNativeMetadata? Metadata(string path) => null;
        public void CopyEntry(string path, Stream destination, CancellationToken token) => destination.Write(files.Single(item => item.Path == path).Data);
    }
}
