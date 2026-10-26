using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.PackageChecking;
using SkillView.Tests.Cli;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class PackageCheckFactTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-package-fact-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => _root.Delete(true);

    [Theory]
    [InlineData("reference", "reference-missing", "SKILL.md", true)]
    [InlineData("Reference", "reference-missing", "SKILL.md", false)]
    [InlineData("reference", "reference-missing", "skill.md", false)]
    [InlineData("reference", "reference-missing", "references/other.md", false)]
    [InlineData("reference", "dependency-file-missing", "SKILL.md", false)]
    [InlineData("reference", "reference-case-mismatch", "SKILL.md", false)]
    [InlineData("metadata", "skill-file-missing", "SKILL.md", false)]
    public void OnlyStructuredOrdinalRootMissingReferenceIsProblem(string category, string code, string path, bool problem)
    {
        var finding = new PackageCheckFinding(category, code, path, 56, "references/maintenance.md", "misleading Problem message", "repair");
        Assert.Equal(problem, PackageCheckFact.IsProblem(finding));
        var fact = Fact(Report() with { Findings = [finding], FindingCount = 1, HasErrors = false });
        Assert.Equal(problem ? 1 : 0, fact.Problems);
        Assert.Equal(problem ? 0 : 1, fact.Reviews);
        Assert.Contains("line 56", fact.Detail);
        Assert.Contains("references/maintenance.md", fact.Detail);
        Assert.DoesNotContain("misleading", fact.Detail);
    }

    [Theory]
    [InlineData("problem")]
    [InlineData("review")]
    [InlineData("partial")]
    [InlineData("omitted-finding")]
    [InlineData("omitted-observation")]
    [InlineData("declaration-review")]
    public void PackageEvidenceComposesWithQueueOnlyForTheExactCopy(string state)
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo");
        var sibling = skill with { ResolvedPath = "/other/demo" };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill, sibling] };
        var report = Report();
        report = state switch
        {
            "problem" => report with { Findings = [new("reference", "reference-missing", "SKILL.md", 1, "a", "m", "r")], FindingCount = 1 },
            "review" => report with { Findings = [new("metadata", "invalid", "SKILL.md", 1, null, "m", "r")], FindingCount = 1 },
            "partial" => report with { Coverage = report.Coverage with { References = "partial" } },
            "omitted-finding" => report with { OmittedFindingCount = 1, FindingCount = 1 },
            "omitted-observation" => report with { ObservationCount = 1 },
            _ => report with { Observations = [new("portability", "windows-path", "other.md", 3, "bad", "review", "review")], ObservationCount = 1 }
        };
        var fact = new PackageCheckFact(new(snapshot, skill, 1, "context", skill.InstallationId), report, DateTimeOffset.UtcNow);
        var filters = InstalledOverview.Build(snapshot, Token).Filters!;
        var queue = InstalledIssueEvidence.Capture(snapshot, 1, new(snapshot.CapturedAt, false, [], []));
        Assert.False(filters.Matches(skill, new(Issue: "none-observed"), null, queue, 1, fact));
        Assert.True(filters.Matches(sibling, new(Issue: "none-observed"), null, queue, 1, fact));
        Assert.Equal(fact.HasFindings, filters.Matches(skill, new(Issue: "any-finding"), null, null, 1, fact));
        Assert.False(filters.Matches(sibling, new(Issue: "any-finding"), null, null, 1, fact));
        Assert.False(filters.Matches(skill, new(Issue: "none-observed"), null, null, 1, fact));
        Assert.True(filters.Matches(skill, new(Issue: "none-observed"), null, queue, 1, fact with { Request = fact.Request with { Snapshot = snapshot with { } } }));
        Assert.True(filters.Matches(skill, new(Issue: "none-observed"), null, queue, 1, fact with { Request = fact.Request with { Generation = 0 } }));
        Assert.Empty(queue.Findings);
    }

    [Fact]
    public async Task FinalNativeCaptureBindsOnceWithoutInventoryAndRejectsReplacementUnknownAndFallback()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "SKILL.md"), "---\nname: demo\ndescription: inert fixture\n---\n[missing](references/missing.md)\n");
        var skill = InstalledOverviewTests.Copy(_root.FullName, "demo");
        var before = PayloadSnapshot.CaptureEvidence(_root.FullName, Token);
        var native = NativeObjectObservation.From(before.RootIdentity!.Value, DateTimeOffset.UtcNow, "inventory-observation");
        skill = skill with { ObservedSubject = OperationSubject.From(skill) with { NativeObject = native } };
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var request = new PackageCheckRequest(snapshot, skill, 1, "context", skill.InstallationId);
        var captures = 0;
        var result = await request.CaptureAsync(Token, (path, sink, token) =>
        {
            captures++;
            return PayloadSnapshot.CaptureWithEntrySink(path, sink, token);
        });
        Assert.Equal(1, captures);
        var fact = Assert.IsType<PackageCheckFact>(result.Fact);
        Assert.Equal(1, fact.Problems);
        Assert.Equal(before.Fingerprint, fact.Report.PayloadFingerprint);
        Assert.Equal(native, fact.Request.Target.ObservedSubject!.NativeObject);
        Assert.Null((await (request with { Snapshot = snapshot with { Skills = [skill, skill] } }).CaptureAsync(Token)).Fact);
        var unknown = skill with { ObservedSubject = skill.ObservedSubject with { NativeObject = NativeObjectObservation.Unknown("not observed") } };
        Assert.Null((await new PackageCheckRequest(snapshot with { Skills = [unknown] }, unknown, 1, "context", unknown.InstallationId).CaptureAsync(Token)).Fact);
        foreach (var changed in new[]
        {
            native with { Volume = "different-volume" }, native with { ObjectId = "different-full-id" },
            native with { CanonicalPath = "different-address" }, native with { ChangeTimeSeconds = "different-generation" },
            native with { ChangeTimeNanoseconds = "different-generation" }, native with { WindowsCreationTime = "different-creation" },
            native with { WindowsChangeTime = "different-generation" }, native with { Platform = "different-platform" }
        })
        {
            var changedTarget = skill with { ObservedSubject = skill.ObservedSubject with { NativeObject = changed } };
            Assert.Null((await new PackageCheckRequest(snapshot with { Skills = [changedTarget] }, changedTarget, 1, "context", changedTarget.InstallationId).CaptureAsync(Token)).Fact);
        }
        var fallback = await request.CaptureAsync(Token, (path, sink, token) =>
        {
            _ = PayloadSnapshot.CaptureWithEntrySink(path, sink, token);
            throw new IOException("final verification failed after entry callbacks");
        });
        Assert.Null(fallback.Fact);
        Assert.Equal("unavailable", fallback.Report.Coverage.Payload);
        Assert.NotNull(fallback.Report.RootMetadataSha256);
        Assert.DoesNotContain(fallback.Report.Findings, PackageCheckFact.IsProblem);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledCaptures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.CaptureAsync(canceled.Token, (path, sink, token) =>
        {
            canceledCaptures++;
            return PayloadSnapshot.CaptureWithEntrySink(path, sink, token);
        }));
        Assert.Equal(0, canceledCaptures);
        var old = _root.FullName + "-original";
        Directory.Move(_root.FullName, old);
        try
        {
            Directory.CreateDirectory(_root.FullName);
            File.Copy(Path.Combine(old, "SKILL.md"), Path.Combine(_root.FullName, "SKILL.md"));
            var replacement = await request.CaptureAsync(Token);
            Assert.Equal(before.Fingerprint, replacement.Report.PayloadFingerprint);
            Assert.Null(replacement.Fact);
        }
        finally { Directory.Delete(_root.FullName, true); Directory.Move(old, _root.FullName); }
    }

    [Fact]
    public void SummaryBoundsAndSanitizesRawValuesAndAccountsForOmissions()
    {
        var report = Report() with
        {
            Findings = [.. Enumerable.Range(0, 12).Select(_ => new PackageCheckFinding("reference", "reference-missing", "SKILL.md", 56,
            "references/\u001b[31m" + new string('x', 5000), "ignored", "ignored"))],
            FindingCount = 15,
            OmittedFindingCount = 3
        };
        var fact = Fact(report);
        Assert.Equal(12, fact.Problems);
        Assert.True(fact.Incomplete);
        Assert.DoesNotContain('\u001b', fact.Detail);
        Assert.InRange(fact.Detail.Length, 1, 5000);
        Assert.Contains("4 retained findings omitted", fact.Detail);
        Assert.Contains("3 findings", fact.Detail);
    }

    internal static PackageCheckReport Report() => PackageCheckCliTests.Report() with { DeclarationCoverage = "limited" };
    internal static PackageCheckFact Fact(PackageCheckReport report)
    {
        var skill = InstalledOverviewTests.Copy(report.Path, "demo");
        return new(new(InventorySnapshot.Empty with { Skills = [skill] }, skill, 1, "context", skill.InstallationId), report, DateTimeOffset.UtcNow);
    }
}
