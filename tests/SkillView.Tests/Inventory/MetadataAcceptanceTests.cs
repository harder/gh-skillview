using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class MetadataAcceptanceTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-metadata-acceptance-");
    public void Dispose() => temporary.Delete(true);

    [Theory]
    [InlineData("|", "First line\nsecond line\n")]
    [InlineData("|-", "First line\nsecond line")]
    [InlineData("|+", "First line\nsecond line\n\n")]
    [InlineData(">", "First line second line\n")]
    [InlineData(">-", "First line second line")]
    [InlineData(">+", "First line second line\n\n")]
    public void BlockDescriptionsRetainYamlSemanticsAndAuthoredFields(string style, string expected)
    {
        var source = $"---\nname: demo\ndescription: {style}\n  First line\n  second line\n\nallowed-tools: Read Bash(git log:*)\nmetadata:\n  version: \"1.0\"\n  vendor/example: keep\n---\nBody\n";
        var (body, metadata, parsed) = FrontMatterParser.Parse(source);
        Assert.True(parsed);
        Assert.Empty(metadata.Diagnostics);
        Assert.Equal(expected, metadata.Description);
        Assert.Equal("Body\n", body);
        Assert.Equal(["Read", "Bash(git log:*)"], metadata.AllowedTools);
        Assert.Equal("1.0", metadata.Version);
        Assert.Equal("keep", metadata.Metadata["vendor/example"]);
        var skill = Scan(source);
        Assert.Equal(ValidityState.Valid, skill.Validity);
        Assert.Equal(expected, skill.FrontMatter.Description);
        Assert.Equal(metadata.AllowedTools.ToArray(), skill.FrontMatter.AllowedTools.ToArray());
        Assert.Equal("1.0", skill.FrontMatter.Version);
        Assert.Equal("keep", skill.FrontMatter.Metadata["vendor/example"]);
    }

    [Theory]
    [InlineData("", "description: valid", "name")]
    [InlineData("name: \"\"", "description: valid", "name")]
    [InlineData("name: null", "description: valid", "name")]
    [InlineData("name: false", "description: valid", "name")]
    [InlineData("name: 42", "description: valid", "name")]
    [InlineData("name: []", "description: valid", "name")]
    [InlineData("name: UpperCase", "description: valid", "Name")]
    [InlineData("name: bad--name", "description: valid", "Name")]
    [InlineData("name: demo", "", "description")]
    [InlineData("name: demo", "description: \"\"", "description")]
    [InlineData("name: demo", "description: null", "description")]
    [InlineData("name: demo", "description: false", "description")]
    [InlineData("name: demo", "description: 42", "description")]
    [InlineData("name: demo", "description: []", "description")]
    public void InvalidRequiredFieldsRemainDiagnosticThroughScanner(string name, string description, string diagnostic)
    {
        var source = $"---\n{name}\n{description}\n---\nBody\n";
        var (_, metadata, parsed) = FrontMatterParser.Parse(source);
        Assert.True(parsed); // A valid YAML document can still be invalid skill metadata.
        Assert.Contains(metadata.Diagnostics, message => message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
        var skill = Scan(source);
        Assert.NotEqual(ValidityState.Valid, skill.Validity);
        Assert.Contains(skill.FrontMatter.Diagnostics, message => message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
        var candidate = Assert.Single(CleanupClassifier.Classify(new InventorySnapshot
        {
            Skills = [skill],
            ScannedRoots = [new(skill.ScanRoot, Scope.Custom, null)],
            UsedGhSkillList = false,
            CapturedAt = DateTimeOffset.UtcNow
        }, []));
        Assert.True(candidate.ReviewOnly);
        Assert.Equal(CleanupClassifier.CandidateKind.Malformed, candidate.Kind);
    }

    [Fact]
    public void OverlongRequiredValuesRemainVisibleAndInvalid()
    {
        foreach (var fields in new[]
        {
            "name: " + new string('x', 65) + "\ndescription: valid",
            "name: demo\ndescription: " + new string('x', 1025)
        })
        {
            var skill = Scan($"---\n{fields}\n---\nBody\n");
            Assert.NotEqual(ValidityState.Valid, skill.Validity);
            Assert.NotEmpty(skill.FrontMatter.Diagnostics);
        }
    }

    [Fact]
    public void FlatYamlNodeLimitIsIndependentOfCharacterAndDepthLimits()
    {
        static string Document(int pairs)
        {
            var text = new StringBuilder("---\nname: demo\ndescription: Node budget fixture\n");
            for (var index = 0; index < pairs; index++) text.AppendLine($"field-{index}: x");
            return text.Append("---\nBody\n").ToString();
        }
        var allowedPairs = (FrontMatterParser.MaxNodes - 5) / 2;
        var control = Document(allowedPairs);
        var excessive = Document(allowedPairs + 1);
        Assert.True(excessive.Length < FrontMatterParser.MaxYamlChars);
        Assert.True(FrontMatterParser.Parse(control).Parsed);
        var (_, metadata, parsed) = FrontMatterParser.Parse(excessive);
        Assert.False(parsed);
        Assert.Contains(metadata.Diagnostics, message => message.Contains("complexity limits", StringComparison.Ordinal));
    }

    private InstalledSkill Scan(string source)
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, Guid.NewGuid().ToString("N")));
        var payload = Directory.CreateDirectory(Path.Combine(root.FullName, "demo"));
        File.WriteAllText(Path.Combine(payload.FullName, "SKILL.md"), source);
        return Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(root.FullName, Scope.Custom, null)]));
    }
}
