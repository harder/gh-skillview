using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

// Observations for navigation only. Deliberately carries no approval fingerprint.
internal sealed record ReplacementHistoryEntry(string Id, string Hint, bool MayNeedRecovery,
    string? Skill = null, string? Purpose = null, DateTimeOffset? CreatedAt = null,
    string? Source = null, string? OriginalPath = null, long? RecordedSlotBytes = null,
    string? Detail = null, ImmutableArray<string> Slots = default, string? OperationPath = null, string? ScanRoot = null);

internal sealed record ReplacementHistory(ImmutableArray<ReplacementHistoryEntry> Entries,
    int ObservedCount, bool HasOmissions, string? Error = null)
{
    internal const string LimitGuidance = "History quota has reached the limit of 16 retained operations. Open History (F4), Inspect a finished or unapplied review, then explicitly retire it using Abandon unapplied review or Discard Undo data to release capacity. Protected evidence cannot be retired.";
    internal string Summary => Error is not null ? "History unavailable: " + Error
        : $"{ObservedCount}{(HasOmissions ? "+" : "")} retained operations; "
            + $"{Entries.Count(entry => entry.MayNeedRecovery)} shown may need recovery (hint)."
            + (HasOmissions ? " Listing capped at 16; additional entries omitted, states unknown." : "")
            + (ObservedCount >= 16 ? " " + LimitGuidance : "");
}

internal sealed partial class ReplacementPlanStore
{
    internal static string ResolveRoot(ProcessRunner runner) =>
        Path.Combine(Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "replacements");

    /// Existing-only, bounded metadata/name observations. Never Observe, hash a
    /// payload, open a registry, resolve a source or create state during listing.
    internal ReplacementHistory ReadHistory(CancellationToken token, IReadOnlyList<string>? excludedRoots = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!HistoryRootPresent()) return new([], 0, false);
            using var state = NativeStateDirectory.OpenOrCreate(Root, excludedRoots ?? [], token, allowCreate: false);
            using var writer = state.OpenExistingFile("writer.lock", true, token);
            CheckWriter(writer);
            var entries = state.EnumerateHistoryEntries(token).Take(MaxPlans + 2).Where(entry => entry.Name != "writer.lock").ToArray();
            var results = ImmutableArray.CreateBuilder<ReplacementHistoryEntry>();
            foreach (var entry in entries.Take(MaxPlans).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (entry.Error is not null || !entry.Directory || !QuarantineStore.ValidId(entry.Name))
                {
                    results.Add(new(entry.Name, "Protected — Inspect to verify", false, Detail: entry.Error ?? "Unknown state entry/type; preserved without following it."));
                    continue;
                }
                try
                {
                    using var operation = NativeStateDirectory.OpenOrCreate(Path.Combine(state.CanonicalPath, entry.Name), excludedRoots ?? [], token, allowCreate: false);
                    using var operationWriter = operation.OpenExistingFile("writer.lock", true, token);
                    CheckWriter(operationWriter);
                    var observed = ReadHistoryEntry(operation, entry.Name, token);
                    operation.ValidateLocation(token);
                    results.Add(observed);
                }
                catch (Exception ex) when (HistoryReadFailure(ex))
                { results.Add(new(entry.Name, "Protected — Inspect to verify", true, Detail: ex.Message)); }
            }
            state.ValidateLocation(token);
            return new(results.ToImmutable(), entries.Length, entries.Length > MaxPlans);
        }
        catch (Exception ex) when (HistoryReadFailure(ex))
        { return new([], 0, false, ex.Message); }
    }

    private bool HistoryRootPresent()
    {
        try { _ = File.GetAttributes(Root); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool HistoryReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or EntryPointNotFoundException;

    // Cheap preflight before source resolution/staging. The existing writer-held
    // quota check still decides allocation; this observation cannot authorize it.
    private void RequireHistoryCapacity(InventorySnapshot inventory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HistoryRootPresent()) return;
        using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token, allowCreate: false);
        using var writer = state.OpenExistingFile("writer.lock", true, token);
        CheckWriter(writer);
        CheckPlanCapacity(state, token);
    }

    private static void CheckPlanCapacity(NativeStateDirectory state, CancellationToken token)
    {
        var entries = state.EnumerateEntries(token).Take(MaxPlans + 2).ToArray();
        if (entries.Length > MaxPlans) throw new IOException(ReplacementHistory.LimitGuidance);
        if (entries.Any(entry => entry.Name != "writer.lock" && !QuarantineStore.ValidId(entry.Name)))
            throw new IOException("New reviews are blocked by unknown retained state entries. Open History (F4) to inspect. Unknown entries remain protected; inspect them before creating another review.");
    }

    private static ReplacementHistoryEntry ReadHistoryEntry(NativeStateDirectory operation, string id, CancellationToken token)
    {
        const int entryLimit = MaxSteps + 11 + 2 * MaxMergeAttempts + MaxSourceReviewEntries;
        var entries = operation.EnumerateHistoryEntries(token).Take(entryLimit + 1).ToArray();
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var slots = new[] { "candidate", "original", "displaced", "recorded-base", "undo-recovery", "owner-journal", "local-review" }
            .Where(names.Contains).ToImmutableArray();
        var plan = Read(operation, "plan.json", ReplacementJsonContext.Default.ReplacementPlan, token);
        // Metadata is a saved, unverified locator. Full schema/hash/native checks
        // belong exclusively to explicit Inspect, including historical merge plans.
        if (plan.Id != id || plan.SchemaVersion is < 1 or > 6 || plan.Subject?.Metadata is null
            || string.IsNullOrWhiteSpace(plan.Subject.Name) || string.IsNullOrWhiteSpace(plan.OriginalPath) || string.IsNullOrWhiteSpace(plan.RootPath)
            || !Path.IsPathFullyQualified(plan.OriginalPath) || !Path.IsPathFullyQualified(plan.RootPath) || plan.Original?.Entries.IsDefault != false || plan.Candidate?.Entries.IsDefault != false)
            throw new IOException("Saved metadata is incomplete or unsupported; Inspect to verify.");
        var knownPurpose = (plan.SchemaVersion, plan.Purpose) is (1, "restore-recorded") or (2, "update-revision")
            or (3, "update-revision") or (4, "unpin-only") or (5, "local-reimport") or (6, LocalSourceSwitchPurpose);
        var historical = plan.SchemaVersion == 3 || names.Contains("local-review") || names.Any(name => IsMergeSlot(name) || IsMergePlan(name));
        var directoryNames = new[] { "candidate", "original", "displaced", "recorded-base", "owner-journal", "undo-recovery", "local-review", "installed-capture", "local-source-capture", "gh-input-capture", "gh-output-capture" };
        var unknown = !knownPurpose || entries.Length > entryLimit || entries.Any(entry => entry.Error is not null
            || entry.Directory != (directoryNames.Contains(entry.Name) || IsMergeSlot(entry.Name)
                || entry.Name.StartsWith("review-installed-", StringComparison.Ordinal) || entry.Name.StartsWith("review-source-", StringComparison.Ordinal)))
            || names.Any(name => name is not ("writer.lock" or "plan.json" or "candidate" or "original" or "displaced" or "recorded-base" or "owner-journal" or "undo-recovery" or "local-review" or "installed-capture" or "local-source-capture" or "gh-input-capture" or "gh-output-capture")
                && !IsSourceReviewEntry(name) && !IsMergeSlot(name) && !IsMergePlan(name)
                && !Enumerable.Range(0, MaxSteps).Any(index => name == $"step-{index:D4}.json"));
        var candidate = names.Contains("candidate"); var original = names.Contains("original"); var displaced = names.Contains("displaced");
        // Owner-journal presence is deliberately conservative: even matching
        // completion filenames cannot prove a completed owner transition.
        var retiring = names.Contains("retiring.json");
        var recovery = retiring || candidate && names.Any(name => name.StartsWith("step-", StringComparison.Ordinal)) || names.Contains("undo-recovery") || names.Contains("owner-journal") || original && (candidate || displaced);
        var hint = retiring ? "Protected — retirement started; Inspect to verify" : unknown || historical ? "Protected — Inspect to verify"
            : recovery ? "May need recovery (hint) — Inspect to verify"
            : candidate && !original && !displaced ? "Unapplied review (hint) — Inspect to verify"
            : original && !candidate && !displaced ? "Applied / Undo data retained (hint) — Inspect to verify"
            : displaced && !candidate && !original ? "Undone (hint) — Inspect to verify"
            : "Protected — Inspect to verify";
        long? EvidenceBytes(ReplacementEvidence? evidence) => evidence is null || evidence.Entries.IsDefault
            || evidence.Entries.Length > PayloadSnapshot.MaxEntries
            || evidence.Entries.Any(entry => entry is null || entry.Bytes is < 0 or > PayloadSnapshot.MaxBytes)
                ? null : evidence.Entries.Sum(entry => entry.Bytes);
        long? size = unknown || historical ? null : (original ? EvidenceBytes(plan.Original) : 0)
            + (candidate || displaced ? EvidenceBytes(plan.Candidate) : 0)
            + (names.Contains("recorded-base") ? EvidenceBytes(plan.Revision?.RecordedBase) : 0);
        return new(id, hint, recovery || unknown, plan.Subject.Name, plan.Purpose, plan.CreatedAt,
            plan.Subject.Metadata.LocalPath ?? plan.Subject.Metadata.Upstream, plan.OriginalPath, size,
            "Saved metadata and entry-name hints only; live locations, payloads and owner state are unverified."
                + (retiring ? " Retirement intent retained; no automatic deletion or resume. Inspect for explicit approval or protected-remnant guidance." : "")
                + (historical ? " Historical merge evidence remains protected." : "")
                + (names.Contains("owner-journal") ? " Owner journal retained; completion is unknown until Inspect." : ""), slots, operation.CanonicalPath, plan.RootPath);
    }
}
