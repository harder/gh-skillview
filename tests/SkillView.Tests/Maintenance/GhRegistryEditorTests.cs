using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhRegistryEditorTests
{
    private const string NewTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Selected = "publisher/demo";
    private const string Entry = """
        {"sourceType":"github","source":"owner/repo","sourceUrl":"https://github.com/owner/repo.git","skillPath":"skills/demo","skillFolderHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","installedAt":"original timestamp","updatedAt":"old timestamp","pinnedRef":"approved-pin","extension": { "nested" : [1, {"a": true}] }}
        """;
    private static string Registry(string entry = Entry) => "{\n  \"version\":3,\"extension\":{\"future\":[1,true]},\n  \"skills\":{\"other\": { \"arbitrary\" : [2,3] },\"" + Selected + "\":" + entry + "},\n  \"dismissed\":{\"notice\":true}\n}\n";
    private static string Fingerprint(string entry = Entry)
    {
        using var document = JsonDocument.Parse(entry);
        return OwnershipRecordFingerprint.Compute(document.RootElement, true);
    }
    private static GhRegistryPatch Prepare(string input, string? pin = "approved-pin", string? expected = null, string selector = Selected) =>
        GhRegistryEditor.Prepare(Encoding.UTF8.GetBytes(input), selector, expected ?? Fingerprint(), NewTree, pin,
            DateTimeOffset.Parse("2026-10-15T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedRevisionTransitionPreservesUnrelatedRawBytesAndExtensions(bool unpin)
    {
        var original = Registry(); var patch = Prepare(original, unpin ? null : "approved-pin");
        var desired = Encoding.UTF8.GetString(patch.DesiredBytes);
        var start = original.IndexOf(Entry, StringComparison.Ordinal);
        Assert.StartsWith(original[..start], desired); Assert.EndsWith(original[(start + Entry.Length)..], desired);
        Assert.Equal(original, Encoding.UTF8.GetString(patch.BeforeBytes));
        using var document = JsonDocument.Parse(patch.DesiredBytes);
        var entry = document.RootElement.GetProperty("skills").GetProperty(Selected);
        Assert.Equal(NewTree, entry.GetProperty("skillFolderHash").GetString());
        Assert.Equal("original timestamp", entry.GetProperty("installedAt").GetString());
        Assert.Equal("2026-10-15T05:00:00Z", entry.GetProperty("updatedAt").GetString());
        Assert.Equal(!unpin, entry.TryGetProperty("pinnedRef", out var pin));
        if (!unpin) Assert.Equal("approved-pin", pin.GetString());
        Assert.Equal("{ \"nested\" : [1, {\"a\": true}] }", entry.GetProperty("extension").GetRawText());
        Assert.Equal(Fingerprint(), patch.BeforeEntryFingerprint);
        Assert.Equal(OwnershipRecordFingerprint.Compute(entry, true), patch.DesiredEntryFingerprint);
    }

    [Fact]
    public void UnrelatedManagerChangesBeforePreparationSurviveButSelectedChangesRefuse()
    {
        var concurrent = Registry().Replace("[2,3]", "[2,3,4]");
        Assert.Contains("[2,3,4]", Encoding.UTF8.GetString(Prepare(concurrent).DesiredBytes));
        Assert.Throws<IOException>(() => Prepare(Registry(Entry.Replace("approved-pin", "other-pin"))));
    }

    [Fact]
    public void IdenticalEntryAtAnotherSelectorIsNotChanged()
    {
        var original = Registry().Replace("{ \"arbitrary\" : [2,3] }", Entry);
        using var desired = JsonDocument.Parse(Prepare(original).DesiredBytes);
        var skills = desired.RootElement.GetProperty("skills");
        Assert.Equal(Entry, skills.GetProperty("other").GetRawText());
        Assert.Equal(NewTree, skills.GetProperty(Selected).GetProperty("skillFolderHash").GetString());
    }

    [Fact]
    public void ValidSelectedEntryCannotAuthorizeUnknownSchemaOrDuplicateProperties()
    {
        Assert.Throws<IOException>(() => Prepare(Registry().Replace("\"version\":3", "\"version\":4")));
        Assert.Throws<IOException>(() => Prepare(Registry().Replace("\"version\":3", "\"version\":3,\"version\":3")));
        Assert.Throws<IOException>(() => Prepare(Registry().Replace("\"future\":[1,true]", "\"future\":[1,true],\"future\":[]")));
        Assert.Throws<IOException>(() => Prepare(Registry().Replace("\"" + Selected + "\":" + Entry,
            "\"" + Selected + "\":" + Entry + ",\"" + Selected + "\":" + Entry)));
    }

    [Fact]
    public void EscapedUnicodeSelectorAndNestedPeerRemainDistinct()
    {
        var input = "{\"version\":3,\"skills\":{\"publisher/d\\u00e9mo\":" + Entry
            + ",\"peer\":{\"nested\":[{\"skills\":{\"decoy\":[]}}]}},\"end\":[1]}";
        var desired = Prepare(input, selector: "publisher/démo").DesiredBytes;
        using var document = JsonDocument.Parse(desired);
        var skills = document.RootElement.GetProperty("skills");
        Assert.Equal(NewTree, skills.GetProperty("publisher/démo").GetProperty("skillFolderHash").GetString());
        Assert.Equal("{\"nested\":[{\"skills\":{\"decoy\":[]}}]}", skills.GetProperty("peer").GetRawText());
    }

    [Fact]
    public void InvalidKnownFieldTypeAndConcurrentSelectedExtensionChangeRefuse()
    {
        var invalid = Entry.Replace("\"installedAt\":\"original timestamp\"", "\"installedAt\":17");
        Assert.Throws<IOException>(() => Prepare(Registry(invalid), expected: Fingerprint(invalid)));
        Assert.Throws<IOException>(() => Prepare(Registry(Entry.Replace("\"a\": true", "\"a\": false"))));
    }

    [Theory]
    [InlineData("{\"version\":4,\"skills\":{}}")]
    [InlineData("{\"version\":3,\"skills\":{},\"skills\":{}}")]
    [InlineData("{\"version\":3,\"skills\":{},\"extra\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"version\":3,\"skills\":")]
    public void UnsupportedOrAmbiguousJsonRefuses(string input) => Assert.Throws<IOException>(() => Prepare(input));

    [Fact]
    public void InputByteLimitAndPreCanceledAdmissionRefuse()
    {
        Assert.Throws<IOException>(() => Prepare(new string(' ', SkillLockFileReader.MaxFileBytes + 1)));
        Assert.Throws<OperationCanceledException>(() => GhRegistryEditor.Prepare([], Selected, Fingerprint(), NewTree,
            null, DateTimeOffset.UtcNow, new(true)));
    }
}
