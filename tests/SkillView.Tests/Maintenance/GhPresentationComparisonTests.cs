using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhPresentationComparisonTests
{
    [Fact]
    public void MappingOrderQuotingAndGhBodyDelimiterHandlingCompareEqual()
    {
        const string source = "---\nname: demo\ndescription: Demo\nmetadata:\n  version: '1.0'\n  vendor:\n    nested: value\nallowed-tools: [Read, Bash]\n---\n\nBody";
        const string installed = "---\nallowed-tools:\n    - Read\n    - Bash\ndescription: \"Demo\"\nmetadata:\n    vendor: {nested: 'value'}\n    version: \"1.0\"\nname: demo\n---\nBody\n";
        var before = GhPresentationComparison.Fingerprint(source);
        Assert.NotNull(before);
        Assert.Equal(before, GhPresentationComparison.Fingerprint(installed));
    }

    [Theory]
    [InlineData("name: changed\ndescription: Demo\n", "Body\n")]
    [InlineData("name: demo\ndescription: Changed\n", "Body\n")]
    [InlineData("name: demo\ndescription: Demo\nunknown: value\n", "Body\n")]
    [InlineData("name: demo\ndescription: Demo\n", "Changed\n")]
    [InlineData("name: demo\ndescription: Demo\n", " Body\n")]
    [InlineData("name: demo\ndescription: Demo\n", "Body \n")]
    [InlineData("name: demo\ndescription: Demo\n", "Body\n\n")]
    public void AuthoredValuesBodySpacesAndTrailingBlankLinesRemainDifferent(string yaml, string body)
    {
        var before = GhPresentationComparison.Fingerprint("---\nname: demo\ndescription: Demo\n---\nBody\n");
        Assert.NotNull(before);
        Assert.NotEqual(before, GhPresentationComparison.Fingerprint("---\n" + yaml + "---\n" + body));
    }

    [Fact]
    public void UnknownNestedFieldsNodeKindsAndSequenceOrderRemainDifferent()
    {
        string Hash(string yaml) => Assert.IsType<string>(GhPresentationComparison.Fingerprint("---\nname: demo\ndescription: Demo\n" + yaml + "---\nBody\n"));
        Assert.NotEqual(Hash("unknown: {nested: first}\n"), Hash("unknown: {nested: second}\n"));
        Assert.NotEqual(Hash("unknown: [first, second]\n"), Hash("unknown: [second, first]\n"));
        Assert.NotEqual(Hash("unknown: [first]\n"), Hash("unknown: first\n"));
    }

    [Theory]
    [InlineData("unknown: 42\n")]
    [InlineData("unknown: true\n")]
    [InlineData("unknown: null\n")]
    [InlineData("unknown: 0xff\n")]
    [InlineData("unknown: 1e9999\n")]
    [InlineData("unknown: .nan\n")]
    [InlineData("unknown: &value scalar\ncopy: *value\n")]
    [InlineData("unknown: !!str value\n")]
    [InlineData("unknown: value\nunknown: value\n")]
    public void AmbiguousTypesAliasesTagsAndDuplicatesCannotAuthorizePresentationFallback(string yaml)
    {
        Assert.Null(GhPresentationComparison.Fingerprint("---\nname: demo\ndescription: Demo\n" + yaml + "---\nBody\n"));
    }

    [Fact]
    public void ExcessDepthOrNodesCannotAuthorizePresentationFallback()
    {
        string Document(string yaml) => "---\nname: demo\ndescription: Demo\nunknown: " + yaml + "\n---\nBody\n";
        var depth = SkillView.Inventory.FrontMatterParser.MaxDepth + 1;
        Assert.Null(GhPresentationComparison.Fingerprint(Document(new string('[', depth) + "value" + new string(']', depth))));
        Assert.Null(GhPresentationComparison.Fingerprint(Document("[" + string.Join(',', Enumerable.Repeat("value", SkillView.Inventory.FrontMatterParser.MaxNodes)) + "]")));
        Assert.NotNull(GhPresentationComparison.Fingerprint(Document("'42'")));
    }
}
