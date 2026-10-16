using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Loading;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class GeminiLoadingCaptureTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-gemini-context-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temporary.Delete(true);
    private LoadingConfigurationDocument Document(object value)
    {
        var path = Path.Combine(_temporary.FullName, Guid.NewGuid() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(value));
        return new LoadingConfigurationReader().Read(path, "fixture", Token);
    }

    [Fact]
    public void ExtensionRulesKeepStoredCaseLiteralQuestionMarksAndLastMatch()
    {
        var document = Document(new { Example = new { overrides = new[] { "!/*", "/Work/*", "!/Work/private/*", "/Work/private/safe/*" } } });
        Assert.True(GeminiLoadingCapture.ExtensionEnabled(document, "Example", "/Work", Token));
        Assert.False(GeminiLoadingCapture.ExtensionEnabled(document, "Example", "/work", Token));
        Assert.False(GeminiLoadingCapture.ExtensionEnabled(document, "Example", "/Work/private", Token));
        Assert.True(GeminiLoadingCapture.ExtensionEnabled(document, "Example", "/Work/private/safe/child", Token));
        Assert.True(GeminiLoadingCapture.ExtensionEnabled(document, "example", "/work", Token)); // Exact manifest key.
        var literal = Document(new { Example = new { overrides = new[] { "!/*", "/Work?/" } } });
        Assert.False(GeminiLoadingCapture.ExtensionEnabled(literal, "Example", "/WorkX", Token));
        Assert.True(GeminiLoadingCapture.ExtensionEnabled(literal, "Example", "/Work?", Token));
    }

    [Theory]
    [InlineData("/", "!/")]
    [InlineData("\\\\server\\share", "!//server/share/")]
    public void ExtensionMatchingPreservesRootAndUncSpelling(string cwd, string rule)
    {
        var document = Document(new { Example = new { overrides = new[] { rule } } });
        Assert.False(GeminiLoadingCapture.ExtensionEnabled(document, "Example", cwd, Token));
    }

    [Fact]
    public void InvalidManifestAndDuplicateNamesNeverEstablishAnotherExtensionsEligibility()
    {
        void Extension(string directory, string name, string version)
        {
            var path = Path.Combine(_temporary.FullName, "extensions", directory); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "gemini-extension.json"), JsonSerializer.Serialize(new { name, version }));
        }
        Extension("invalid-name", "bad_name", "1"); Extension("invalid-version", "valid-name", "");
        Extension("one", "duplicate", "1"); Extension("two", "duplicate", "2"); Extension("three", "unique", "1");
        var layers = ImmutableArray.CreateBuilder<LoadingSettingsLayer>(); var diagnostics = ImmutableArray.CreateBuilder<string>();
        var blockers = ImmutableArray.CreateBuilder<string>();
        var extensions = GeminiLoadingCapture.Extensions(_temporary.FullName, _temporary.FullName,
            new LoadingConfigurationReader(), layers, diagnostics, blockers, Token);
        Assert.All(extensions.Where(item => item.InstalledDirectory.EndsWith("invalid-name", StringComparison.Ordinal)
            || item.InstalledDirectory.EndsWith("invalid-version", StringComparison.Ordinal)), item => Assert.Null(item.Enabled));
        var settings = new LoadingSettings(layers.ToImmutable(), []) { LocalPolicyBlockers = blockers.ToImmutable() };
        Assert.True(settings.HasUncertainLocalPolicy);
        Assert.Contains(blockers, item => item.Contains("Duplicate Gemini", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedExtensionSchemaIsUnknownAndCanceledEvaluationCannotDefaultToEnabled()
    {
        var invalid = Document(new { Example = new { overrides = new object[] { false } } });
        Assert.Null(GeminiLoadingCapture.ExtensionEnabled(invalid, "Example", "/work", Token));
        var oversized = Document(new { Example = new { overrides = new[] { "!" + new string('*', 4096) } } });
        Assert.Null(GeminiLoadingCapture.ExtensionEnabled(oversized, "Example", "/work", Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GeminiLoadingCapture.ExtensionEnabled(Document(new { }), "Example", "/work", canceled.Token));
    }

    [Fact]
    public void TrustParentUsesOriginalRuleSpecificityAndMalformedEnumsStayUnknown()
    {
        var parent = _temporary.FullName; var child = Path.Combine(parent, "child"); Directory.CreateDirectory(child);
        var document = Document(new Dictionary<string, string> { [parent] = "DO_NOT_TRUST", [child] = "TRUST_PARENT" });
        Assert.Equal("trusted", GeminiLoadingCapture.SavedTrust(document, parent, Token));
        Assert.Equal("unknown", GeminiLoadingCapture.SavedTrust(Document(new Dictionary<string, string> { [parent] = "trust_folder" }), child, Token));
        Assert.Equal("no-matching-rule", GeminiLoadingCapture.SavedTrust(Document(new Dictionary<string, string> { [child] = "TRUST_FOLDER" }), parent, Token));
    }

    [Fact]
    public void DisappearingListedOpenCodeChildMarksTheCaptureIncomplete()
    {
        var agents = Path.Combine(_temporary.FullName, "agents"); Directory.CreateDirectory(agents);
        var header = Path.Combine(agents, "build.md"); File.WriteAllText(header, "---\npermission:\n  skill: deny\n---\n");
        var layers = ImmutableArray.CreateBuilder<LoadingSettingsLayer>(); var blockers = ImmutableArray.CreateBuilder<string>();
        new OpenCodeAgentCapture(path => { if (path == header) File.Delete(path); }).Capture(_temporary.FullName,
            new LoadingConfigurationReader(), layers, blockers, Token);
        Assert.Contains(blockers, message => message.Contains("incomplete", StringComparison.Ordinal));
        Assert.Empty(layers);
    }
}
