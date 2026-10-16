using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.PackageChecking;
using Xunit;

namespace SkillView.Tests.PackageChecking;

public sealed class StaticPackageCheckerTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-package-check-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Payload => Path.Combine(temporary.FullName, "package");
    private string SkillFile => Path.Combine(Payload, "SKILL.md");
    private const string Metadata = "---\nname: demo\ndescription: Static package fixture\n---\n";

    public StaticPackageCheckerTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(SkillFile, Metadata);
    }

    public void Dispose() => temporary.Delete(true);

    private InstalledSkill Target() => new()
    {
        Name = "demo",
        ResolvedPath = Payload,
        ScanRoot = temporary.FullName,
        Scope = Scope.Custom,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    private void Write(string path, string text)
    {
        var full = Path.Combine(Payload, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public async Task HealthyExplicitReferencesAreObservedWithoutExecutingOrChangingAnyPayload()
    {
        Write("SKILL.md", Metadata + "[Guide](references/guide.md) `scripts/check.py` ![image](assets/image.bin)\n");
        Write("references/guide.md", "[Main](../SKILL.md#part)\n");
        Write("scripts/check.py", "raise Exception('This must never run')\n");
        Write("assets/image.bin", "Exact local bytes");
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("valid", result.FormatStatus);
        Assert.Equal("none-observed", result.ReferenceStatus);
        Assert.Equal("complete", result.Coverage.Payload);
        Assert.Equal("limited", result.Coverage.References);
        Assert.Equal(4, result.Coverage.AnalyzedReferences);
        Assert.Empty(result.Findings); Assert.False(result.HasErrors);
        Assert.Equal("not-assessed", result.AgentCompatibility); Assert.Equal("not-assessed", result.SourceReview);
        Assert.Equal("none", result.Execution);
        var after = PayloadSnapshot.CaptureEvidence(Payload, Token);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, after.ObjectStateFingerprint);
    }

    [Fact]
    public async Task RequiredMetadataErrorsRemainSeparateFromMissingReferenceErrors()
    {
        Write("SKILL.md", "---\nname: demo\n---\n[Broken](references/missing.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("invalid", result.FormatStatus); Assert.Equal("findings", result.ReferenceStatus);
        Assert.Contains(result.Findings, finding => finding.Category == "metadata" && finding.Code == "metadata-required-description");
        Assert.Contains(result.Findings, finding => finding.Category == "reference" && finding.Code == "reference-missing"
            && finding.Target == "references/missing.md" && finding.Line == 4);
        Assert.Equal(2, result.FindingCount);
    }

    [Fact]
    public async Task MissingRootMetadataDoesNotPretendTheOtherFilesWereNotInspected()
    {
        File.Delete(SkillFile);
        Write("README.md", "[Missing](assets/missing.png)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("missing", result.FormatStatus); Assert.Equal("complete", result.Coverage.Payload);
        Assert.Contains(result.Findings, finding => finding.Code == "skill-file-missing");
        Assert.Contains(result.Findings, finding => finding.Code == "reference-missing");
    }

    [Theory]
    [InlineData("---\nname: [\n---\n", "metadata-framing-or-yaml")]
    [InlineData("No frontmatter\n", "metadata-framing-or-yaml")]
    public async Task MalformedMetadataDoesNotAbortWholePayloadObservation(string text, string code)
    {
        Write("SKILL.md", text);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("invalid", result.FormatStatus); Assert.Equal("complete", result.Coverage.Payload);
        Assert.Contains(result.Findings, finding => finding.Code == code);
    }

    [Fact]
    public async Task UnclosedFrontmatterCannotInventMarkdownReferencesButOtherDocumentsRemainCheckable()
    {
        Write("SKILL.md", "---\nname: demo\nnotes: '[not a proven body link](missing-in-yaml.md)'\n");
        Write("README.md", "[Actual link](references/actual-missing.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("invalid", result.FormatStatus); Assert.Equal("partial", result.Coverage.References);
        Assert.DoesNotContain(result.Findings, finding => finding.Target == "missing-in-yaml.md");
        Assert.Contains(result.Findings, finding => finding.Target == "references/actual-missing.md");
        Assert.Equal(1, result.Coverage.AnalyzedMarkdownFiles);
    }

    [Fact]
    public async Task InvalidYamlWithProvenClosingDelimiterStillHasASeparateMarkdownBody()
    {
        Write("SKILL.md", "---\nname: [\n---\n[Actual](references/missing.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Equal("invalid", result.FormatStatus); Assert.Equal("findings", result.ReferenceStatus);
        Assert.Contains(result.Findings, finding => finding.Target == "references/missing.md" && finding.Line == 4);
    }

    [Fact]
    public async Task CaseMismatchAndEncodedInternalNamesUseCapturedOrdinalPaths()
    {
        Write("SKILL.md", Metadata + "[Case](references/guide.md) [Encoded](assets/a%20b.png)\n");
        Write("references/Guide.md", "Guide\n"); Write("assets/a b.png", "bytes");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        var mismatch = Assert.Single(result.Findings);
        Assert.Equal("reference-case-mismatch", mismatch.Code);
        Assert.Equal("references/guide.md", mismatch.Target);
        Assert.Contains("references/Guide.md", mismatch.Message);
    }

    [Fact]
    public async Task LinkedContainersExternalPathsAndDynamicReferencesAreNotProbed()
    {
        var outside = Path.Combine(temporary.FullName, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "[Would be missing](outside-secret.txt)\n");
        Directory.CreateSymbolicLink(Path.Combine(Payload, "linked"), outside);
        Write("SKILL.md", Metadata + "[Linked](linked/secret.md) [Escape](../outside/secret.md) [External](https://example.invalid/no.md) [Dynamic](${ASSET}/no.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings);
        Assert.Equal("unknown", result.ReferenceStatus); Assert.Equal("partial", result.Coverage.References);
        Assert.Contains(result.Coverage.Reasons, reason => reason.Contains("links", StringComparison.Ordinal));
        Assert.Equal(1, result.Coverage.AnalyzedMarkdownFiles);
    }

    [Fact]
    public async Task CaseVariantLinkPrefixesRemainUnknownWithoutFollowingOrClaimingMissingTargets()
    {
        var outside = Path.Combine(temporary.FullName, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(Payload, "link"), outside);
        Write("SKILL.md", Metadata + "[Linked](LINK/secret.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Contains(result.Coverage.Reasons, reason => reason.Contains("links", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("scripts/pointer/../tool.py")]
    [InlineData("SCRIPTS/POINTER/../tool.py")]
    [InlineData("scripts/pointer/./../tool.py")]
    [InlineData("scripts/%70ointer/../tool.py")]
    [InlineData("scripts/pointer//../tool.py")]
    public async Task LinkTraversalCannotBeErasedByLexicalDotSegmentNormalization(string reference)
    {
        var outside = Path.Combine(temporary.FullName, "outside", "child"); Directory.CreateDirectory(outside);
        Write("scripts/tool.py", "This file does not establish where pointer/../tool.py resolves.");
        Directory.CreateSymbolicLink(Path.Combine(Payload, "scripts", "pointer"), outside);
        Write("SKILL.md", Metadata + $"[Unsafe normalization]({reference})\n[Known](scripts/tool.py)\n");
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token);
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Equal("partial", result.Coverage.References); Assert.Equal(2, result.Coverage.AnalyzedReferences);
        Assert.Contains(result.Coverage.Reasons, reason => reason.Contains("links", StringComparison.Ordinal));
        Assert.Equal(before.Fingerprint, PayloadSnapshot.CaptureEvidence(Payload, Token).Fingerprint);
    }

    [Fact]
    public async Task OrdinaryCapturedDirectoryDotSegmentsStillResolveWithoutUnknownCoverage()
    {
        Directory.CreateDirectory(Path.Combine(Payload, "scripts", "ordinary"));
        Write("scripts/tool.py", "Not executed");
        Write("SKILL.md", Metadata + "[Known](scripts/ordinary/../tool.py)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("none-observed", result.ReferenceStatus);
        Assert.Equal("limited", result.Coverage.References);
    }

    [Theory]
    [InlineData("assets/a&amp;b.png", "assets/a&b.png")]
    [InlineData("assets/&#97;.png", "assets/a.png")]
    [InlineData("assets/&#x61;.png", "assets/a.png")]
    public async Task HtmlCharacterReferencesCannotProduceFalseLiteralMissingFindings(string reference, string actual)
    {
        Write(actual, "Present under the rendered destination spelling.");
        Write("SKILL.md", Metadata + $"[Entity]({reference})\n[Known](SKILL.md)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Equal("partial", result.Coverage.References);
    }

    [Fact]
    public async Task BackslashEscapedAmpersandIsNotDecodedAsAnHtmlCharacterReference()
    {
        Write("assets/a&amp;b.png", "The ampersand is literal, not a second decoding opportunity.");
        Write("SKILL.md", Metadata + "[Literal](assets/a\\&amp;b.png)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("none-observed", result.ReferenceStatus);
        Assert.Equal("limited", result.Coverage.References);
    }

    [Fact]
    public async Task SecondHeldReadMustMatchTheFirstCapturedHashBeforeTextBecomesFrozenEvidence()
    {
        Write("SKILL.md", Metadata + "[Missing](a.md)\n");
        var replacement = Encoding.UTF8.GetBytes(Metadata + "[Missing](b.md)\n");
        var checker = new StaticPackageChecker(captureForTests: (path, sink, token) =>
            PayloadSnapshot.CaptureWithEntrySink(path, (item, entry) =>
            {
                if (entry.Path != "SKILL.md") { sink(item, entry); return; }
                using var changed = new MemoryStream(replacement, writable: false);
                Assert.Equal(entry.Bytes, replacement.Length);
                sink(item with { Content = changed }, entry);
            }, token));
        var result = await checker.CheckAsync(Target(), Token);
        Assert.Equal("valid", result.FormatStatus);
        Assert.Equal("unavailable", result.Coverage.Payload); Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Empty(result.Findings); Assert.Null(result.PayloadFingerprint);
        Assert.Contains(result.Coverage.Reasons, reason => reason.Contains("fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedFinalNativeVerificationDiscardsCollectedReferenceClaimsAndUsesRegularMetadataOnly()
    {
        Write("SKILL.md", Metadata + "[Missing](assets/missing.png)\n");
        var checker = new StaticPackageChecker(captureForTests: (path, sink, token) =>
        {
            _ = PayloadSnapshot.CaptureWithEntrySink(path, sink, token);
            throw new IOException("Injected final namespace verification failure");
        });
        var result = await checker.CheckAsync(Target(), Token);
        Assert.Equal("valid", result.FormatStatus);
        Assert.Equal("unavailable", result.Coverage.Payload); Assert.Equal("unknown", result.ReferenceStatus);
        Assert.Null(result.PayloadFingerprint); Assert.Empty(result.Findings); Assert.False(result.HasErrors);
        Assert.Equal(0, result.Coverage.AnalyzedReferences);
    }

    [Fact]
    public async Task RootMetadataFallbackDoesNotFollowAFinalSkillLink()
    {
        var outside = Path.Combine(temporary.FullName, "outside.md"); File.WriteAllText(outside, Metadata);
        File.Delete(SkillFile); File.CreateSymbolicLink(SkillFile, outside);
        var checker = new StaticPackageChecker(captureForTests: (_, _, _) => throw new IOException("Native walk unavailable"));
        var result = await checker.CheckAsync(Target(), Token);
        Assert.Equal("unknown", result.FormatStatus); Assert.Empty(result.Findings);
        Assert.Equal("unavailable", result.Coverage.Payload);
    }

    [Fact]
    public async Task MarkdownBufferBudgetsAreReservedBeforeReadingAndFindingsKeepExactOmissionCounts()
    {
        Write("SKILL.md", Metadata + "[a](a.md) [b](b.md) [c](c.md)\n");
        Write("huge.md", new string('x', 257));
        var limits = new StaticPackageChecker.Limits(DocumentBytes: 256, SkillBytes: 256, TextBytes: 512, Findings: 1);
        var result = await new StaticPackageChecker(limits).CheckAsync(Target(), Token);
        Assert.Equal("complete", result.Coverage.Payload); Assert.Equal("partial", result.Coverage.References);
        Assert.Equal(3, result.FindingCount); Assert.Equal(2, result.OmittedFindingCount);
        Assert.Single(result.Findings); Assert.Equal("findings", result.ReferenceStatus);
        Assert.Equal(1, result.Coverage.AnalyzedMarkdownFiles);
    }

    [Fact]
    public async Task CancellationStopsBeforeNativeCaptureAdmission()
    {
        var calls = 0;
        var checker = new StaticPackageChecker(captureForTests: (_, _, _) => { calls++; throw new IOException("Unexpected capture"); });
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(Target(), source.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MalformedUrlEscapesAreUnknownRatherThanInventedMissingFiles()
    {
        Write("SKILL.md", Metadata + "[Bad](assets/%ZZ.png)\n");
        var result = await new StaticPackageChecker().CheckAsync(Target(), Token);
        Assert.Empty(result.Findings); Assert.Equal("unknown", result.ReferenceStatus);
    }
}
