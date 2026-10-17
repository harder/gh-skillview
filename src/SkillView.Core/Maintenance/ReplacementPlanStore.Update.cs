using System.Collections.Immutable;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private static bool IsUnchangedRevision(ReplacementPlan plan) =>
        IsUnchangedRevision(plan, plan.Original.Payload == plan.Candidate.Payload);

    private static bool IsUnchangedRevision(ReplacementPlan plan, bool payloadsMatch)
    {
        if (plan.Revision is not { } revision || plan.Original.Mode != plan.Candidate.Mode
            || !payloadsMatch
            || revision.Owner is { } owner && owner.BeforeFingerprint != owner.DesiredFingerprint)
            return false;
        try { SameTracking(plan.Subject.Metadata, revision.DesiredMetadata); return true; }
        catch (IOException) { return false; }
    }

    internal async Task<ReplacementPreparation> PlanLatestAsync(string ghPath, InstalledSkill target,
        InventorySnapshot inventory, bool unpin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ManagedSkillDiscovery.RefuseTarget(target, token, runner);
        DesiredRevisionPolicyGate.RequireAllowed(target, DesiredPolicies.Observe(target, Exclusions(inventory), token),
            "update-revision", explicitUnpin: unpin);
        if (target.Pinned && !unpin) throw new IOException("Exact pin retained; updating this installation requires explicit unpin review.");
        var process = runner ?? throw new IOException("Latest update planning requires a captured process context.");
        var source = await new GhRevisionResolver(process, ghPath).ResolveLatestAsync(
            target.FrontMatter.Upstream ?? throw new IOException("No observed remote source."), target.FrontMatter.GithubPath, token).ConfigureAwait(false);
        var desired = target.FrontMatter with
        {
            GithubTreeSha = source.Tree,
            GithubRef = source.Ref,
            GithubPath = source.Subpath,
            PinnedRef = null,
            Pinned = false
        };
        var stager = new GhRecordedTreeStager(process);
        return await PrepareUpdateAsync(target, inventory, source,
            (destination, ct) => Stage(desired, source.Tree, destination, ct),
            (destination, ct) => Stage(target.FrontMatter, target.TreeSha!, destination, ct), unpin, token).ConfigureAwait(false);

        Task Stage(SkillFrontMatter tracking, string tree, NativeStateDirectory destination, CancellationToken ct) =>
            stager.StageIntoAsync(ghPath, source.Repository, tree, destination, bytes =>
            {
                if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Staged SKILL.md exceeds its metadata budget.");
                return Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(new UTF8Encoding(false, true).GetString(bytes), tracking));
            }, ct);
    }

    internal async Task<ReplacementResult> PlanUpdateAsync(InstalledSkill target, InventorySnapshot inventory,
        ResolvedSkillRevision source, Func<NativeStateDirectory, CancellationToken, Task> stage,
        Func<NativeStateDirectory, CancellationToken, Task> stageBase, bool unpin, CancellationToken token) =>
        (await PrepareUpdateCoreAsync(target, inventory, source, stage, stageBase, unpin, false, token).ConfigureAwait(false)).Plan!;

    internal Task<ReplacementPreparation> PrepareUpdateAsync(InstalledSkill target, InventorySnapshot inventory,
        ResolvedSkillRevision source, Func<NativeStateDirectory, CancellationToken, Task> stage,
        Func<NativeStateDirectory, CancellationToken, Task> stageBase, bool unpin, CancellationToken token) =>
        PrepareUpdateCoreAsync(target, inventory, source, stage, stageBase, unpin, true, token);

    private Task<ReplacementPreparation> PrepareUpdateCoreAsync(InstalledSkill target, InventorySnapshot inventory,
        ResolvedSkillRevision source, Func<NativeStateDirectory, CancellationToken, Task> stage,
        Func<NativeStateDirectory, CancellationToken, Task> stageBase, bool unpin, bool allowUnchanged, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target.Pinned && !unpin) throw new IOException("Exact pin retained; updating this installation requires explicit unpin review.");
        var desired = target.FrontMatter with
        {
            GithubTreeSha = source.Tree,
            GithubRef = source.Ref,
            GithubPath = source.Subpath,
            PinnedRef = null,
            Pinned = false
        };
        var revision = new ReplacementRevision(source, desired, null, []);
        ValidateSource(target.FrontMatter, revision);
        revision = revision with { Owner = PrepareOwner(target, source, inventory, token) };
        return PlanCoreAsync(target, inventory, stage, revision, stageBase, token, allowUnchanged);
    }

    private static void ValidateSource(SkillFrontMatter original, ReplacementRevision revision)
    {
        var source = revision.Source;
        var desired = revision.DesiredMetadata;
        if (source is null || desired is null || string.IsNullOrWhiteSpace(original.Upstream)
            || GhRevisionResolver.Repository(source.Repository) != GhRevisionResolver.Repository(original.Upstream)
            || SkillSourceIdentity.NormalizeSubpath(original.GithubPath) != source.Subpath
            || source.Commit is not { Length: 40 } || !source.Commit.All(char.IsAsciiHexDigit)
            || source.Tree is not { Length: 40 } || !source.Tree.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(source.Ref) || source.Ref.Any(char.IsControl)
            || !(source.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) || source.Ref.StartsWith("refs/tags/", StringComparison.Ordinal))
            || desired.Upstream != source.Repository || desired.GithubTreeSha != source.Tree
            || desired.GithubRef != source.Ref || desired.GithubPath != source.Subpath
            || desired.Pinned || desired.PinnedRef is not null || desired.LocalPath is not null || !desired.Diagnostics.IsEmpty)
            throw new IOException("Saved update source, revision or pin transition is inconsistent.");
    }

    private static void ValidateRevision(ReplacementPlan plan, string operationPath, CancellationToken token)
    {
        if (plan.Revision is not { } revision) return;
        if (revision.RecordedBase is not { } baseline
            || baseline.Identity.CanonicalPath != Path.Combine(operationPath, "recorded-base")
            || revision.LocalChanges.IsDefault || revision.LocalChanges.Length > PayloadSnapshot.MaxEntries
            || revision.LocalChanges.Any(path => string.IsNullOrEmpty(path)))
            throw new IOException("Saved update baseline or owner transition is invalid.");
        ValidateSource(plan.Subject.Metadata, revision);
        ValidateEvidence(baseline);
        if (revision.Owner is { } owner) ValidateOwnerTransition(plan.Subject.Metadata, plan.Subject.Owners, revision.Source, owner, token);
        else if (!plan.Subject.Owners.IsEmpty) throw new IOException("Saved update lacks the required owner transition.");
    }
}
