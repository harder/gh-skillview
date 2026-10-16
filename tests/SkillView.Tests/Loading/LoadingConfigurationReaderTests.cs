using System.Collections.Immutable;
using SkillView.Loading;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class LoadingConfigurationReaderTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-loading-config-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _temporary.Delete(true);
    private LoadingConfigurationDocument Read(string name, string text)
    {
        var path = Path.Combine(_temporary.FullName, name);
        File.WriteAllText(path, text);
        return new LoadingConfigurationReader().Read(path, "fixture configuration", Token);
    }

    [Fact]
    public void TomlArrayTablesKeepPathsAndTypedEnablementWithoutReflection()
    {
        var document = Read("config.toml", """
            unrelated_secret = "never print this credential"
            [[skills.config]]
            path = 'C:\skills\demo\SKILL.md'
            enabled = false
            [[skills.config]]
            path = "/skills/other"
            enabled = true
            """);
        Assert.Equal("observed", document.Evidence.State);
        Assert.NotNull(document.Evidence.Sha256);
        var items = document.Root!.Get("skills", "config")!.Items;
        Assert.Equal(2, items.Length);
        Assert.False(items[0].Get("enabled")!.Boolean);
        Assert.Equal("C:\\skills\\demo\\SKILL.md", items[0].Get("path")!.Text);
        Assert.True(items[1].Get("enabled")!.Boolean);
        Assert.Null(document.Evidence.Diagnostic);
    }

    [Theory]
    [InlineData("config.json", "{\"a\": false, \"b\": \"false\"}")]
    [InlineData("config.yaml", "a: false\nb: \"false\"\n")]
    [InlineData("config.toml", "a = false\nb = \"false\"\n")]
    public void AQuotedBooleanCannotSilentlyDisableOrEnable(string name, string input)
    {
        var document = Read(name, input);
        Assert.Equal("observed", document.Evidence.State);
        Assert.Equal("boolean", document.Root!.Get("a")!.Kind);
        Assert.False(document.Root.Get("a")!.Boolean);
        Assert.Equal("string", document.Root.Get("b")!.Kind);
        Assert.Null(document.Root.Get("b")!.Boolean);
    }

    [Fact]
    public void PermissionPatternSourceOrderSurvivesParsing()
    {
        var document = Read("opencode.jsonc", """
            {
              // The final matching pattern is significant.
              "permission": { "skill": { "*": "deny", "internal-*": "allow", "internal-secret": "ask", }, },
            }
            """);
        Assert.Equal("observed", document.Evidence.State);
        Assert.Equal(new[] { "*", "internal-*", "internal-secret" }, document.Root!.Get("permission", "skill")!.FieldOrder);
    }

    [Theory]
    [InlineData("\uFEFF", "---", false)]
    [InlineData("", "...", false)]
    [InlineData("", "---", true)]
    public void ParsedYamlRetainsOriginalFramingForProviderAcceptance(string prefix, string delimiter, bool accepted)
    {
        var path = Path.Combine(_temporary.FullName, "SKILL.md");
        File.WriteAllText(path, prefix + "---\nname: demo\ndescription: Framing\n" + delimiter + "\n");
        var document = new LoadingConfigurationReader().Read(path, "skill", Token, frontMatter: true);
        Assert.Equal("observed", document.Evidence.State); Assert.NotNull(document.Root);
        Assert.Equal(accepted, document.HeaderFraming!.GeminiAcceptance);
        Assert.Equal(delimiter, document.HeaderFraming.ClosingDelimiter);
    }

    [Theory]
    [InlineData("config.json", "{\"enabled\": true, \"enabled\": false}")]
    [InlineData("config.toml", "enabled = true\nenabled = false\n")]
    [InlineData("config.yaml", "enabled: true\nenabled: false\n")]
    [InlineData("config.yaml", "a: &value true\nb: *value\n")]
    [InlineData("config.yaml", "a: !execute do-not-run\n")]
    [InlineData("config.yaml", "permission: !execute { skill: deny }\n")]
    [InlineData("config.yaml", "permission: !execute [deny]\n")]
    [InlineData("config.yaml", "permission: &named { skill: deny }\n")]
    [InlineData("config.yaml", "permission: &named [deny]\n")]
    [InlineData("config.yaml", "permission: &named deny\n")]
    [InlineData("config.json", "[false]")]
    public void AmbiguousOrUnsupportedConfigurationStaysUnknown(string name, string input)
    {
        var document = Read(name, input);
        Assert.Equal("invalid", document.Evidence.State);
        Assert.NotNull(document.Evidence.Sha256);
        Assert.Null(document.Root);
        Assert.NotNull(document.Evidence.Diagnostic);
    }

    [Theory]
    [InlineData("config.json", "{\"secret\": \"super-secret-credential\",BROKEN}")]
    [InlineData("config.toml", "secret = \"super-secret-credential\"\nBROKEN [")]
    [InlineData("config.yaml", "secret: super-secret-credential\nlist: [BROKEN")]
    public void ParserFailuresDoNotEchoConfigurationCredentials(string name, string input)
    {
        var document = Read(name, input);
        Assert.Equal("invalid", document.Evidence.State);
        Assert.DoesNotContain("super-secret-credential", document.Evidence.ToString());
        Assert.Null(document.Root);
    }

    [Fact]
    public void YamlNullNumbersAndExplicitTypesCannotMasqueradeAsStrings()
    {
        var document = Read("config.yaml", """
            absent: null
            empty:
            integer: 123
            exponent: 1.2e3
            hexadecimal: 0x123
            nan: .nan
            explicit: !!int "123"
            quoted: "null"
            typed: !!str false
            """);
        Assert.Equal("observed", document.Evidence.State);
        foreach (var key in new[] { "absent", "empty", "integer", "exponent", "hexadecimal", "nan", "explicit" })
            Assert.Equal("other", document.Root!.Get(key)!.Kind);
        Assert.Equal("string", document.Root!.Get("quoted")!.Kind);
        Assert.Equal("string", document.Root.Get("typed")!.Kind);
        Assert.Null(document.Root.Get("typed")!.Boolean);
    }

    [Fact]
    public async Task SpecialConfigurationCannotBlockCaptureIncludingThroughALink()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_temporary.FullName, "settings.json");
        var result = await new ProcessRunner(new Logger()).RunAsync("mkfifo", [path], cancellationToken: Token);
        Assert.True(result.Succeeded, result.StdErr);
        var link = Path.Combine(_temporary.FullName, "linked.json");
        File.CreateSymbolicLink(link, path);
        foreach (var candidate in new[] { path, link })
        {
            var document = await Task.Run(() => new LoadingConfigurationReader().Read(candidate, "fixture", Token), Token)
                .WaitAsync(TimeSpan.FromSeconds(2), Token);
            Assert.Equal("unavailable", document.Evidence.State);
            Assert.Null(document.Root);
        }
    }

    [Fact]
    public void RegularConfigurationLinksRemainReadable()
    {
        if (OperatingSystem.IsWindows()) return; // CI does not require symlink privilege.
        var target = Path.Combine(_temporary.FullName, "actual.json");
        File.WriteAllText(target, "{\"enabled\":false}");
        var link = Path.Combine(_temporary.FullName, "linked.json");
        File.CreateSymbolicLink(link, target);
        var document = new LoadingConfigurationReader().Read(link, "fixture", Token);
        Assert.Equal("observed", document.Evidence.State);
        Assert.Equal(link, document.Evidence.Path);
        Assert.False(document.Root!.Get("enabled")!.Boolean);
    }

    [Fact]
    public void StrictJsonAndCommentOnlyJsonDoNotInheritJsoncTrailingCommaSupport()
    {
        var path = Path.Combine(_temporary.FullName, "settings.json");
        File.WriteAllText(path, "{ /* comment */ \"enabled\": false }");
        Assert.Equal("invalid", new LoadingConfigurationReader().Read(path, "fixture", Token).Evidence.State);
        Assert.Equal("observed", new LoadingConfigurationReader().Read(path, "fixture", Token, LoadingJsonSyntax.Comments).Evidence.State);
        File.WriteAllText(path, "{\"enabled\":false,}");
        Assert.Equal("invalid", new LoadingConfigurationReader().Read(path, "fixture", Token, LoadingJsonSyntax.Comments).Evidence.State);
        Assert.Equal("observed", new LoadingConfigurationReader().Read(path, "fixture", Token, LoadingJsonSyntax.Jsonc).Evidence.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrontmatterReadsOnlyABoundedHeaderAndIgnoresLargeOrInvalidUtf8Bodies(bool bom)
    {
        var path = Path.Combine(_temporary.FullName, "SKILL.md");
        var header = System.Text.Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + "---\r\nname: demo\r\ndisable-model-invocation: true\r\n---\r\n");
        File.WriteAllBytes(path, [.. header, 0xFF, .. new byte[1024 * 1024]]);
        var reader = new LoadingConfigurationReader();
        var document = reader.Read(path, "skill header", Token, frontMatter: true);
        Assert.Equal("observed", document.Evidence.State);
        Assert.Equal("demo", document.Root!.Get("name")!.Text);
        Assert.True(document.Root.Get("disable-model-invocation")!.Boolean);
        var digest = document.Evidence.Sha256;
        File.WriteAllText(path, (bom ? "\uFEFF" : "") + "---\r\nname: demo\r\ndisable-model-invocation: true\r\n---\r\nchanged body");
        Assert.Equal(digest, new LoadingConfigurationReader().Read(path, "skill header", Token, frontMatter: true).Evidence.Sha256);
    }

    [Fact]
    public void HeaderDelimiterAtAReadBoundaryMustBeACompleteLine()
    {
        var path = Path.Combine(_temporary.FullName, "SKILL.md");
        // At byte 4096 the second YAML value has only supplied its first ---.
        var content = "---\nname: demo\n#" + new string('x', 4076) + "\n---not-a-delimiter\n";
        Assert.Equal(4093, content.IndexOf("---not", StringComparison.Ordinal));
        File.WriteAllText(path, content);
        Assert.Equal("invalid", new LoadingConfigurationReader().Read(path, "skill header", Token, frontMatter: true).Evidence.State);
        File.WriteAllText(path, "---\nname: demo\n---");
        Assert.Equal("observed", new LoadingConfigurationReader().Read(path, "skill header", Token, frontMatter: true).Evidence.State);
    }

    [Fact]
    public void MissingUnavailableAndBoundedFilesNeverBecomeEmptyObservedSettings()
    {
        var reader = new LoadingConfigurationReader();
        var missing = reader.Read(Path.Combine(_temporary.FullName, "absent.json"), "fixture", Token);
        Assert.Equal("missing", missing.Evidence.State); Assert.Null(missing.Root);
        var unavailable = reader.Read(_temporary.FullName, "fixture", Token);
        Assert.Equal("unavailable", unavailable.Evidence.State); Assert.Null(unavailable.Root);
        var path = Path.Combine(_temporary.FullName, "large.json");
        File.WriteAllBytes(path, new byte[LoadingConfigurationReader.MaxFileBytes + 1]);
        var bounded = reader.Read(path, "fixture", Token);
        Assert.Equal("bounded", bounded.Evidence.State); Assert.Null(bounded.Root);
        Assert.Equal(LoadingConfigurationReader.MaxFileBytes + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void CancellationAndFileCountBoundsCloseReadAdmission()
    {
        var reader = new LoadingConfigurationReader();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read("invalid\0address", "fixture", canceled.Token));
        for (var index = 0; index < LoadingConfigurationReader.MaxFiles; index++)
            Assert.Equal("missing", reader.Read(Path.Combine(_temporary.FullName, index + ".json"), "fixture", Token).Evidence.State);
        Assert.Equal("bounded", reader.Read("invalid\0address", "fixture", Token).Evidence.State);
    }

    [Fact]
    public void LoadingContextKeyIncludesProjectProfileVersionConfigurationTrustAndPolicy()
    {
        SkillLoadingContext Context(string project = "one", string? profile = null, string? version = "0.46.0",
            string configuration = "first", string trust = "trusted", string rule = "rules-one") =>
            SkillLoadingContext.Capture("gemini", profile, Path.Combine(_temporary.FullName, project), _temporary.FullName,
                _temporary.FullName, Path.Combine(_temporary.FullName, "config"), version, null, trust, configuration, rule);
        var contexts = new[] { Context(), Context(project: "two"), Context(profile: "alternate"), Context(version: "0.46.1"),
            Context(configuration: "second"), Context(trust: "unknown"), Context(rule: "rules-two") };
        Assert.Equal(contexts.Length, contexts.Select(value => value.ContextId).Distinct().Count());
        Assert.Equal(Context().ContextId, Context().ContextId);
        Assert.Equal("gemini-cli", Context().AgentId);
    }

    [Fact]
    public void PureFixtureVersionLabelsDoNotEstablishNativeConformance()
    {
        var rules = new AgentLoadingRuleSet("gemini-cli", "tagged-source-0.46.0", ["0.46.0"], "not-run", []);
        Assert.Equal("source-correlated", rules.Applicability("0.46.0"));
        Assert.Equal("client-version-unverified", rules.Applicability("0.46.1"));
        Assert.Equal("client-version-unknown", rules.Applicability(null));
        Assert.Equal("not-run", rules.NativeConformance);
        var rolling = rules with { SourceCorrelatedVersions = ImmutableArray<string>.Empty };
        Assert.Equal("client-version-unverified", rolling.Applicability("0.46.0"));
    }
}
