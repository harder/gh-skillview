using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Loading;

internal sealed record LoadingDiscoveryRoot(string Path, string Placement, bool IncludeHidden,
    string Applicability = "applied", string? Reason = null, string? SourceEvidence = null)
{
    internal bool Covers(string entry)
    {
        if (!PathIdentity.IsInside(entry, Path)) return false;
        return IncludeHidden || !System.IO.Path.GetRelativePath(Path, entry).Split(System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar).Any(part => part.StartsWith('.') && part != ".");
    }
}

internal sealed record LoadingRootObservation(ImmutableArray<string> Roots, ImmutableArray<DiscoveryLocation> Failures)
{
    internal InventorySnapshot Apply(InventorySnapshot inventory) => Failures.IsEmpty ? inventory : inventory with
    {
        ConfiguredRootsComplete = false,
        Skills = inventory.Skills.Select(skill => skill.ObservedSubject is { } subject ? skill with
        { ObservedSubject = subject with { ExposureCoverageComplete = false, ExposureCoverage = "Additional loading discovery coverage is incomplete; other consumers may exist." } } : skill).ToImmutableArray(),
        Diagnostics = inventory.Diagnostics with
        {
            InaccessiblePaths = inventory.Diagnostics.InaccessiblePaths.AddRange(Failures.Select(failure => failure.Path)),
            Locations = inventory.Diagnostics.Locations.AddRange(Failures)
        }
    };
}

internal static class LoadingDiscoveryCapture
{
    internal static LoadingRootObservation Probe(IEnumerable<string> candidates, CancellationToken token,
        Func<string, FileAttributes>? inspectForTests = null)
    {
        var roots = ImmutableArray.CreateBuilder<string>(); var failures = ImmutableArray.CreateBuilder<DiscoveryLocation>();
        var inspect = inspectForTests ?? File.GetAttributes;
        var count = 0;
        foreach (var path in candidates.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 256) { failures.Add(new(path, "unsupported", "Loading discovery root limit exceeded; omitted locations remain unknown.")); break; }
            try
            {
                var attributes = inspect(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                    failures.Add(new(path, "unsupported", "The expected loading root is not a directory."));
                else roots.Add(path);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures.Add(new(path, "unsupported", "Loading discovery root could not be inspected; absence cannot be inferred.")); }
        }
        return new(roots.ToImmutable(), failures.ToImmutable());
    }
}
