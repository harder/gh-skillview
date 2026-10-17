using System.Text;
using SkillView.Maintenance;
using SkillView.PackageChecking;
using Xunit;

namespace SkillView.Tests.PackageChecking;

public sealed partial class StaticPackageCheckerTests
{
    [Fact]
    public async Task FormatNativeCompatibilityPayloadAndSourceReviewRemainIndependent()
    {
        Write("SKILL.md", "\uFEFF" + Metadata + "[Missing](assets/absent.png)\n");
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var checker = new StaticPackageChecker(agent: new("gemini-cli", "0.46.0"));
        var broken = await checker.CheckAsync(Target(), Token);
        Assert.Equal("valid", broken.Format.State);
        Assert.Equal("incompatible", broken.AgentAssessment.State);
        Assert.Contains(broken.AgentAssessment.Sources, s => s.Revision == "v0.46.0" && s.Url.EndsWith("skillLoader.ts", StringComparison.Ordinal));
        Assert.Equal("unknown", broken.PayloadIntact.State);
        Assert.Equal("local-defects-observed", broken.PayloadIntact.ObservedCondition);
        Assert.Equal("not-reviewed", broken.SourceReviewed.State);
        Assert.Equal(before.Fingerprint, broken.PayloadFingerprint);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SkillFile))), broken.RootMetadataSha256);
        Assert.Equal(broken.RootMetadataSha256, broken.AgentAssessment.Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).ObjectStateFingerprint);

        Write("assets/absent.png", "A restored authored asset");
        var intact = await checker.CheckAsync(Target(), Token);
        Assert.Equal("unknown", intact.PayloadIntact.State);
        Assert.Equal("no-supported-reference-defects-observed", intact.PayloadIntact.ObservedCondition);
        Assert.Contains(intact.PayloadIntact.Reasons, r => r.Contains("expected payload baseline", StringComparison.Ordinal));
        Assert.Equal("valid", intact.Format.State);
        Assert.Equal("incompatible", intact.AgentAssessment.State);
        Assert.Equal("not-reviewed", intact.SourceReviewed.State);

        Write("SKILL.md", "---\nname: demo\n---\n[Present](assets/absent.png)\n");
        var invalid = await checker.CheckAsync(Target(), Token);
        Assert.Equal("invalid", invalid.Format.State);
        Assert.Equal("unknown", invalid.PayloadIntact.State);
        Assert.Equal("no-supported-reference-defects-observed", invalid.PayloadIntact.ObservedCondition);
        Assert.Equal("unknown", invalid.AgentAssessment.State);
        Assert.Equal("not-reviewed", invalid.SourceReviewed.State);
    }

    [Theory]
    [InlineData("gemini-cli", null, "client-version-unknown")]
    [InlineData("gemini-cli", "999.0", "client-version-unverified")]
    [InlineData("unmodeled-agent", "1.0", "unsupported-agent")]
    [InlineData("claude", "1.0", "client-version-unverified")]
    public async Task UnsupportedAgentOrVersionCannotBecomeCompatible(string agent, string? version, string reason)
    {
        var result = await new StaticPackageChecker(agent: new(agent, version)).CheckAsync(Target(), Token);
        Assert.Equal("unknown", result.AgentAssessment.State);
        Assert.Contains(result.AgentAssessment.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
        Assert.Equal("unknown", result.PayloadIntact.State);
        Assert.Equal("no-supported-reference-defects-observed", result.PayloadIntact.ObservedCondition);
        Assert.Equal("not-reviewed", result.SourceReviewed.State);
    }

    [Fact]
    public async Task GeminiDelimiterEvidenceRemainsSeparateFromParsedYamlAndLimitedRuleSuccess()
    {
        Write("SKILL.md", Metadata.Replace("\n---\n", "\n...\n", StringComparison.Ordinal));
        var checker = new StaticPackageChecker(agent: new("gemini", "0.46.0"));
        var rejected = await checker.CheckAsync(Target(), Token);
        Assert.Equal("valid", rejected.Format.State);
        Assert.Equal("incompatible", rejected.AgentAssessment.State);
        Write("SKILL.md", Metadata);
        var acceptedFraming = await checker.CheckAsync(Target(), Token);
        Assert.Equal("unknown", acceptedFraming.AgentAssessment.State);
        Assert.Contains(acceptedFraming.AgentAssessment.Reasons, r => r.Contains("framing rule passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InternalFragmentsUseCapturedHeadingsIncludingDuplicateIdsAndCaseFindings()
    {
        Write("SKILL.md", Metadata + "# Home\n[Good](references/guide.md#install-1) [Case](#Home) [Broken](references/guide.md#absent)\n");
        Write("references/guide.md", "# Install\n# Install\n```md\n# Absent\n```\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal(2, result.Findings.Length);
        Assert.Contains(result.Findings, f => f.Code == "reference-anchor-case-mismatch" && f.Target == "#Home");
        Assert.Contains(result.Findings, f => f.Code == "reference-anchor-missing" && f.Target == "references/guide.md#absent");
        Assert.DoesNotContain(result.Findings, f => f.Target == "references/guide.md#install-1");
    }

    [Theory]
    [InlineData("# *Rich* Heading\n")]
    [InlineData("# Résumé\n")]
    [InlineData("Heading\n=======\n")]
    [InlineData("<a id=\"custom\"></a>\n")]
    [InlineData("- # Custom\n")]
    [InlineData("1. # Custom\n")]
    [InlineData("Text <span id=\"custom\">custom anchor</span>\n")]
    public async Task UnsupportedAnchorGrammarCannotProveAMissingHeading(string body)
    {
        Write("SKILL.md", Metadata + "[Renderer specific](references/guide.md#custom)\n");
        Write("references/guide.md", body);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Equal("unknown", result.PayloadIntact.State);
        Assert.DoesNotContain(result.Findings, f => f.Code == "reference-anchor-missing");
    }

    [Fact]
    public async Task LocalDependenciesHaveConcreteRepairWhileExternalDeclarationsRemainUnknown()
    {
        Write("package.json", "{\"dependencies\":{\"remote\":\"^2.0\",\"local\":\"file:vendor/missing\"}}\n");
        Write("requirements.txt", "requests[security]>=2.0,<3\n-r missing-requirements.txt\n");
        Write("scripts/check.py", "raise Exception('sentinel: skill code must not execute')\n");
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        var repairs = result.Findings.Where(f => f.Code == "dependency-file-missing").ToArray();
        Assert.Equal(2, repairs.Length);
        Assert.Contains(repairs, f => f.Path == "package.json" && f.Line is null && f.Repair.Contains("vendor/missing", StringComparison.Ordinal));
        Assert.Contains(repairs, f => f.Path == "requirements.txt" && f.Line == 2 && f.Repair.Contains("missing-requirements.txt", StringComparison.Ordinal));
        Assert.Contains(result.Observations, o => o.Value == "remote@^2.0" && o.State == "availability-unknown" && o.Repair.Contains("selected Node environment", StringComparison.Ordinal));
        Assert.Contains(result.Observations, o => o.Kind == "python-requirement" && o.State == "availability-unknown" && o.Repair.Contains("python -m pip", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Observations, o => o.State is "installed" or "missing");
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).ObjectStateFingerprint);
        Assert.Equal("none", result.Execution);
    }

    [Theory]
    [InlineData("{\"dependencies\":[]}")]
    [InlineData("{\"dependencies\":{\"remote\":1}}")]
    [InlineData("{\"dependencies\":{\"remote\":\"2\",\"remote\":\"3\"}}")]
    [InlineData("{\"dependencies\":{\"local\":\"file:missing\"},\"dependencies\":{}}")]
    public async Task InvalidDependencyManifestsDiscardPartialInterpretations(string manifest)
    {
        Write("package.json", manifest);
        Write("requirements.txt", "-e ../outside\nrequests [unclosed\n");
        Write("pyproject.toml", "[project]\ndependencies = [\"not-interpreted\"]\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Equal("unknown", result.PayloadIntact.State);
        Assert.Equal("no-supported-reference-defects-observed", result.PayloadIntact.ObservedCondition);
        Assert.Equal("valid", result.Format.State);
        Assert.Contains(result.Observations, o => o.Kind == "unsupported-manifest" && o.State == "unknown");
        Assert.Contains(result.Observations, o => o.Kind == "unsupported-requirement");
        Assert.Contains(result.Observations, o => o.Path == "pyproject.toml" && o.State == "unknown");
        Assert.DoesNotContain(result.Observations, o => o.Kind == "dependencies");
        Assert.DoesNotContain(result.Findings, f => f.Code == "dependency-file-missing");
    }

    [Theory]
    [InlineData("allowed-tools: [Bash, 17]")]
    [InlineData("agents: {name: gemini-cli}")]
    [InlineData("compatibility: false")]
    [InlineData("dependencies: {}")]
    public async Task UnsupportedAuthoredDeclarationShapeCannotBecomeValidPolicy(string declaration)
    {
        Write("SKILL.md", Metadata.Replace("description: Static package fixture", "description: Static package fixture\n" + declaration, StringComparison.Ordinal));
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("valid", result.Format.State);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains("unsupported syntax", StringComparison.Ordinal));
        Assert.Equal("unknown", result.AgentAssessment.State);
        Assert.DoesNotContain(result.Observations, o => o.State is "allowed" or "compatible" or "installed");
    }

    [Fact]
    public async Task PortabilityAndLiteralDeclarationsAreReviewEvidenceNotRuntimeClaims()
    {
        Write("SKILL.md", Metadata.Replace("description: Static package fixture", "description: Static package fixture\ncompatibility: Python 3.12 and credentials\nallowed-tools: Bash(python *)", StringComparison.Ordinal)
            + "[Platform](/opt/site/file.md)\n[Docs](https://example.invalid/api)\n");
        Write("scripts/check.sh", "#!/bin/bash\r\nprintf 'https://example.invalid/example'\r\n");
        if (!OperatingSystem.IsWindows()) Write("assets/CON.txt", "A Windows reserved basename");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Contains(result.Observations, o => o.Kind == "nonportable-reference" && o.State == "review");
        Assert.Contains(result.Observations, o => o.Kind == "script-interpreter" && o.Value == "/bin/bash");
        Assert.Contains(result.Observations, o => o.Kind == "shebang-crlf");
        if (!OperatingSystem.IsWindows()) Assert.Contains(result.Observations, o => o.Kind == "windows-path");
        Assert.Contains(result.Observations, o => o.Kind == "authored-compatibility" && o.State == "availability-unknown");
        Assert.Contains(result.Observations, o => o.Kind == "allowed-tools" && o.State == "text-only");
        Assert.Contains(result.Observations, o => o.Kind == "literal-url" && o.Value == "https://example.invalid/api" && o.State == "text-only");
        Assert.DoesNotContain(result.Observations, o => o.State is "allowed" or "contacted" or "safe");
    }

    [Fact]
    public async Task NonregularAndBoundedDeclarationInputsCannotBecomeEmptyDefaultRequirements()
    {
        var outside = Path.Combine(temporary.FullName, "external.json");
        File.WriteAllText(outside, "{\"dependencies\":{\"secret\":\"file:missing\"}}");
        File.CreateSymbolicLink(Path.Combine(Payload, "package.json"), outside);
        Write("requirements.txt", new string('x', 257));
        var checker = new StaticPackageChecker(new(DocumentBytes: 256, SkillBytes: 256, TextBytes: 512));
        var result = await checker.CheckAsync(Target(), Token);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains("not a regular file", StringComparison.Ordinal));
        Assert.Contains(result.DeclarationReasons, r => r.Contains("limit", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Observations, o => o.Value.Contains("secret", StringComparison.Ordinal));
        Assert.Empty(result.Findings);
        File.Delete(Path.Combine(Payload, "package.json"));
        File.WriteAllBytes(Path.Combine(Payload, "requirements.txt"), [0xff, 0xfe]);
        var encoding = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("partial", encoding.DeclarationCoverage);
        Assert.Contains(encoding.DeclarationReasons, r => r.Contains("UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObservationOmissionsAreCountedWithoutHidingIncompleteCoverage()
    {
        Write("requirements.txt", "one==1\ntwo==2\nthree==3\n");
        var result = await new StaticPackageChecker(new(Findings: 1)).CheckAsync(Target(), Token);
        Assert.Single(result.Observations);
        Assert.Equal(3, result.ObservationCount);
        Assert.Equal(2, result.OmittedObservationCount);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Equal("unknown", result.PayloadIntact.State);
        Assert.Equal("no-supported-reference-defects-observed", result.PayloadIntact.ObservedCondition);
    }

    [Fact]
    public async Task OmittedReferenceFindingCannotBeHiddenByAnEarlierMetadataError()
    {
        Write("SKILL.md", "---\nname: demo\n---\n[Missing](missing.md)\n");
        var result = await new StaticPackageChecker(new(Findings: 1)).CheckAsync(Target(), Token);
        Assert.Equal("metadata", Assert.Single(result.Findings).Category);
        Assert.Equal(1, result.OmittedFindingCount);
        Assert.Equal("invalid", result.Format.State);
        Assert.Equal("unknown", result.PayloadIntact.State);
        Assert.Equal("local-defects-observed", result.PayloadIntact.ObservedCondition);
    }

    [Theory]
    [InlineData("file:~/shared-lib")]
    [InlineData("file:~other/shared-lib")]
    [InlineData("link:~/shared-lib")]
    public async Task HomeRelativeDependenciesRemainUnknownWithoutPackageRelativeAbsenceClaims(string address)
    {
        Write("package.json", System.Text.Json.JsonSerializer.Serialize(new { dependencies = new { shared = address } }));
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains("home-relative", StringComparison.Ordinal));
        Assert.Contains(result.Observations, o => o.Kind == "home-relative-path" && o.Value == address && o.State == "unknown"
            && o.Repair.Contains("did not expand HOME", StringComparison.Ordinal));
        Assert.Equal("none-observed", result.ReferenceStatus);
        Assert.Equal("no-supported-reference-defects-observed", result.PayloadIntact.ObservedCondition);
        Assert.Empty(result.Findings);
        Assert.False(result.HasErrors);
        Assert.Equal(SkillView.Bootstrapping.ExitCodes.UserError, SkillView.Cli.CliDispatcher.PackageCheckExitCode(result));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).ObjectStateFingerprint);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("SKILL.md")]
    public async Task OversizedSharedMarkdownMakesReferenceAndDeclarationCoveragePartial(string path)
    {
        Write(path, "https://example.invalid/unobserved\n" + new string('x', 257));
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker(new(DocumentBytes: 256, SkillBytes: 256, TextBytes: 512)).CheckAsync(Target(), Token);
        Assert.Equal("complete", result.Coverage.Payload);
        Assert.Equal("partial", result.Coverage.References);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains("limit omitted", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Observations, o => o.Value.Contains("unobserved", StringComparison.Ordinal));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("SKILL.md")]
    public async Task InvalidUtf8SharedMarkdownCannotSupplyPartialDeclarationText(string path)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes("https://example.invalid/unobserved\n");
        File.WriteAllBytes(Path.Combine(Payload, path), [.. prefix, 0xff]);
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("complete", result.Coverage.Payload);
        Assert.Equal("partial", result.Coverage.References);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains("UTF-8", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Observations, o => o.Value.Contains("unobserved", StringComparison.Ordinal));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("SKILL.md")]
    public async Task LinkedSharedMarkdownIsNotReadAndKeepsBothCoverageDimensionsPartial(string path)
    {
        var external = Path.Combine(temporary.FullName, "external-markdown.md");
        File.WriteAllText(external, Metadata + "https://example.invalid/external-unobserved\n");
        var selected = Path.Combine(Payload, path);
        if (File.Exists(selected)) File.Delete(selected);
        File.CreateSymbolicLink(selected, external);
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("complete", result.Coverage.Payload);
        Assert.Equal("partial", result.Coverage.References);
        Assert.Equal("partial", result.DeclarationCoverage);
        Assert.Contains(result.DeclarationReasons, r => r.Contains(path, StringComparison.Ordinal) && r.Contains("not a regular file", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Observations, o => o.Value.Contains("external-unobserved", StringComparison.Ordinal));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).ObjectStateFingerprint);
    }
}
