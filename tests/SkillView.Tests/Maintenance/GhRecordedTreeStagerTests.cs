using SkillView.Maintenance;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhRecordedTreeStagerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"sha\":\"abc\",\"truncated\":false,\"tree\":[{}]}")]
    [InlineData("{\"sha\":\"abc\",\"truncated\":false,\"tree\":[{\"path\":\"x\",\"type\":\"blob\",\"sha\":\"def\",\"mode\":\"100644\",\"size\":\"huge\"}]}")]
    [InlineData("{\"sha\":\"abc\",\"truncated\":false,\"tree\":[{\"path\":\"x\",\"type\":\"blob\",\"sha\":\"def\",\"mode\":\"100644\",\"size\":1.5}]}")]
    public void MalformedSuccessfulPayloadIsAProviderRefusal(string payload) =>
        Assert.Throws<IOException>(() => GhRecordedTreeStager.ParseTreeResponse(payload, "abc"));
    [Fact]
    public void TreeAndBlobIdentifiersBindExactNamesModesAndBytes()
    {
        var content = Encoding.UTF8.GetBytes("approved\n");
        var blob = GhRecordedTreeStager.GitObjectHash("blob", content);
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.UTF8.GetBytes("100644 SKILL.md\0")); bytes.Write(Convert.FromHexString(blob));
        var tree = GhRecordedTreeStager.GitObjectHash("tree", bytes.ToArray());
        var json = $"[{{\"path\":\"SKILL.md\",\"type\":\"blob\",\"mode\":\"100644\",\"sha\":\"{blob}\",\"size\":{content.Length}}}]";
        using var document = JsonDocument.Parse(json);
        GhRecordedTreeStager.VerifyTreeIdentity(document.RootElement, tree);
        foreach (var altered in new[] { json.Replace("SKILL.md", "OTHER.md"), json.Replace("100644", "100755"), json.Replace(blob, new string('0', 40)) })
        {
            using var changed = JsonDocument.Parse(altered);
            Assert.Throws<IOException>(() => GhRecordedTreeStager.VerifyTreeIdentity(changed.RootElement, tree));
        }
        Assert.NotEqual(blob, GhRecordedTreeStager.GitObjectHash("blob", Encoding.UTF8.GetBytes("tampered\n")));
    }

    [Fact]
    public void MissingParentAndDuplicatePathsCannotBecomeWriteAuthority()
    {
        foreach (var json in new[]
        {
            "[{\"path\":\"missing/file\",\"type\":\"blob\",\"mode\":\"100644\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]",
            "[{\"path\":\"file\",\"type\":\"blob\",\"mode\":\"100644\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},{\"path\":\"file\",\"type\":\"blob\",\"mode\":\"100644\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]"
        })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<IOException>(() => GhRecordedTreeStager.VerifyTreeIdentity(document.RootElement, new string('a', 40)));
        }
    }

}
