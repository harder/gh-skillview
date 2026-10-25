using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class PinDisplayPolicyTests
{
    private static SkillPackage Owner(string? pin) => new("test/repo", "github", null, null, null) { PinnedRef = pin };

    [Fact]
    public void SkillLessEmptyDirectoryHasNoPinWarningOrPinPolicyEvidence()
    {
        var temporary = Directory.CreateTempSubdirectory("skillview-empty-pin-display-");
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary.FullName, "empty"));
            var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with
            { ScannedRoots = [new(temporary.FullName, Scope.Custom, null)] }, TestContext.Current.CancellationToken);
            var entry = Assert.Single(queue.Entries);
            Assert.Equal("EmptyDirectory", entry.Issue);
            Assert.Null(entry.Cleanup!.Skill);
            Assert.Null(entry.PinDisplay);
            Assert.DoesNotContain("Pin policy", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
            var evidence = MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken, showDetails: true);
            Assert.DoesNotContain("pin state unknown", evidence);
            Assert.DoesNotContain("`Pin`", evidence);
        }
        finally { temporary.Delete(true); }
    }

    [Fact]
    public void LegacyEntriesUseTheirRecordedPinEvidenceWithoutInventingUnknownWarnings()
    {
        var skill = CopiesPresentationTests.Copy("/skills/demo");
        var entry = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] },
            TestContext.Current.CancellationToken).Entries.Single(entry => entry.Issue == "installation-review") with
        { PinDisplay = null };
        Assert.DoesNotContain("Pin policy", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
        Assert.Contains("no pin recorded", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken, showDetails: true));
        Assert.Contains("Routine updates leave this version pinned", MaintenanceQueueFormatter.Render(entry with { Pinned = true }, TestContext.Current.CancellationToken));
        Assert.Contains("Routine updates leave this version pinned", MaintenanceQueueFormatter.Render(entry with
        { Subject = entry.Subject with { Revision = entry.Subject.Revision with { PinnedRef = "v1" } } }, TestContext.Current.CancellationToken));
        skill = skill with { OwnershipClaims = [Owner("v1"), Owner(null)] };
        entry = entry with { Subject = OperationSubject.From(skill) };
        Assert.Contains("Pin records disagree", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParsedExplicitUnpinnedFrontmatterConflictsWithAnOwnerPin()
    {
        var (_, metadata, parsed) = FrontMatterParser.Parse("---\nname: demo\ndescription: Example\npinned: false\n---\nBody\n");
        Assert.True(parsed);
        Assert.False(metadata.Pinned);
        var skill = CopiesPresentationTests.Copy("/skills/demo") with { FrontMatter = metadata, Package = Owner("v1") };
        var pin = PinDisplayPolicy.From(skill);
        Assert.Equal(PinDisplayState.Conflicting, pin.State);
        Assert.True(pin.HasPin);
        Assert.Contains("**Pinned:** Conflicting", InstalledScreen.RenderDetail(skill));
        Assert.Contains("conflicting pin records", UpdateReviewPresentation.Selection(skill));
        var entry = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] },
            TestContext.Current.CancellationToken).Entries.Single(entry => entry.Issue == "installation-review");
        entry = entry with { PinDisplay = null, Cleanup = CopiesPresentationTests.Collision(skill) };
        Assert.Contains("Pin records disagree", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void MalformedBlankOwnerRefsRemainUnknownRatherThanConflictingOrUnpinned(string blank)
    {
        var skill = CopiesPresentationTests.Copy("/skills/demo") with { OwnershipClaims = [Owner(blank), Owner(null)] };
        var pin = PinDisplayPolicy.From(skill);
        Assert.Equal(PinDisplayState.Unknown, pin.State);
        Assert.False(pin.HasPin);
        Assert.Equal("?", pin.Flag);
        Assert.Contains("**Pinned:** Unknown", InstalledScreen.RenderDetail(skill));
        Assert.Contains("pin state unknown", CopiesPresentation.Render(new([skill], 0, 0, false), skill, skill));
        var entry = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] },
            TestContext.Current.CancellationToken).Entries.Single(entry => entry.Issue == "installation-review");
        Assert.True(entry.Pinned); // Historical wire boolean still counts any non-null owner ref.
        Assert.Equal(PinDisplayState.Unknown, entry.PinDisplay!.State);
    }

    [Fact]
    public void MalformedOwnerRefDoesNotInventDisagreementWithAnOtherwiseKnownPin()
    {
        var skill = CopiesPresentationTests.Copy("/skills/demo") with { OwnershipClaims = [Owner(" "), Owner("v1")] };
        var pin = PinDisplayPolicy.From(skill);
        Assert.Equal(PinDisplayState.Unknown, pin.State);
        Assert.True(pin.HasPin); // Retain the positive claim while the other policy remains invalid.
        Assert.Null(pin.PinnedRef);
    }

    [Fact]
    public void Schema3LockOnlyPinIsVisibleAcrossPresentationsWithoutChangingLegacyJson()
    {
        var temporary = Directory.CreateTempSubdirectory("skillview-pin-display-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "skills")).FullName;
            File.WriteAllText(Path.Combine(temporary.FullName, SkillLockFileReader.FileName),
                """{"version":3,"skills":{"demo":{"source":"test/repo","sourceType":"github","pinnedRef":"refs/tags/v1"}}}""");
            var owner = Assert.Single(new SkillLockFileReader(new Logger()).LoadObservations([root],
                TestContext.Current.CancellationToken, new(HomeDirectory: ""))).Package;
            var skill = CopiesPresentationTests.Copy(Path.Combine(root, "demo")) with { Package = owner };
            Assert.False(skill.Pinned);
            Assert.Equal("refs/tags/v1", skill.Revision.PinnedRef);
            var pin = PinDisplayPolicy.From(skill);
            Assert.Equal(PinDisplayState.Pinned, pin.State);
            Assert.Equal("p", pin.Flag);
            Assert.Contains("**Pinned:** Yes", InstalledScreen.RenderDetail(skill));
            Assert.Contains("**Pinned ref:** `refs/tags/v1`", InstalledScreen.RenderDetail(skill));
            Assert.Contains("Pin: refs/tags/v1", CopiesPresentation.Render(new([skill], 0, 0, false), skill, skill));
            Assert.Contains("refs/tags/v1", UpdateReviewPresentation.Selection(skill));
            var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
            var queue = MaintenanceQueueBuilder.Build(snapshot, TestContext.Current.CancellationToken);
            var entry = Assert.Single(queue.Entries.Where(entry => entry.Issue == "installation-review"));
            Assert.True(entry.Pinned);
            Assert.Equal(pin, entry.PinDisplay);
            Assert.Contains("Routine updates leave this version pinned", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
            using var inventoryJson = JsonDocument.Parse(CliDispatcher.RenderListJson(snapshot));
            var serialized = inventoryJson.RootElement.GetProperty("skills")[0];
            Assert.False(serialized.GetProperty("pinned").GetBoolean());
            Assert.Equal(JsonValueKind.Null, serialized.GetProperty("pinnedRef").ValueKind);
            Assert.Equal("refs/tags/v1", serialized.GetProperty("revision").GetProperty("pinnedRef").GetString());
            using var queueJson = JsonDocument.Parse(CliDispatcher.RenderQueueJson(queue));
            Assert.True(queueJson.RootElement.GetProperty("entries").EnumerateArray()
                .Single(row => row.GetProperty("issue").GetString() == "installation-review").GetProperty("pinned").GetBoolean());
            Assert.DoesNotContain("pinDisplay", queueJson.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally { temporary.Delete(true); }
    }

    [Theory]
    [InlineData("v1", "v2")]
    [InlineData("v1", null)]
    public void ConflictingOwnerPinsAreExplicitEvenWhenAgreedRevisionDropsThePin(string first, string? second)
    {
        var skill = CopiesPresentationTests.Copy("/skills/demo") with { OwnershipClaims = [Owner(first), Owner(second)] };
        Assert.Null(skill.Revision.PinnedRef);
        Assert.False(skill.Pinned);
        var pin = PinDisplayPolicy.From(skill);
        Assert.Equal(PinDisplayState.Conflicting, pin.State);
        Assert.True(pin.HasPin);
        Assert.Equal("!", pin.Flag);
        Assert.Contains("**Pinned:** Conflicting", InstalledScreen.RenderDetail(skill));
        Assert.Contains("conflicting pin records", CopiesPresentation.Render(new([skill], 0, 0, false), skill, skill));
        Assert.Contains("conflicting pin records", UpdateReviewPresentation.Selection(skill));
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] }, TestContext.Current.CancellationToken);
        foreach (var entry in queue.Entries)
        {
            Assert.Contains("Pin records disagree", MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken));
            var evidence = MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken, showDetails: true);
            Assert.Contains("conflicting pin records", evidence);
            Assert.DoesNotContain("no pin recorded", evidence);
        }
    }

    [Fact]
    public void FrontmatterAndOwnerDisagreementDoesNotTurnUnrelatedSourceConflictsIntoPinConflicts()
    {
        var skill = CopiesPresentationTests.Copy("/skills/demo") with
        {
            OwnershipClaims = [Owner("v1")],
            FrontMatter = SkillFrontMatter.Empty with { Pinned = true, PinnedRef = "v2" }
        };
        Assert.Equal(PinDisplayState.Conflicting, PinDisplayPolicy.From(skill).State);
        skill = skill with { FrontMatter = SkillFrontMatter.Empty, MetadataConflicts = ["Conflicting source records"] };
        Assert.Equal(PinDisplayState.Pinned, PinDisplayPolicy.From(skill).State);
        skill = skill with { OwnershipClaims = [Owner(null)] };
        Assert.Equal(PinDisplayState.Unpinned, PinDisplayPolicy.From(skill).State);
        skill = skill with { Validity = ValidityState.UnparsableFrontMatter };
        Assert.Equal(PinDisplayState.Unknown, PinDisplayPolicy.From(skill).State);
    }

    [Fact]
    public void InstalledPinCycleIncludesLockOnlyAndConflictingPinsButNeverCallsUnknownOrConflictingRowsUnpinned()
    {
        var plain = CopiesPresentationTests.Copy("/plain", "plain");
        var locked = CopiesPresentationTests.Copy("/locked", "locked") with { Package = Owner("v1") };
        var conflict = CopiesPresentationTests.Copy("/conflict", "conflict") with { OwnershipClaims = [Owner("v1"), Owner(null)] };
        var unknown = CopiesPresentationTests.Copy("/unknown", "unknown") with { Validity = ValidityState.UnparsableFrontMatter };
        var malformed = CopiesPresentationTests.Copy("/malformed", "malformed") with { Package = Owner(" ") };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(InventorySnapshot.Empty), (_, _, _) => Task.CompletedTask, () => { }, () => { });
        view.LoadSeeded(InventorySnapshot.Empty with { Skills = [plain, locked, conflict, unknown, malformed] });
        view.FocusList();
        view.NewKeyDownEvent(new Key('P'));
        Assert.Equal(["locked", "conflict"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('P'));
        Assert.Equal(["plain"], view.VisibleSkillNamesForTests);
        view.NewKeyDownEvent(new Key('P'));
        Assert.Equal(5, view.VisibleSkillNamesForTests.Count);
    }
}
