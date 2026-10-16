using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    private void PinUnpinFixture()
    {
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), File.ReadAllText(Path.Combine(Payload, "SKILL.md"))
            .Replace("  github-ref:", "  github-pinned: 'old-pin' # retained comment\n  github-ref:"));
        File.WriteAllText(Registry, OriginalRegistry.Replace("\"selectedExtension\":", "\"pinnedRef\": \"old-pin\", \"selectedExtension\":"));
        if (OperatingSystem.IsWindows())
            foreach (var path in new[] { Payload, Path.Combine(Payload, "SKILL.md"), Path.Combine(Payload, "extra") })
                WindowsPayloadMetadataTests.SetFixtureOwner(path);
    }

    private ReplacementResult? UnpinPlan()
    {
        var inventory = Inventory(); var before = PayloadSnapshot.Capture(Payload, Token); var owner = File.ReadAllBytes(Registry);
        try { return Assert.IsType<ReplacementResult>(Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token).Plan); }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
        {
            Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
            Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
            Assert.Equal(owner, File.ReadAllBytes(Registry));
            Assert.Empty(Directory.EnumerateFiles(State, "plan.json", SearchOption.AllDirectories));
            return null;
        }
    }

    [Fact]
    public void SharedUnpinNoopLeavesOwnerNativePayloadAndStateUntouched()
    {
        var before = File.ReadAllBytes(Registry); var payload = PayloadSnapshot.Capture(Payload, Token); var inventory = Inventory();
        var result = Store().PlanUnpin(Assert.Single(inventory.Skills), inventory, Token);
        Assert.NotNull(result.Unchanged); Assert.Null(result.Plan); Assert.False(Directory.Exists(State));
        Assert.Equal(before, File.ReadAllBytes(Registry)); Assert.Equal(payload.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void SharedUnpinPreservesRawEntryFieldsFreshOtherOwnerBytesEveryExposureAndExactUndo()
    {
        PinUnpinFixture();
        var before = PayloadSnapshot.Capture(Payload, Token); var owner = File.ReadAllBytes(Registry); var selected = Selected();
        var plan = UnpinPlan(); if (plan is null) return;
        Assert.True(plan.Subject.Agents.Length >= 2); Assert.NotNull(plan.PinPolicy!.Owner);
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var link, out var error), error);
        var current = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(owner).Replace("\"extension\":[1,2]", "\"extension\":[1,2,3], \"fresh\":true"));
        File.WriteAllBytes(Registry, current);
        var expected = GhRegistryEditor.PrepareUnpin(current, "demo", plan.PinPolicy.Owner!.BeforeFingerprint, Token).DesiredBytes;
        var result = Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only");
        Assert.True(result.Succeeded, result.Message); Assert.Equal(expected, File.ReadAllBytes(Registry));
        using (var entry = JsonDocument.Parse(Selected()))
        {
            Assert.Equal(OldTree, entry.RootElement.GetProperty("skillFolderHash").GetString());
            Assert.Equal("2020-01-01T00:00:00Z", entry.RootElement.GetProperty("updatedAt").GetString());
            Assert.False(entry.RootElement.TryGetProperty("pinnedRef", out _));
        }
        Assert.Contains("Local instructions", File.ReadAllText(Path.Combine(Payload, "SKILL.md")));
        Assert.Equal("Keep local extra", File.ReadAllText(Path.Combine(Payload, "extra")));
        Assert.True(SecureRemovalBackend.TryCaptureLinkIdentity(Exposure, out var after, out error), error); Assert.Equal(link, after);
        var inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        Assert.True(Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token, "unpin-only").Succeeded);
        Assert.Equal(current, File.ReadAllBytes(Registry)); Assert.Equal(selected, Selected());
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint); Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void SharedUnpinRejectsChangedSelectedOwnerBeforeAnyPayloadMove()
    {
        PinUnpinFixture(); var plan = UnpinPlan(); if (plan is null) return;
        var original = PayloadSnapshot.Capture(Payload, Token);
        File.WriteAllText(Registry, File.ReadAllText(Registry).Replace("\"selectedExtension\":{\"keep\":true}", "\"selectedExtension\":{\"keep\":false}"));
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only"));
        Assert.False(Directory.Exists(plan.RetainedPath)); Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Fact]
    public void SharedUnpinRejectsSemanticallyEquivalentSelectedEntryReformatBeforeApplyAndUndo()
    {
        PinUnpinFixture(); var plan = UnpinPlan(); if (plan is null) return;
        var original = PayloadSnapshot.Capture(Payload, Token);
        var beforeBytes = File.ReadAllBytes(Registry);
        var reformatted = beforeBytes.AsSpan().ToArray();
        var text = Encoding.UTF8.GetString(reformatted).Replace("\"selectedExtension\":{\"keep\":true}", "\"selectedExtension\" : { \"keep\" : true }");
        File.WriteAllText(Registry, text);
        Assert.Equal(plan.PinPolicy!.Owner!.BeforeFingerprint, GhRegistryEditor.CaptureEntry(File.ReadAllBytes(Registry), "demo", Token).Fingerprint);
        Assert.Throws<IOException>(() => Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only"));
        Assert.Equal(text, File.ReadAllText(Registry)); Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        File.WriteAllBytes(Registry, beforeBytes);
        Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only").Succeeded);
        var installed = PayloadSnapshot.Capture(Payload, Token);
        text = File.ReadAllText(Registry).Replace("\"selectedExtension\":{\"keep\":true}", "\"selectedExtension\" : { \"keep\" : true }");
        File.WriteAllText(Registry, text);
        Assert.Equal(plan.PinPolicy.Owner.DesiredFingerprint, GhRegistryEditor.CaptureEntry(File.ReadAllBytes(Registry), "demo", Token).Fingerprint);
        Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token, "unpin-only"));
        Assert.Equal(text, File.ReadAllText(Registry)); Assert.Equal(installed.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SharedUnpinTornForwardUndoAndCompensationUseExplicitRepairThenRestart(bool undo, bool compensation)
    {
        PinUnpinFixture(); var before = File.ReadAllBytes(Registry); var original = PayloadSnapshot.Capture(Payload, Token);
        var plan = UnpinPlan(); if (plan is null) return;
        if (undo) Assert.True(Store().Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only").Succeeded);
        var killed = Store(step =>
        {
            if (compensation)
            {
                if (step == "registry-truncated") throw new IOException("Interrupted pin write");
                if (step.EndsWith("-restore-intent.json", StringComparison.Ordinal)) throw new SimulatedProcessDeath();
            }
            else if (step == "registry-truncated") throw new SimulatedProcessDeath();
        });
        if (undo)
        {
            var current = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
            Assert.Throws<SimulatedProcessDeath>(() => killed.Recover(plan.Id, current.ReviewFingerprint, Inventory(), true, Token, "unpin-only"));
        }
        else Assert.Throws<SimulatedProcessDeath>(() => killed.Apply(plan.Id, plan.PlanFingerprint, Inventory(), Token, "unpin-only", "unpin-only"));
        Assert.Empty(File.ReadAllBytes(Registry));
        var inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        Assert.Equal("torn", inspect.Owner!.State); Assert.NotNull(inspect.Owner.Repair);
        Assert.Throws<IOException>(() => Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), undo, Token, "unpin-only"));
        var repaired = Store().RepairOwner(plan.Id, inspect.ReviewFingerprint, Inventory(), Token, "unpin-only");
        Assert.True(repaired.Succeeded, repaired.Message);
        inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
        var completed = Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), undo, Token, "unpin-only");
        Assert.True(completed.Succeeded, completed.Message);
        if (!undo)
        {
            inspect = Store().Inspect(plan.Id, Inventory(), Token, "unpin-only");
            Assert.True(Store().Recover(plan.Id, inspect.ReviewFingerprint, Inventory(), true, Token, "unpin-only").Succeeded);
        }
        Assert.Equal(before, File.ReadAllBytes(Registry)); Assert.Equal(original.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(original.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
    }
}
