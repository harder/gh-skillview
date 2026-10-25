using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

/// A read-only issue at one physical address. Neither this row nor its ID grants
/// execution authority: specialist workflows must capture and review fresh evidence.
public sealed record MaintenanceQueueEntry(string Id, string Issue, string Title,
    OperationSubject Subject, ImmutableArray<string> AffectedPaths, string Confidence,
    string Reason, string RecommendedAction, bool CleanupSuppressed, bool Pinned)
{
    public string UpdateState => "unknown";
    public string UpdateReason => "No update availability check was requested by this queue capture.";
    public string RuntimeState => "unknown";
    public string RuntimeReason => "Inventory observes disk and recorded ownership, not a running agent session.";
    public string DisablementState => "not-assessed";
    public string MigrationState => "not-assessed";
    internal CleanupClassifier.Candidate? Cleanup { get; init; }
    internal PinDisplayPolicy? PinDisplay { get; init; }
    internal bool IsCopyReview => Issue is "NameCollision" or "Duplicate";
}

public sealed record MaintenanceQueue(DateTimeOffset CapturedAt, bool Partial,
    ImmutableArray<string> Diagnostics, ImmutableArray<MaintenanceQueueEntry> Entries);

public static class MaintenanceQueueBuilder
{
    public static MaintenanceQueue Build(InventorySnapshot snapshot, CancellationToken token = default)
    {
        var entries = ImmutableArray.CreateBuilder<MaintenanceQueueEntry>();
        var cleanup = CleanupClassifier.ClassifyWithCancellation(snapshot, snapshot.ScannedRoots, null, token);
        foreach (var candidate in cleanup)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Impact is not { } impact) continue;
            var subject = impact.Subject;
            entries.Add(new(EntryId(candidate.Kind.ToString(), subject.InstallationId), candidate.Kind.ToString(),
                subject.Name ?? Path.GetFileName(candidate.Path), subject,
                Paths(candidate.Path, impact.AffectedExposures),
                candidate.ReviewOnly ? "review-required" : "observed-candidate", candidate.Reason,
                "review-cleanup", candidate.Skill?.Ignored ?? false, IsPinned(candidate.Skill))
            { Cleanup = candidate, PinDisplay = candidate.Skill is { } skill ? PinDisplayPolicy.From(skill) : null });
        }

        // Unknown availability is a review opportunity, never an available update.
        // Include suppressed and pinned installs: those policies do not disable an
        // agent and cannot establish whether a revision is current or loaded.
        foreach (var skill in snapshot.Skills.DistinctBy(skill => skill.InstallationId))
        {
            token.ThrowIfCancellationRequested();
            var subject = OperationSubject.From(skill);
            entries.Add(new(EntryId("installation-review", subject.InstallationId), "installation-review", skill.Name,
                subject, Paths(subject.Path, subject.Exposures),
                subject.ExposureCoverageComplete && !subject.Source.Conflicted ? "recorded-observation" : "limited-observation",
                "Inspect source, revision, owners and consumers before requesting a check or saved plan.",
                "review-installation", skill.Ignored, IsPinned(skill))
            { PinDisplay = PinDisplayPolicy.From(skill) });
        }

        var diagnostics = snapshot.Diagnostics.InaccessiblePaths.Select(path => "Incomplete discovery: " + path)
            .Concat(snapshot.Providers.Where(provider => !provider.Succeeded)
                .Select(provider => provider.Provider + ": " + (provider.Error ?? "capture failed")))
            .Concat(snapshot.ConfiguredRootsComplete ? [] : new[] { "Configured-root discovery is incomplete." })
            .Concat(snapshot.PreferenceDiagnostics).ToImmutableArray();
        return new(snapshot.CapturedAt, snapshot.IsPartial || !snapshot.ConfiguredRootsComplete, diagnostics,
            entries.DistinctBy(entry => entry.Id).OrderBy(entry => entry.Issue, StringComparer.Ordinal)
                .ThenBy(entry => entry.Subject.Path, StringComparer.Ordinal).ToImmutableArray());
    }

    internal static InstalledSkill? ResolveInstallation(InventorySnapshot snapshot, OperationSubject selected)
    {
        var matches = snapshot.Skills.Where(skill => skill.InstallationId == selected.InstallationId
            && PathIdentity.Equals(skill.ResolvedPath, selected.Path)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsPinned(InstalledSkill? skill) => skill is not null && (skill.Pinned
        || skill.Revision.PinnedRef is not null || skill.AllOwnershipClaims.Any(owner => owner.PinnedRef is not null));

    private static string EntryId(string issue, string installationId) => issue + ":" + installationId;

    private static ImmutableArray<string> Paths(string target, IEnumerable<ObservedSkillExposure> exposures) =>
        new[] { target }.Concat(exposures.Select(exposure => exposure.Membership.Path))
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
}
