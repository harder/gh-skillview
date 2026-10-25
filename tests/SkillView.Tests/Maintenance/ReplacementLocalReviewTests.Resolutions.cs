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
        HistoricalMergeFixture.CreateIncompleteAttempt(State, plan, Token);
        var path = Path.Combine(first.ReviewPath, "choices-00.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
        Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
    }

    [Fact]
    public async Task ExcessivePrivateDraftEntriesAreRefusedBeforeLoadingAnyResolutionArchive()
    {
        var plan = await Plan(); var review = HistoricalMergeFixture.CaptureReview(State, plan, Token);
        HistoricalMergeFixture.CreateIncompleteAttempt(State, plan, Token);
        HistoricalMergeFixture.SeedOperation(State, plan, Token, operation =>
        {
            using var draft = NativeStateDirectory.OpenOrCreate(Path.Combine(operation.CanonicalPath, "local-review"), [], Token, allowCreate: false);
            for (var i = 0; i < 37; i++)
            {
                using var file = draft.OpenFile($"unexpected-{i:D2}", false, Token); file.Write("retained"u8); file.Flush(true);
            }
            draft.FlushNamespace(Token);
        });
        var failure = Assert.Throws<IOException>(() => Store(sourceReviewRequired: true).Inspect(plan.Id, Inventory(), Token));
        Assert.Contains("bounded history budget", failure.Message);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task CorruptNewestHistoricalChoicesCannotReviveEarlierApproval()
    {
        var plan = await Plan();
        var review = HistoricalMergeFixture.CaptureReview(State, plan, Token);
        HistoricalMergeFixture.CreateIncompleteAttempt(State, plan, Token);
        Assert.Equal("ready", Store().Inspect(plan.Id, Inventory(), Token).Status);
        HistoricalMergeFixture.SeedOperation(State, plan, Token, operation =>
        {
            using var draft = NativeStateDirectory.OpenOrCreate(Path.Combine(operation.CanonicalPath, "local-review"), [], Token, allowCreate: false);
            using (var newest = draft.OpenFile("choices-01.json", false, Token)) { newest.Write("{"u8); newest.Flush(true); }
            draft.FlushNamespace(Token);
        });
        Assert.ThrowsAny<JsonException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.ThrowsAny<JsonException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task PartialHistoricalReviewStillClosesOriginalReplacementApproval()
    {
        var plan = await Plan();
        var original = PayloadSnapshot.Capture(Payload, Token);
        HistoricalMergeFixture.SeedOperation(State, plan, Token, operation =>
        {
            using var review = operation.CreateStagingDirectory("local-review", Token); review.SealStagingDirectory(448, Token);
        });
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
        HistoricalMergeFixture.CreateIncompleteAttempt(State, plan, Token);
        Assert.Equal("ready", Store().Inspect(plan.Id, Inventory(), Token).Status);
        var blobPath = Path.Combine(review.ReviewPath, "resolutions-01", "entry-00000");
        HistoricalMergeFixture.SeedOperation(State, plan, Token, operation =>
        {
            using var draft = NativeStateDirectory.OpenOrCreate(Path.Combine(operation.CanonicalPath, "local-review"), [], Token, allowCreate: false);
            using var unused = draft.CreateStagingDirectory("resolutions-01", Token);
            using (var blob = unused.OpenFile("entry-00000", false, Token))
            {
                blob.SetLength(CapturedResolutionArchive.MaxRetainedBytes); blob.Flush(true);
                if (completedUnused)
                {
                    blob.Position = 0;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob));
                    var entry = Assert.Single(review.Resolution!.Entries);
                    var archive = new CapturedResolutionArchive([entry with
                    {
                        Entry = entry.Entry with { Bytes = blob.Length, ContentHash = hash, ComparisonHash = hash }, StoredBytes = blob.Length
                    }]);
                    archive.Validate();
                    using var file = unused.OpenFile("manifest.json", false, Token);
                    BoundedReviewJson.Write(file, new ResolutionArchiveManifest(1, archive.Entries),
                        ResolutionJsonContext.Default.ResolutionArchiveManifest, CapturedPayloadArchive.MaxManifestBytes, Token);
                    file.Flush(true);
                }
            }
            unused.SealStagingDirectory(448, Token);
            draft.FlushNamespace(Token);
        });
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
