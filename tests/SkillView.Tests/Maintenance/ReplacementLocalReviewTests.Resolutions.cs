using System.Text.Json;
using System.Runtime.InteropServices;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class ReplacementLocalReviewTests
{
    [Fact]
    public async Task FrozenResolutionSurvivesOriginalInputEditsDeletionReuseApplyAndUndo()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan(); var first = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var source = Path.Combine(temporary.FullName, "my-resolution.txt");
        File.WriteAllText(source, Metadata.Replace("version: '1.0'", "version: '1.5'") + "Reviewed merged instructions\n");
        File.SetUnixFileMode(source, (UnixFileMode)384);
        var resolved = Store().ChooseLocalChanges(plan.Id, first.ReviewFingerprint, [new("SKILL.md", "resolved")], Inventory(), Token,
            [new("SKILL.md", source)]);
        Assert.True(resolved.CanMaterialize, string.Join("; ", resolved.MetadataRefusals));
        var reviewed = Assert.Single(resolved.Review.Paths, row => row.Path == "SKILL.md");
        Assert.Equal("resolved", reviewed.Selection); Assert.Equal(384, reviewed.Resolution!.UnixMode);
        File.WriteAllText(source, "changed later"); File.Delete(source);
        var reused = Store().ChooseLocalChanges(plan.Id, resolved.ReviewFingerprint,
            [new("SKILL.md", "resolved"), new("resource", "source")], Inventory(), Token);
        Assert.Equal(reviewed.Resolution, reused.Review.Paths.Single(row => row.Path == "SKILL.md").Resolution);
        Assert.Single(Directory.EnumerateDirectories(reused.ReviewPath, "resolutions-*"));
        var restarted = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        Assert.Equal(reused.ReviewFingerprint, restarted.ReviewFingerprint);
        var merged = Store().FinalizeLocalChanges(plan.Id, reused.ReviewFingerprint, Inventory(), Token);
        Assert.Equal("1.5", merged.LocalDisposition!.ResultAuthoredVersion);
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        var result = Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed");
        Assert.True(result.Succeeded, result.Message);
        var text = File.ReadAllText(Path.Combine(Payload, "SKILL.md"));
        Assert.Contains("Reviewed merged instructions", text); Assert.Contains(NewTree, text); Assert.DoesNotContain(OldTree, text);
        Assert.Equal((UnixFileMode)384, File.GetUnixFileMode(Path.Combine(Payload, "SKILL.md")));
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
        var inspection = Store().Inspect(plan.Id, Inventory(), Token);
        var undo = Store().Recover(plan.Id, inspection.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undo.Succeeded, undo.Message); Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
    }

    [Fact]
    public async Task ReusedResolutionKeepsOriginalSourceEvidenceAndRevisedFilesGetIndependentCaptures()
    {
        var plan = await Plan(); var first = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var input = Path.Combine(temporary.FullName, "manual.bin"); File.WriteAllBytes(input, [0, 255, 1]);
        var one = Store().ChooseLocalChanges(plan.Id, first.ReviewFingerprint,
            [new("resource", "resolved"), new("SKILL.md", "installed")], Inventory(), Token, [new("resource", input)]);
        var file = Path.Combine(one.ReviewPath, "choices-01.json");
        var saved = JsonSerializer.Deserialize(File.ReadAllBytes(file), LocalReviewJsonContext.Default.LocalReviewSelection)!;
        var reference = Assert.Single(saved.Resolutions!.Entries);
        using var archive = NativeStateDirectory.OpenOrCreate(Path.Combine(one.ReviewPath, reference.Archive), [], Token, allowCreate: false);
        var oldSource = Assert.Single(CapturedResolutionArchive.Load(archive, reference.ManifestFingerprint, Token).Entries).Metadata!.Identity;
        File.WriteAllBytes(input, [2, 3, 4]);
        var two = Store().ChooseLocalChanges(plan.Id, one.ReviewFingerprint,
            [new("resource", "resolved"), new("SKILL.md", "installed")], Inventory(), Token, [new("resource", input)]);
        Assert.Equal(2, Directory.EnumerateDirectories(two.ReviewPath, "resolutions-*").Count());
        var loaded = CapturedResolutionArchive.Load(archive, reference.ManifestFingerprint, Token);
        Assert.Equal(oldSource, Assert.Single(loaded.Entries).Metadata!.Identity);
        using var bytes = new MemoryStream(); loaded.OpenReader(archive).CopyEntry("resource", bytes, Token);
        Assert.Equal(new byte[] { 0, 255, 1 }, bytes.ToArray());
        Assert.NotEqual(one.Review.Paths.Single(row => row.Path == "resource").Resolution!.ContentHash,
            two.Review.Paths.Single(row => row.Path == "resource").Resolution!.ContentHash);
    }

    [Fact]
    public async Task CorruptReferencedResolutionRefusesBeforeLiveMovesEvenAfterMergedPlanIsBuilt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var input = Path.Combine(temporary.FullName, "resource.bin"); File.WriteAllBytes(input, [1, 2, 3]);
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint,
            [new("SKILL.md", "installed"), new("resource", "resolved")], Inventory(), Token, [new("resource", input)]);
        var merged = Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
        var blob = Directory.EnumerateFiles(chosen.ReviewPath, "entry-*", SearchOption.AllDirectories)
            .Single(path => Path.GetFileName(Path.GetDirectoryName(path))!.StartsWith("resolutions-", StringComparison.Ordinal));
        File.WriteAllBytes(blob, [3, 2, 1]);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed"));
        Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Contains("Installed instructions", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
    }

    [Fact]
    public async Task PartialResolutionAttemptsAreRetainedRetryableAndChargedToCumulativeStorageBudget()
    {
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var input = Path.Combine(temporary.FullName, "not-a-skill"); File.WriteAllText(input, "no frontmatter");
        Assert.Throws<IOException>(() => Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint,
            [new("SKILL.md", "resolved")], Inventory(), Token, [new("SKILL.md", input)]));
        var partial = Path.Combine(review.ReviewPath, "resolutions-00"); Assert.True(Directory.Exists(partial));
        var retry = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint,
            [new("resource", "resolved"), new("SKILL.md", "installed")], Inventory(), Token, [new("resource", input)]);
        Assert.Equal(2, Directory.EnumerateDirectories(review.ReviewPath, "resolutions-*").Count());
        Assert.NotEqual(review.ReviewFingerprint, retry.ReviewFingerprint);
        using (var file = new FileStream(Path.Combine(partial, "entry-00000"), FileMode.Open, FileAccess.Write))
            file.SetLength(CapturedResolutionArchive.MaxRetainedBytes);
        Assert.Throws<IOException>(() => Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task OldChoiceSerializationOmitsResolutionFieldAndUnsupportedPairingCannotReviveOlderChoices()
    {
        var legacy = new LocalReviewSelection(1, new('a', 64), []);
        var encoded = JsonSerializer.Serialize(legacy, LocalReviewJsonContext.Default.LocalReviewSelection);
        Assert.DoesNotContain("resolutions", encoded);
        Assert.Equal("{\n  \"schemaVersion\": 1,\n  \"headerFingerprint\": \"" + new string('a', 64) + "\",\n  \"choices\": []\n}", encoded);
        var plan = await Plan(); var first = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var path = Path.Combine(first.ReviewPath, "choices-00.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
        Assert.Throws<IOException>(() => Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token));
    }

    [Fact]
    public async Task ExcessivePrivateDraftEntriesAreRefusedBeforeLoadingAnyResolutionArchive()
    {
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        for (var i = 0; i < 37; i++) File.WriteAllText(Path.Combine(review.ReviewPath, $"unexpected-{i:D2}"), "retained");
        var failure = Assert.Throws<IOException>(() => Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token));
        Assert.Contains("bounded history budget", failure.Message);
        Assert.False(Directory.Exists(plan.RetainedPath));
    }

    [Fact]
    public async Task ResolvedTypeChangeCopiesCapturedSourceAttributesAndModeWithoutChangingOriginalDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return;
        File.Delete(Path.Combine(Payload, "resource")); Directory.CreateDirectory(Path.Combine(Payload, "resource"));
        var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = await Plan(); var review = Store().ReviewLocalChanges(plan.Id, plan.PlanFingerprint, Inventory(), Token);
        var input = Path.Combine(temporary.FullName, "new-regular-file"); File.WriteAllBytes(input, [0, 255, 7]);
        File.SetUnixFileMode(input, (UnixFileMode)384);
        const string attribute = "com.harder.skillview.resolution";
        byte[] expected = [7, 0, 255];
        Assert.Equal(0, ResolutionNative.setxattr(input, attribute, expected, (nuint)expected.Length, 0, 0));
        var chosen = Store().ChooseLocalChanges(plan.Id, review.ReviewFingerprint,
            [new("SKILL.md", "installed"), new("resource", "resolved")], Inventory(), Token, [new("resource", input)]);
        Assert.True(chosen.CanMaterialize, string.Join("; ", chosen.MetadataRefusals));
        var merged = Store().FinalizeLocalChanges(plan.Id, chosen.ReviewFingerprint, Inventory(), Token);
        Assert.True(Store().Apply(plan.Id, merged.PlanFingerprint, Inventory(), Token, "merge-reviewed").Succeeded);
        Assert.Equal(new byte[] { 0, 255, 7 }, File.ReadAllBytes(Path.Combine(Payload, "resource")));
        Assert.Equal((UnixFileMode)384, File.GetUnixFileMode(Path.Combine(Payload, "resource")));
        var actual = new byte[expected.Length];
        Assert.Equal((nint)expected.Length, ResolutionNative.getxattr(Path.Combine(Payload, "resource"), attribute, actual, (nuint)actual.Length, 0, 0));
        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(Path.Combine(plan.RetainedPath, "resource")));
        Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(plan.RetainedPath, Token).Fingerprint);
    }

    private static class ResolutionNative
    {
        [DllImport("libc", SetLastError = true)] internal static extern int setxattr(string path, string name, byte[] value, nuint bytes, uint position, int options);
        [DllImport("libc", SetLastError = true)] internal static extern nint getxattr(string path, string name, byte[] value, nuint bytes, uint position, int options);
    }
}
