using SkillView.Maintenance;
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
}
