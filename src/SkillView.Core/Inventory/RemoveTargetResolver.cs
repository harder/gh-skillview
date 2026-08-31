using System.Collections.Immutable;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

internal enum RemoveTargetKind
{
    CurrentInstall,
    AgentSymlink,
    PackageGroup,
    RepoGroup,
}

internal sealed record RemoveTarget(
    RemoveTargetKind Kind,
    string Title,
    string Description,
    ImmutableArray<InstalledSkill> Skills,
    AgentMembership? AgentMembership = null,
    string? GroupKey = null);

internal sealed record RemoveTargetItem(
    InstalledSkill Skill,
    RemoveValidator.RemoveValidation Validation);

internal sealed record RemoveTargetEvaluation(
    RemoveTarget Target,
    ImmutableArray<RemoveTargetItem> Items)
{
    public bool CanExecute => Items.Length > 0
        && Items.All(item => item.Validation.Allowed);
    public bool RequiresSecondConfirm =>
        Items.Any(item => item.Validation.RequiresSecondConfirm);

    public ImmutableArray<RemoveValidator.Error> Errors => Items
        .SelectMany(item => item.Validation.Errors)
        .ToImmutableArray();

    public ImmutableArray<RemoveValidator.Warning> Warnings => Items
        .SelectMany(item => item.Validation.Warnings)
        .ToImmutableArray();
}

internal static class RemoveTargetResolver
{
    public static ImmutableArray<RemoveTarget> BuildTargets(InstalledSkill selected, InventorySnapshot snapshot)
        => BuildTargetsWithCancellation(selected, snapshot, CancellationToken.None);

    public static ImmutableArray<RemoveTarget> BuildTargetsWithCancellation(
        InstalledSkill selected,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = ImmutableArray.CreateBuilder<RemoveTarget>();
        targets.Add(new RemoveTarget(
            RemoveTargetKind.CurrentInstall,
            "Remove this skill",
            "Delete the selected installed skill.",
            [selected]));

        foreach (var agent in selected.Agents.Where(agent => agent.IsSymlink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add(new RemoveTarget(
                RemoveTargetKind.AgentSymlink,
                $"Unlink from {agent.AgentId}",
                $"Remove only the {agent.AgentId} symlink and keep the canonical copy.",
                [selected],
                AgentMembership: agent));
        }

        var packageTarget = BuildPackageTarget(selected, snapshot, cancellationToken);
        if (packageTarget is not null)
        {
            targets.Add(packageTarget);
        }

        var repoTarget = BuildRepoTarget(selected, snapshot, cancellationToken);
        if (repoTarget is not null
            && !HasSameSkills(repoTarget, packageTarget, cancellationToken))
        {
            targets.Add(repoTarget);
        }

        return targets.ToImmutable();
    }

    public static RemoveTargetEvaluation Evaluate(
        RemoveTarget target,
        InventorySnapshot snapshot) =>
        EvaluateWithCancellation(target, snapshot, CancellationToken.None);

    public static RemoveTargetEvaluation EvaluateWithCancellation(
        RemoveTarget target,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Kind == RemoveTargetKind.AgentSymlink)
        {
            if (target.Skills.Length != 1 || target.AgentMembership is not { } link)
            {
                return new RemoveTargetEvaluation(
                    target,
                    ImmutableArray<RemoveTargetItem>.Empty);
            }
            var skill = target.Skills[0];
            cancellationToken.ThrowIfCancellationRequested();
            return new RemoveTargetEvaluation(
                target,
                [new RemoveTargetItem(
                    skill,
                    RemoveValidator.ValidateSymlink(link.Path, snapshot.ScannedRoots))]);
        }

        var items = ImmutableArray.CreateBuilder<RemoveTargetItem>(target.Skills.Length);
        foreach (var skill in target.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new RemoveTargetItem(
                skill,
                RemoveValidator.Validate(skill, snapshot.ScannedRoots, snapshot.Skills)));
        }

        return new RemoveTargetEvaluation(target, items.ToImmutable());
    }

    internal static string? RepoKey(InstalledSkill skill)
    {
        var value = skill.Package?.SourceUrl;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = skill.FrontMatter.Upstream;
        }

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static RemoveTarget? BuildPackageTarget(
        InstalledSkill selected,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var package = selected.Package;
        if (package is null || string.IsNullOrWhiteSpace(package.Source))
        {
            return null;
        }

        var matches = snapshot.Skills
            .Where(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return string.Equals(
                    skill.Package?.Source,
                    package.Source,
                    StringComparison.OrdinalIgnoreCase);
            })
            .DistinctBy(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PathIdentity.NormalizeKey(skill.ResolvedPath);
            })
            .ToImmutableArray();

        return matches.Length > 1
            ? new RemoveTarget(
                RemoveTargetKind.PackageGroup,
                $"Remove package: {package.Source}",
                $"Delete all {matches.Length} skills installed from the same package.",
                matches,
                GroupKey: package.Source)
            : null;
    }

    private static RemoveTarget? BuildRepoTarget(
        InstalledSkill selected,
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var repoKey = RepoKey(selected);
        if (string.IsNullOrWhiteSpace(repoKey))
        {
            return null;
        }

        var matches = snapshot.Skills
            .Where(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return string.Equals(
                    RepoKey(skill),
                    repoKey,
                    StringComparison.OrdinalIgnoreCase);
            })
            .DistinctBy(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PathIdentity.NormalizeKey(skill.ResolvedPath);
            })
            .ToImmutableArray();

        return matches.Length > 1
            ? new RemoveTarget(
                RemoveTargetKind.RepoGroup,
                $"Remove repo: {repoKey}",
                $"Delete all {matches.Length} skills linked to the same repo.",
                matches,
                GroupKey: repoKey)
            : null;
    }

    private static bool HasSameSkills(
        RemoveTarget candidate,
        RemoveTarget? other,
        CancellationToken cancellationToken)
    {
        if (other is null || candidate.Skills.Length != other.Skills.Length)
        {
            return false;
        }

        var left = candidate.Skills
            .Select(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PathIdentity.NormalizeKey(skill.ResolvedPath);
            })
            .OrderBy(path => path, StringComparer.Ordinal);
        var right = other.Skills
            .Select(skill =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PathIdentity.NormalizeKey(skill.ResolvedPath);
            })
            .OrderBy(path => path, StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }
}
