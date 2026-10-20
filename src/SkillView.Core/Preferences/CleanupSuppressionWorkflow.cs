using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Preferences;

internal sealed record SuppressionBatchResult(int Changed, int Skipped, int Failed, bool Canceled, ImmutableArray<string> Errors);

internal static class CleanupSuppressionWorkflow
{
    internal static async Task<SuppressionBatchResult> SetAsync(
        IReadOnlyList<CleanupClassifier.Candidate> selected, CleanupSuppressionStore store,
        Func<CancellationToken, Task<InventorySnapshot>> capture, Action mutationObserved,
        Action targetChanged, CancellationToken token)
    {
        var changed = 0; var skipped = 0; var failed = 0;
        var errors = ImmutableArray.CreateBuilder<string>();
        try
        {
            var inventory = await capture(token).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in selected)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(PathIdentity.NormalizeKey(candidate.Path))) { skipped++; continue; }
                if (candidate.Skill is not { } reviewed || reviewed.Validity == ValidityState.BrokenSymlink)
                {
                    skipped++; Error(candidate.Path + ": no supported installation binding; entry left unchanged."); continue;
                }
                try
                {
                    var matches = inventory.Skills.Where(skill => PathIdentity.Equals(skill.ResolvedPath, reviewed.ResolvedPath))
                        .Take(2).ToArray();
                    if (matches.Length > 1)
                        throw new IOException("Selected installation is ambiguous in refreshed inventory; refresh or reconcile it before ignoring.");
                    var current = matches.FirstOrDefault();
                    if (current is null || current.SourceIdentity != reviewed.SourceIdentity || current.Name != reviewed.Name
                        || CleanupSuppressionStore.OwnerNamespace(current) != CleanupSuppressionStore.OwnerNamespace(reviewed))
                        throw new IOException("Selected installation source/context changed; refresh before ignoring.");
                    if (current.SourceIdentity.Key is null && !CleanupSuppressionStore.SameObject(reviewed.ObservedSubject?.NativeObject, current.ObservedSubject?.NativeObject))
                        throw new IOException("Selected unknown-source object changed or its continuity is unavailable; refresh before ignoring.");
                    var result = store.Set(current, inventory, false, token, mutationObserved);
                    if (result.Changed) { changed++; targetChanged(); } else skipped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or NotSupportedException)
                { failed++; Error(candidate.Path + ": " + ex.Message); }
            }
            return new(changed, skipped, failed, false, errors.ToImmutable());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(changed, skipped, failed, true, errors.ToImmutable()); }
        void Error(string text) { if (errors.Count < 128) errors.Add(text); }
    }

}
