using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementLocalReviewTests
{
    [Fact]
    public async Task OldChoiceSerializationOmitsResolutionFieldAndUnsupportedPairingCannotReviveOlderChoices()
    {
        var legacy = new LocalReviewSelection(1, new('a', 64), []);
        var encoded = JsonSerializer.Serialize(legacy, LocalReviewJsonContext.Default.LocalReviewSelection);
        Assert.DoesNotContain("resolutions", encoded);
        var expectedLegacy = "{\n  \"schemaVersion\": 1,\n  \"headerFingerprint\": \"" + new string('a', 64) + "\",\n  \"choices\": []\n}";
        Assert.Equal(expectedLegacy.ReplaceLineEndings(System.Environment.NewLine), encoded);
        var plan = await Plan(); var first = HistoricalMergeFixture.CaptureReview(State, plan, Token);
        Directory.CreateDirectory(Path.Combine(State, plan.Id, "merged-00"));
        var attempt = Directory.CreateDirectory(Path.Combine(State, plan.Id, "review-installed-merged-00"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(attempt.FullName, (UnixFileMode)448);
        var path = Path.Combine(first.ReviewPath, "choices-00.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
        Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
    }

    [Fact]
    public async Task ExcessivePrivateDraftEntriesAreRefusedBeforeLoadingAnyResolutionArchive()
    {
        var plan = await Plan(); var review = HistoricalMergeFixture.CaptureReview(State, plan, Token);
        Directory.CreateDirectory(Path.Combine(State, plan.Id, "merged-00"));
        var attempt = Directory.CreateDirectory(Path.Combine(State, plan.Id, "review-installed-merged-00"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(attempt.FullName, (UnixFileMode)448);
        for (var i = 0; i < 37; i++) File.WriteAllText(Path.Combine(review.ReviewPath, $"unexpected-{i:D2}"), "retained");
        var failure = Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.Contains("bounded history budget", failure.Message);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task CorruptNewestHistoricalChoicesCannotReviveEarlierApproval()
    {
        var plan = await Plan();
        var review = HistoricalMergeFixture.CaptureReview(State, plan, Token);
        Directory.CreateDirectory(Path.Combine(State, plan.Id, "merged-00"));
        var attempt = Directory.CreateDirectory(Path.Combine(State, plan.Id, "review-installed-merged-00"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(attempt.FullName, (UnixFileMode)448);
        var newest = Path.Combine(review.ReviewPath, "choices-01.json");
        File.WriteAllText(newest, "{");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(newest, (UnixFileMode)384);
        Assert.ThrowsAny<JsonException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.ThrowsAny<JsonException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task PartialHistoricalReviewStillClosesOriginalReplacementApproval()
    {
        var plan = await Plan();
        var original = PayloadSnapshot.Capture(Payload, Token);
        var review = Directory.CreateDirectory(Path.Combine(State, plan.Id, "local-review"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(review.FullName, (UnixFileMode)448);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Equal(original.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialAndUnusedResolutionAttemptsCountTowardCumulativeBudgetBeforeMoves(bool completedUnused)
    {
        var plan = await Plan();
        var input = Path.Combine(temporary.FullName, "resolution.bin"); File.WriteAllBytes(input, [1, 2, 3]);
        var review = HistoricalMergeFixture.CaptureReview(State, plan, Token, resolutionFile: input);
        var selection = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(review.ReviewPath, "choices-00.json")),
            LocalReviewJsonContext.Default.LocalReviewSelection)!;
        Assert.Equal(2, selection.SchemaVersion); Assert.Single(selection.Resolutions!.Entries);
        // Retained source-review attempt forces the historical saved-review reader
        // even when this interrupted operation never completed its merge plan.
        Directory.CreateDirectory(Path.Combine(State, plan.Id, "merged-00"));
        var attempt = Directory.CreateDirectory(Path.Combine(State, plan.Id, "review-installed-merged-00"));
        var unused = Directory.CreateDirectory(Path.Combine(review.ReviewPath, "resolutions-01"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(attempt.FullName, (UnixFileMode)448);
            File.SetUnixFileMode(unused.FullName, (UnixFileMode)448);
        }
        var blobPath = Path.Combine(unused.FullName, "entry-00000");
        using (var blob = File.Create(blobPath)) blob.SetLength(CapturedResolutionArchive.MaxRetainedBytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(blobPath, (UnixFileMode)384);
        if (completedUnused)
        {
            using var blob = File.OpenRead(blobPath);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob));
            var entry = Assert.Single(review.Resolution!.Entries);
            var archive = new CapturedResolutionArchive([entry with
            {
                Entry = entry.Entry with { Bytes = blob.Length, ContentHash = hash, ComparisonHash = hash }, StoredBytes = blob.Length
            }]);
            archive.Validate();
            using var file = new FileStream(Path.Combine(unused.FullName, "manifest.json"), FileMode.CreateNew);
            BoundedReviewJson.Write(file, new ResolutionArchiveManifest(1, archive.Entries),
                ResolutionJsonContext.Default.ResolutionArchiveManifest, CapturedPayloadArchive.MaxManifestBytes, Token);
            file.Flush(true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle, (UnixFileMode)384);
        }
        var before = PayloadSnapshot.Capture(Payload, Token);
        var moves = new List<string>();
        var failure = Assert.Throws<IOException>(() => Store(step => { if (step.EndsWith("-moved", StringComparison.Ordinal)) moves.Add(step); })
            .Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Contains("cumulative byte/manifest budget", failure.Message);
        Assert.Empty(moves); Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
        Assert.True(File.Exists(blobPath));
    }

    [Fact]
    public async Task CorruptReferencedResolutionRefusesBeforeLiveMovesEvenAfterMergedPlanIsBuilt()
    {
        var plan = await Plan();
        var input = Path.Combine(temporary.FullName, "resolution.bin"); File.WriteAllBytes(input, [1, 2, 3]);
        var review = HistoricalMergeFixture.CaptureReview(State, plan, Token, resolutionFile: input);
        if (!review.Installed.RootMetadata!.Supported || !review.Source.RootMetadata!.Supported
            || review.Installed.Entries.Concat(review.Source.Entries).Any(entry => !entry.Metadata!.Supported))
            Assert.Skip("Completed native merge fixture requires supported metadata; run this case on privileged Linux or macOS.");
        HistoricalMergeFixture.Create(State, plan, Token, capturedReview: review);
        var merged = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.NotNull(merged.LocalDisposition);
        File.Delete(input);
        File.WriteAllBytes(Path.Combine(review.ReviewPath, "resolutions-00", "entry-00000"), [3, 2, 1]);
        var before = PayloadSnapshot.Capture(Payload, Token); var moves = new List<string>();
        var failure = Assert.Throws<IOException>(() => Store(step => { if (step.EndsWith("-moved", StringComparison.Ordinal)) moves.Add(step); })
            .Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed"));
        Assert.Contains("Captured", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(moves); Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

}
