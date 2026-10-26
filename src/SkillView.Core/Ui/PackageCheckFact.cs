using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.PackageChecking;

namespace SkillView.Ui;

// One explicit observation of one displayed physical copy. Never mutation authority.
internal sealed record PackageCheckRequest(InventorySnapshot Snapshot, InstalledSkill Target, long Generation, string Context, string Id)
{
    // Compare frozen addresses only; rendering/filtering never normalize paths or hash.
    internal bool HasExactAddress => Snapshot.Skills.Count(s => s.ResolvedPath == Target.ResolvedPath
        || s.ObservedSubject?.InstallationId == Id) == 1
        && Snapshot.Skills.Any(s => ReferenceEquals(s, Target));

    internal bool HasExactSubject => HasExactAddress && Target.ObservedSubject is { NativeObject.Status: "observed", NativeObject.CapturedAt: not null } subject
        && subject.InstallationId == Id && subject.Path == Target.ResolvedPath;

    internal async Task<PackageCheckResult> CaptureAsync(CancellationToken token,
        Func<string, Action<SkillView.Inventory.NativePayloadItem, PayloadEntry>, CancellationToken, PayloadSnapshot>? capture = null)
    {
        PayloadSnapshot? final = null;
        var checker = new StaticPackageChecker(captureForTests: (path, sink, ct) =>
        {
            // Only the successful final verification may bind a report. Sink
            // callbacks and the checker's metadata fallback grant no binding.
            var snapshot = (capture ?? PayloadSnapshot.CaptureWithEntrySink)(path, sink, ct);
            final = snapshot;
            return snapshot;
        });
        var report = await checker.CheckAsync(Target, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var observed = Target.ObservedSubject?.NativeObject;
        var matches = HasExactSubject && final?.RootIdentity is { } identity && observed?.CapturedAt is { } at
            && NativeObjectObservation.From(identity, at, observed.Role) == observed
            && report.InstallationId == Id && report.Path == Target.ResolvedPath
            && report.PayloadFingerprint == final.Fingerprint;
        return new(report, matches ? new(this, report, DateTimeOffset.UtcNow) : null);
    }
}

internal sealed record PackageCheckResult(PackageCheckReport Report, PackageCheckFact? Fact);

internal sealed record PackageCheckFact(PackageCheckRequest Request, PackageCheckReport Report, DateTimeOffset CheckedAt)
{
    internal static string InventoryLifetimeNotice => "Check results are cleared when the skill list reloads. "
        + $"Opening Installed or Issues reloads lists at least {DisplayInventoryCache.ReuseWindow.TotalMinutes:0} minute old.";

    internal static bool IsProblem(PackageCheckFinding finding) => finding.Category == "reference"
        && finding.Code == "reference-missing" && finding.Path == "SKILL.md";
    internal int Problems => Report.Findings.Count(IsProblem);
    internal int Reviews => Report.Findings.Count(f => !IsProblem(f)) + Report.Observations.Count(o => o.State == "review");
    internal int Infos => Report.Observations.Count(o => o.State != "review");
    internal bool HasFindings => Problems > 0 || Reviews > 0;
    internal bool Incomplete => Report.Coverage.Payload != "complete" || Report.Coverage.References != "limited"
        || Report.Coverage.Reasons.Length > 0 || Report.DeclarationCoverage != "limited" || Report.DeclarationReasons.Length > 0
        || Report.OmittedFindingCount > 0 || Report.OmittedObservationCount > 0;
    internal bool Matches(InventorySnapshot snapshot, long generation, string id, string path) =>
        ReferenceEquals(Request.Snapshot, snapshot) && Request.Generation == generation
        && Request.Id == id && Request.Target.ResolvedPath == path;
    internal bool Matches(InventorySnapshot snapshot, long generation, InstalledSkill skill) =>
        ReferenceEquals(Request.Target, skill) && ReferenceEquals(Request.Snapshot, snapshot) && Request.Generation == generation;
    internal string Key => "package-check:" + InstalledFilters.Key(Request.Id, Request.Target.ResolvedPath);

    internal string Detail
    {
        get
        {
            var lines = new List<string> { $"## Last explicit package check\n\n{Problems} Problems · {Reviews} Reviews · {Infos} Info · {CheckedAt:O}",
                "Copy: " + Code(Request.Target.ResolvedPath),
                InventoryLifetimeNotice,
                "Scope: captured local package only; source integrity, availability and effective agent compatibility remain unknown.",
                Incomplete ? "Package checks incomplete; unchecked content remains unknown." : "No additional findings in the supported package checks." };
            foreach (var finding in Report.Findings.Take(8))
                lines.Add((IsProblem(finding) ? "Problem: Missing file referenced by " : "Review: ") + Code(finding.Path)
                    + (finding.Line is > 0 ? $" (line {finding.Line})" : "") + ": " + Code(finding.Target ?? finding.Code));
            foreach (var observation in Report.Observations.OrderBy(o => o.State == "review" ? 0 : 1).Take(4))
                lines.Add((observation.State == "review" ? "Review: " : "Info: ") + Code(observation.Path) + (observation.Line is > 0 ? $" (line {observation.Line})" : "")
                    + ": " + Code(observation.Value) + " (" + Code(observation.State) + ")");
            var hidden = Report.Findings.Length - Math.Min(8, Report.Findings.Length)
                + Report.Observations.Length - Math.Min(4, Report.Observations.Length);
            if (hidden > 0) lines.Add($"{hidden} retained findings omitted from this summary; K opens the full check.");
            if (Report.OmittedFindingCount > 0 || Report.OmittedObservationCount > 0)
                lines.Add($"Check limits omitted {Report.OmittedFindingCount} findings and {Report.OmittedObservationCount} observations.");
            lines.Add("Declarations retain their reported Info/Review and unknown meanings in the full report. **K:** check package again.");
            return string.Join("\n\n", lines);
        }
    }

    private static string Code(string value) => MarkdownTableFormatter.FormatCodeSpan(InstalledOverview.Text(value, 240));
}
