using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed partial class SharedReplacementRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedLegacySameTreeOwnerPlanInspectsRecoversInterruptedMoveAndUndoes(bool sameTimestamp)
    {
        var plan = await PersistLegacyEqualOwnerPlan(sameTimestamp);
        var owner = plan.Revision!.Owner!;
        Assert.False(owner.BeforeEntry.AsSpan().SequenceEqual(owner.DesiredEntry));
        Assert.Equal(sameTimestamp, owner.BeforeFingerprint == owner.DesiredFingerprint);
        var before = PayloadSnapshot.Capture(Payload, Token);
        var inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal("ready", inspected.Status);
        using (var operation = NativeStateDirectory.OpenOrCreate(Path.GetDirectoryName(plan.PlanPath)!, [], Token))
        using (var original = NativeDirectoryMoveLease.Open(Skills, Payload, Token))
            operation.ReceiveDirectory(original, "original", Token);
        inspected = Store().Inspect(plan.Id, Inventory(), Token);
        Assert.Equal("original-retained", inspected.Status);
        var recovered = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), false, Token);
        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal("installed", recovered.Status);
        Assert.True(recovered.MutationObserved);
        Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.Equal(owner.DesiredFingerprint, GhRegistryEditor.CaptureEntry(File.ReadAllBytes(Registry), "demo", Token).Fingerprint);
        if (!sameTimestamp) Assert.Equal(owner.DesiredEntry, Selected());
        inspected = Store().Inspect(plan.Id, Inventory(), Token);
        var undone = Store().Recover(plan.Id, inspected.ReviewFingerprint, Inventory(), true, Token);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.RootIdentity!.Value, PayloadSnapshot.Capture(Payload, Token).RootIdentity!.Value));
        Assert.Equal(before.Objects, PayloadSnapshot.Capture(Payload, Token).Objects);
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
    }

    [Fact]
    public async Task PersistedLegacyOwnerPlanCannotChangeUnapprovedExtensionFields()
    {
        var plan = await PersistLegacyEqualOwnerPlan(false, corruptExtension: true);
        var error = Assert.Throws<IOException>(() => Store().Inspect(plan.Id, Inventory(), Token));
        Assert.Contains("outside the approved tree/pin/timestamp policy", error.Message);
        Assert.Equal(OriginalRegistry, File.ReadAllText(Registry));
        Assert.True(File.Exists(Path.Combine(Payload, "extra")));
    }

    private async Task<ReplacementResult> PersistLegacyEqualOwnerPlan(bool sameTimestamp, bool corruptExtension = false)
    {
        var planned = await EqualPlan();
        var saved = JsonSerializer.Deserialize(File.ReadAllBytes(planned.PlanPath), ReplacementJsonContext.Default.ReplacementPlan)!;
        // Frozen pre-change Prepare output: it always rebuilt the selected
        // entry and emitted updatedAt, even for an equal tree/pin. Do not use
        // the current preparation helper to manufacture this compatibility case.
        var timestamp = sameTimestamp ? "2020-01-01T00:00:00Z" : "2025-01-01T00:00:00Z";
        var desired = Encoding.UTF8.GetBytes("{\"source\":\"harder/fixture\",\"sourceType\":\"github\",\"sourceUrl\":\"https://github.com/harder/fixture.git\",\"skillPath\":\"skills/demo\",\"installedAt\":\"2020-01-01T00:00:00Z\",\"selectedExtension\":{\"keep\":"
            + (corruptExtension ? "false" : "true") + "},\"skillFolderHash\":\"" + OldTree + "\",\"updatedAt\":\"" + timestamp + "\"}");
        var wrapped = Encoding.UTF8.GetBytes("{\"version\":3,\"skills\":{\"demo\":" + Encoding.UTF8.GetString(desired) + "}}");
        var fingerprint = GhRegistryEditor.CaptureEntry(wrapped, "demo", Token).Fingerprint;
        var owner = saved.Revision!.Owner! with { DesiredEntry = desired, DesiredFingerprint = fingerprint, UpdatedAt = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture) };
        saved = saved with { Revision = saved.Revision with { Owner = owner }, ReviewFingerprint = "" };
        // Reproduce the existing schema-2 canonical plan-file contract, rather
        // than relying on an in-memory result which never opens the old image.
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(saved, ReplacementJsonContext.Default.ReplacementPlan));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Canonical(document.RootElement, writer);
        saved = saved with { ReviewFingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())) };
        File.WriteAllBytes(planned.PlanPath, JsonSerializer.SerializeToUtf8Bytes(saved, ReplacementJsonContext.Default.ReplacementPlan));
        return planned with { PlanFingerprint = saved.ReviewFingerprint, Revision = saved.Revision };

        static void Canonical(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Canonical(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(item, writer); writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
