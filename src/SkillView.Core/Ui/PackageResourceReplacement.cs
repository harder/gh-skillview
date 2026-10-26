using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.PackageChecking;

namespace SkillView.Ui;

// Read-only eligibility and comparison of already captured evidence. The saved
// update planner remains the only authority to admit a replacement.
internal static partial class PackageResourceReplacement
{
    internal const string Restored = "This resource was restored; other checks remain incomplete.";
    internal const string Incomplete = "Replacement installed; package verification incomplete.";
    internal const string Disclosure = "Whole-package replacement; the original, including local edits, is retained for Undo. Review update follows latest and may change the revision; Choose version requests one Git tag.";

    [GeneratedRegex(@"\Areferences/(?:[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*/)*[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();

    internal static bool EligibleFinding(PackageCheckFinding finding) => PackageCheckFact.IsProblem(finding)
        && finding.Target is { Length: <= 4096 } target && TargetPattern().IsMatch(target);

    internal static string? Target(PackageCheckFact fact)
    {
        var request = fact.Request;
        var report = fact.Report;
        if (!request.HasExactSubject || report.InstallationId != request.Id || report.Path != request.Target.ResolvedPath
            || report.Coverage.Payload != "complete" || report.PayloadFingerprint is null
            || report.FormatStatus != "valid" || report.RootMetadataSha256 is null || report.OmittedFindingCount != 0
            || !Complete(request.Snapshot) || !TrackedUnpinned(request.Target)) return null;
        // Global parser/declaration uncertainty is intentionally not a gate.
        var targets = report.Findings.Where(EligibleFinding).Select(f => f.Target!).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return targets.Length == 1 ? targets[0] : null;
    }

    internal static bool Complete(InventorySnapshot snapshot) => !snapshot.IsPartial && snapshot.ConfiguredRootsComplete
        && snapshot.OwnershipReadDiagnostics.IsEmpty && snapshot.OwnershipRecords.All(o => o.Diagnostic is null);

    internal static bool TrackedUnpinned(InstalledSkill skill)
    {
        var metadata = skill.FrontMatter;
        if (skill.Validity != ValidityState.Valid || !skill.MetadataConflicts.IsEmpty || !skill.OwnershipDiagnostics.IsEmpty
            || !metadata.Diagnostics.IsEmpty || metadata.LocalPath is not null || PinDisplayPolicy.From(skill).State != PinDisplayState.Unpinned
            || metadata.GithubTreeSha is not { Length: 40 } tree || !tree.All(char.IsAsciiHexDigit)
            || metadata.GithubRef is not { } reference || !(reference.StartsWith("refs/tags/", StringComparison.Ordinal)
                || reference.StartsWith("refs/heads/", StringComparison.Ordinal)) || metadata.GithubPath is null) return false;
        try
        {
            GhRevisionResolver.Repository(metadata.Upstream ?? "");
            GhRevisionResolver.ValidateTag(reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? reference[10..] : reference[11..]);
        }
        catch (IOException) { return false; }
        var path = SkillSourceIdentity.NormalizeSubpath(metadata.GithubPath);
        if (path is null || path.Length > 4096 || path.Split('/').Length > 16
            || path.Length > 0 && path.Split('/').Any(p => p is "" or "." or ".." or ".git" || p.Contains('\\') || p.Contains(':') || p.Any(char.IsControl))) return false;
        var owners = skill.AllOwnershipClaims.ToArray();
        if (!owners.All(o => o.Provider == "gh-vercel-global" && !o.ReadOnly && !o.IsExclusiveOwner && o.ManagedRegion is null
                && o.SchemaVersion == 3 && o.Diagnostic is null && o.LockFilePath is not null && o.Selector is not null && o.EntryFingerprint is not null)
            || owners.Select(o => (o.LockFilePath, o.Selector, o.EntryFingerprint)).Distinct().Take(2).Count() > 1) return false;
        try
        {
            foreach (var owner in owners)
                GhSharedLockGuard.ValidateUpdateRecords(ImmutableDictionary<string, GhSharedLockGuard.Record>.Empty.Add(owner.Selector!,
                    new(owner.SourceUrl, owner.Source, owner.SourceType, owner.SkillPath, owner.ContentHash, owner.PinnedRef)), [skill]);
            return true;
        }
        catch (IOException) { return false; }
    }

    internal static bool SameNative(NativeObjectObservation? before, NativeObjectObservation? after) =>
        before is { Status: "observed", CapturedAt: not null } && after is { Status: "observed", CapturedAt: not null }
        && (before with { CapturedAt = null, Role = "" }) == (after with { CapturedAt = null, Role = "" });

    internal static bool SameSource(SkillFrontMatter before, SkillFrontMatter after) => before.Upstream == after.Upstream
        && before.GithubPath == after.GithubPath && before.GithubRef == after.GithubRef && before.GithubTreeSha == after.GithubTreeSha
        && before.Pinned == after.Pinned && before.PinnedRef == after.PinnedRef && before.LocalPath == after.LocalPath;

    internal static bool FreshCopy(PackageCheckFact fact, InventorySnapshot fresh, InstalledSkill copy) => Complete(fresh)
        && TrackedUnpinned(copy) && copy.InstallationId == fact.Request.Id && copy.ResolvedPath == fact.Request.Target.ResolvedPath
        && SameSource(fact.Request.Target.FrontMatter, copy.FrontMatter)
        && SameNative(fact.Request.Target.ObservedSubject?.NativeObject, copy.ObservedSubject?.NativeObject);

    internal static bool Ready(PackageCheckFact fact, string target, ReplacementResult plan) => Target(fact) == target
        && SavedUpdateCoordinator.IsRevisionUpdate(plan) && plan.Succeeded && !plan.Canceled && !plan.MutationObserved
        && plan.Subject.InstallationId == fact.Request.Id && plan.OriginalPath == fact.Request.Target.ResolvedPath
        && SameSource(fact.Request.Target.FrontMatter, plan.Subject.Metadata)
        && SameNative(fact.Request.Target.ObservedSubject?.NativeObject, plan.Impact?.Subject.NativeObject)
        && plan.Comparison is { } comparison && comparison.InstalledPayload == fact.Report.PayloadFingerprint
        && comparison.Paths.Count(p => p.Path == target && p.Installed is null && p.Candidate?.Kind == "file") == 1;

    internal static async Task<string> CheckAppliedAsync(InstalledSkill frozen, string target, ReplacementResult applied,
        CancellationToken token, Func<string, Action<SkillView.Inventory.NativePayloadItem, PayloadEntry>, CancellationToken, PayloadSnapshot>? capture = null)
    {
        if (applied.OriginalPath != frozen.ResolvedPath || applied.Subject.InstallationId != frozen.InstallationId) return Incomplete;
        PayloadSnapshot? final = null;
        var checker = new StaticPackageChecker(captureForTests: (path, sink, ct) =>
        {
            var snapshot = (capture ?? PayloadSnapshot.CaptureWithEntrySink)(path, sink, ct);
            final = snapshot; // only the finally verified complete capture
            return snapshot;
        });
        // The old native root is intentionally not compared: native apply moved
        // the candidate here. Never check a sibling, a name match, or staging.
        var report = await checker.CheckAsync(frozen, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var restored = Verified(report, final, target, applied);
        var problems = report.Findings.Count(PackageCheckFact.IsProblem);
        var reviews = report.Findings.Length - problems + report.Observations.Count(o => o.State == "review");
        var details = new List<string> { restored ? Restored : Incomplete,
            $"{problems} Problems · {reviews} Reviews. Package checks remain incomplete; unchecked content remains unknown." };
        foreach (var reason in report.Coverage.Reasons.Concat(report.DeclarationReasons)) details.Add(Code(reason));
        foreach (var finding in report.Findings.Take(16)) details.Add((PackageCheckFact.IsProblem(finding) ? "Problem: " : "Review: ")
            + Code(finding.Path) + ": " + Code(finding.Target ?? finding.Code));
        foreach (var observation in report.Observations.Where(o => o.State == "review").Take(8))
            details.Add("Review: " + Code(observation.Path) + ": " + Code(observation.Value));
        details.Add($"Check retained {report.Findings.Length} findings and {report.Observations.Length} observations; omitted {report.OmittedFindingCount} findings and {report.OmittedObservationCount} observations. K checks this copy again after Refresh.");
        return string.Join("\n\n", details);
    }

    private static string Code(string value) => MarkdownTableFormatter.FormatCodeSpan(InstalledOverview.Text(value, 240));

    internal static bool Verified(PackageCheckReport report, PayloadSnapshot? final, string target, ReplacementResult applied) =>
        report.Path == applied.OriginalPath && report.InstallationId == applied.Subject.InstallationId
        && report.Coverage.Payload == "complete" && report.OmittedFindingCount == 0 && final is not null
        && final.Path == applied.OriginalPath && final.RootIdentity?.CanonicalPath == applied.OriginalPath
        && applied.Comparison is { } comparison && report.PayloadFingerprint == comparison.CandidatePayload
        && final.Fingerprint == comparison.CandidatePayload && final.Entries.Any(e => e.Path == target && e.Kind == "file")
        && !report.Findings.Any(f => PackageCheckFact.IsProblem(f) && f.Target == target);
}
