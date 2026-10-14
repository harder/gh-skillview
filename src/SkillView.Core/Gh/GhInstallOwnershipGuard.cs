using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

internal static class GhInstallOwnershipGuard
{
    internal sealed record Context(ImmutableArray<SkillPackage> Claims);

    internal static Context Capture(IReadOnlyList<string> destinations, ProcessRunner runner,
        GhSkillInstallService.Options options, Logger logger, CancellationToken token)
    {
        var home = runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", options.ProcessEnvironment);
        var state = runner.GetEffectiveEnvironmentVariable("XDG_STATE_HOME", options.ProcessEnvironment);
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        if (!string.IsNullOrEmpty(home)) home = Path.GetFullPath(home, workingDirectory);
        if (!string.IsNullOrEmpty(state)) state = Path.GetFullPath(state, workingDirectory);
        var diagnostics = new List<LockReadDiagnostic>();
        var discoveryRoots = destinations.SelectMany(destination => new[] { destination, CanonicalCandidate(destination) })
            .Distinct(StringComparer.Ordinal).ToArray();
        var observations = new SkillLockFileReader(logger).LoadObservations(discoveryRoots, token, new(home, state), diagnostics);
        bool Covers(IEnumerable<string> roots) => roots.Any(root => destinations.Any(destination => SameDirectory(root, destination)));
        if (diagnostics.Any(diagnostic => diagnostic.CoveredRoots.IsEmpty || Covers(diagnostic.CoveredRoots)))
            throw new IOException("Destination ownership is incomplete; resolve its lockfile diagnostics before installing.");
        return new(observations.Where(observation => Covers(observation.Package.CoveredRoots))
            .Select(observation => observation.Package).ToImmutableArray());
    }

    internal static void Validate(Context context, IReadOnlyList<string> destinations, IReadOnlyList<string>? selectors,
        CancellationToken token)
    {
        if (context.Claims.IsEmpty) return;
        if (selectors is null || selectors.Count == 0)
            throw new IOException("The source's exact installation names are unresolved in an owned destination; use a coordinated maintenance plan.");
        if (selectors.Count > SkillLockFileReader.MaxEntries) throw new IOException("Installation selector limit exceeded.");
        // Case folding only narrows candidate work. Describes/PathIdentity is
        // the authority for each actual filesystem lookup, including sensitive directories.
        var flatNames = selectors.Select(selector => selector.Split('/')[^1])
            .Distinct(StringComparer.Ordinal).GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var claim in context.Claims)
        {
            token.ThrowIfCancellationRequested();
            if (claim.ReadOnly || claim.Selector is null)
                throw new IOException("Unsupported destination owner evidence requires a coordinated maintenance plan.");
            if (!flatNames.TryGetValue(claim.Selector.Split('/')[^1], out var candidates)) continue;
            foreach (var destination in destinations)
            {
                token.ThrowIfCancellationRequested();
                if (!claim.CoveredRoots.Any(root => SameDirectory(root, destination))) continue;
                foreach (var name in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(destination, name);
                    var physical = PathResolver.Resolve(path) ?? path;
                    var target = new InstalledSkill
                    {
                        Name = name,
                        ResolvedPath = physical,
                        ScanRoot = destination,
                        Scope = Scope.Custom,
                        Agents = [new("unknown", path, PathResolver.IsSymlink(path), DiscoveryRoot: destination)],
                        FrontMatter = SkillFrontMatter.Empty,
                        Validity = ValidityState.Valid,
                        Provenance = Provenance.FsScan,
                        Ignored = false,
                        IsSymlinked = false,
                        InstalledAt = null
                    };
                    var contextual = claim with { CoveredRoots = claim.CoveredRoots.Add(destination) };
                    if (contextual.Describes(target))
                        throw new IOException("The install destination has a contextual owner record; use a coordinated maintenance plan before replacement.");
                }
            }
        }
    }

    internal static void ValidateUnchanged(Context context, CancellationToken token)
    {
        foreach (var group in context.Claims.GroupBy(claim => claim.LockFilePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key is null || group.Any(claim => claim.RecordFingerprint is null))
                throw new IOException("Destination owner evidence is incomplete; refresh before installing.");
            var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SkillLockFileReader.ReadBytes(group.Key, token)));
            if (group.Any(claim => claim.RecordFingerprint != fingerprint))
                throw new IOException("Destination owner records changed during discovery; refresh before installing.");
        }
    }

    private static bool SameDirectory(string left, string right) => PathIdentity.Equals(left, right)
        || PathIdentity.Equals(CanonicalCandidate(left), CanonicalCandidate(right));

    private static string CanonicalCandidate(string path)
    {
        return OwnershipPath.CanonicalCandidate(path)
            ?? throw new IOException("Destination ownership context could not be established within the depth limit.");
    }
}
