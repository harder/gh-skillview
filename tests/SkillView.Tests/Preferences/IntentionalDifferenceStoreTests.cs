using System.Collections.Immutable;
using System.Security.Cryptography;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Tests.Ui;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed class IntentionalDifferenceStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-intentional-");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private IntentionalDifferenceStore Store(int maxRecords = 512) => new(Path.Combine(_root.FullName, "state"), maxRecords);
    private InventorySnapshot Capture(IntentionalDifferenceStore? store = null)
    {
        var skills = new[] { ("one", "refs/tags/v1"), ("two", "refs/tags/v2") }.Select(pair =>
        {
            var path = Path.Combine(_root.FullName, "skills", pair.Item1, "demo"); Directory.CreateDirectory(path);
            var file = Path.Combine(path, "SKILL.md");
            if (!File.Exists(file)) File.WriteAllText(file, "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/source\n  github-path: skills/demo\n  github-ref: " + pair.Item2 + "\n---\nAuthored instructions.\n");
            var (_, metadata, _) = FrontMatterParser.Parse(File.ReadAllText(file));
            var skill = CapturedFindingTests.Copy(path) with { FrontMatter = metadata, ScanRoot = Path.GetDirectoryName(path)! };
            return skill with { ObservedSubject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, Token) };
        }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills, ScannedRoots = [new(Path.Combine(_root.FullName, "skills"), Scope.Custom, null)] };
        return snapshot with { IntentionalDifferences = (store ?? Store()).Read(IntentionalDifferenceStore.Exclusions(snapshot), Token) };
    }
    private static CapturedFinding Family(InventorySnapshot snapshot) => Assert.Single(MaintenanceQueueBuilder.Build(snapshot, Token).Findings.Where(f => f.Difference is not null));
    public void Dispose() => _root.Delete(true);

    [Fact]
    public void ReadMissingStateCreatesNothingAndSetRestartRestorePreservePayloadNativeObjects()
    {
        var store = Store(); Assert.Empty(store.Read([], Token).Records); Assert.False(Directory.Exists(store.Root));
        var snapshot = Capture(store); var family = Family(snapshot);
        var payloads = snapshot.Skills.Select(skill => PayloadSnapshot.CaptureEvidence(skill.ResolvedPath, Token)).ToArray();
        var receipt = store.SetObserved(family, snapshot, Token);
        Assert.True(File.Exists(receipt)); Assert.Single(Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories));
        var restarted = Capture(Store()); var intentional = Family(restarted); Assert.True(intentional.Intentional);
        var row = Assert.Single(ChangesQueueBuilder.Build(MaintenanceQueueBuilder.Build(restarted, Token), Token).Rows);
        Assert.Equal("Intent", row.Kind); Assert.Contains("Restore suggestions", ChangesQueueBuilder.Render(row, Token, false, null));
        store.SetObserved(intentional, restarted, Token);
        Assert.False(Family(Capture()).Intentional); Assert.Equal(2, Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories).Length);
        for (var index = 0; index < snapshot.Skills.Length; index++)
        {
            var after = PayloadSnapshot.CaptureEvidence(snapshot.Skills[index].ResolvedPath, Token);
            Assert.Equal(payloads[index].Fingerprint, after.Fingerprint); Assert.Equal(payloads[index].Objects, after.Objects);
            Assert.Equal(payloads[index].RootIdentity, after.RootIdentity);
            Assert.False(File.Exists(IgnoreMarker.MarkerPathFor(snapshot.Skills[index].ResolvedPath)));
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("membership")]
    [InlineData("method")]
    [InlineData("owner-context")]
    public void RelevantEvidenceChangesInvalidateAcknowledgement(string change)
    {
        var store = Store(); var snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        snapshot = Capture(store);
        var skill = snapshot.Skills[1] with { ObservedSubject = null };
        skill = change switch
        {
            "revision" => skill with { FrontMatter = skill.FrontMatter with { GithubRef = "refs/tags/v3" } },
            "source" => skill with { FrontMatter = skill.FrontMatter with { Upstream = "https://github.com/other/source" } },
            "membership" => skill with { Agents = [new("claude", skill.ResolvedPath, false, Scope.Project, _root.FullName)] },
            "method" => skill with { OwnershipClaims = [new("fixture/source", "github", null, null, null) { Provider = "manual", InstallationMode = "copy" }] },
            _ => skill with { OwnershipClaims = [new("fixture/source", "github", null, null, null) { Provider = "owner", ContextRoot = _root.FullName }] }
        };
        var changed = snapshot with { Skills = snapshot.Skills.SetItem(1, skill) };
        Assert.All(MaintenanceQueueBuilder.Build(changed, Token).Findings.Where(f => f.Difference is not null), finding => Assert.False(finding.Intentional));
    }

    [Fact]
    public void StaleWriterAndChangedLiveMetadataRefuseWithoutOverwritingRecords()
    {
        var store = Store(); var snapshot = Capture(store); var selected = Family(snapshot);
        store.SetObserved(selected, snapshot, Token);
        var before = Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(store.Root, path), File.ReadAllBytes);
        Assert.Throws<IOException>(() => store.SetObserved(selected, Capture(store), Token));
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(store.Root, pair.Key!))));
        var fresh = Capture(store); var current = Family(fresh);
        store.SetObserved(current, fresh, Token);
        fresh = Capture(store); current = Family(fresh);
        File.WriteAllText(Path.Combine(fresh.Skills[0].ResolvedPath, "SKILL.md"), "---\nname: demo\ndescription: Changed\nmetadata:\n  github-repo: https://github.com/different/source\n  github-path: skills/demo\n---\n");
        Assert.Throws<IOException>(() => store.SetObserved(current, fresh, Token));
        Assert.Equal(2, Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void PartialOrMalformedStateRemainsVisiblePreservedAndNeverRevivesOlderSet()
    {
        var store = Store(); var snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        var newest = Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Last();
        File.WriteAllText(newest, "{\"body\":"); var bytes = File.ReadAllBytes(newest);
        var malformed = Capture(store); Assert.NotNull(malformed.IntentionalDifferences.Diagnostic);
        Assert.False(Family(malformed).Intentional);
        Assert.ThrowsAny<Exception>(() => store.SetObserved(Family(malformed), malformed, Token));
        Assert.Equal(bytes, File.ReadAllBytes(newest)); Assert.Equal(2, Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void CapacityReservesUndoAndReportsFiniteRetentionLimit()
    {
        var store = Store(2); var snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        snapshot = Capture(store);
        var error = Assert.Throws<IOException>(() => store.SetObserved(Family(snapshot), snapshot, Token));
        Assert.Contains("Retained records: 2/2", error.Message); Assert.Contains("Reset saved intentional differences", error.Message); Assert.Contains(store.Root, error.Message);
        Assert.False(Family(Capture(store)).Intentional);
    }

    [Fact]
    public void NativePlacementExcludesSkillsAndConfiguredMissingRootsAndRejectsSymlinks()
    {
        var snapshot = Capture(); var selected = Family(snapshot);
        foreach (var path in new[] { snapshot.Skills[0].ResolvedPath, Path.Combine(_root.FullName, "expected", "prefs") })
        {
            var inventory = snapshot with { ConfiguredRoots = [new(Path.Combine(_root.FullName, "expected"), Scope.Custom, null)] };
            Assert.Throws<IOException>(() => new IntentionalDifferenceStore(path).SetObserved(selected, inventory, Token));
        }
        if (!OperatingSystem.IsWindows())
        {
            var destination = Directory.CreateDirectory(Path.Combine(_root.FullName, "outside"));
            var link = Path.Combine(_root.FullName, "link"); Directory.CreateSymbolicLink(link, destination.FullName);
            Assert.Throws<IOException>(() => new IntentionalDifferenceStore(link).SetObserved(selected, snapshot, Token));
            Assert.Empty(destination.EnumerateFileSystemInfos());
        }
    }

    [Fact]
    public void AcknowledgementDoesNotSuppressProblemsOrCoverageOrWriteCleanupMarkers()
    {
        var store = Store(); var snapshot = Capture(store); store.SetObserved(Family(snapshot), snapshot, Token);
        snapshot = Capture(store);
        var broken = CapturedFindingTests.Copy(Path.Combine(_root.FullName, "broken")) with { Name = "broken", Validity = ValidityState.InvalidMetadata, Ignored = false };
        var protectedSnapshot = snapshot with { Skills = snapshot.Skills.Add(broken), ConfiguredRootsComplete = false };
        var queue = MaintenanceQueueBuilder.Build(protectedSnapshot, Token);
        Assert.True(queue.Findings.Single(f => f.Difference is not null).Intentional);
        Assert.Contains(queue.Findings, f => f.Classification == FindingClass.Problem);
        Assert.True(queue.Partial); Assert.All(snapshot.Skills, skill => Assert.False(skill.Ignored));
        var evidence = InstalledIssueEvidence.Capture(protectedSnapshot, 1, queue);
        Assert.Contains("intentional-difference", evidence.Findings[(snapshot.Skills[0].InstallationId, snapshot.Skills[0].ResolvedPath)]);
    }

    [Fact]
    public void CanceledPreferenceCannotCreateState()
    {
        var snapshot = Capture(); var selected = Family(snapshot); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Store().SetObserved(selected, snapshot, canceled.Token));
        Assert.False(Directory.Exists(Store().Root));
    }
}
