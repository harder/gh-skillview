using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

internal static class GhInstallOwnershipGuard
{
    internal sealed record Context(ImmutableArray<SkillPackage> Claims, ManagedSkillDiscovery.Observation Managed,
        ProcessRunner Runner, GhSkillInstallService.Options Options)
    {
        internal bool NeedsManagedSelectors(IReadOnlyList<string> destinations) => Managed.Regions.Any(region =>
            destinations.Any(destination => region.Covers(destination) || region.IsInside(destination)));
    }

    internal static Context Capture(IReadOnlyList<string> destinations, ProcessRunner runner,
        GhSkillInstallService.Options options, Logger logger, CancellationToken token)
    {
        var home = runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", options.ProcessEnvironment);
        var state = runner.GetEffectiveEnvironmentVariable("XDG_STATE_HOME", options.ProcessEnvironment);
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? runner.EffectiveWorkingDirectory);
        if (!string.IsNullOrEmpty(home)) home = Path.GetFullPath(home, workingDirectory);
        if (!string.IsNullOrEmpty(state)) state = Path.GetFullPath(state, workingDirectory);
        var diagnostics = new List<LockReadDiagnostic>();
        var discoveryRoots = destinations.SelectMany(destination => new[] { destination, CanonicalCandidate(destination) })
            .Distinct(StringComparer.Ordinal).ToArray();
        var managed = ManagedSkillDiscovery.Capture(runner, discoveryRoots.Select(path => new ScanRoot(path, Scope.Custom, null)),
            token, options.ProcessEnvironment, workingDirectory, allowHidden: true, cachePathObservations: false);
        ManagedSkillDiscovery.Refuse(managed, destinations);
        var observations = new SkillLockFileReader(logger).LoadObservations(discoveryRoots, token, new(home, state), diagnostics);
        bool Covers(IEnumerable<string> roots) => roots.Any(root => destinations.Any(destination => SameDirectory(root, destination)));
        if (diagnostics.Any(diagnostic => diagnostic.CoveredRoots.IsEmpty || Covers(diagnostic.CoveredRoots)))
            throw new IOException("Destination ownership is incomplete; resolve its lockfile diagnostics before installing.");
        return new(observations.Where(observation => Covers(observation.Package.CoveredRoots))
            .Select(observation => observation.Package).ToImmutableArray(), managed, runner, options);
    }

    internal static void Validate(Context context, IReadOnlyList<string> destinations, IReadOnlyList<string>? selectors,
        CancellationToken token)
    {
        if ((selectors is null || selectors.Count == 0) && context.NeedsManagedSelectors(destinations))
            throw new IOException("Managed destination children require exact source selectors before installation.");
        var replacements = destinations.SelectMany(destination => (selectors ?? []).Select(selector =>
            Path.Combine(destination, selector.Split('/')[^1]))).ToArray();
        var selected = destinations.Concat(replacements).ToArray();
        ManagedSkillDiscovery.Refuse(context.Managed, selected);
        ManagedSkillDiscovery.Refuse(context.Managed, replacements, includeContainedRegions: true);
        var live = ManagedSkillDiscovery.Capture(context.Runner,
            selected.Select(path => new ScanRoot(path, Scope.Custom, null)), token,
            context.Options.ProcessEnvironment, context.Options.WorkingDirectory, discoverPayloads: false, cachePathObservations: false);
        ManagedSkillDiscovery.Refuse(live, selected);
        ManagedSkillDiscovery.Refuse(live, replacements, includeContainedRegions: true);
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

    // No selector-to-native-name guess can authorize replacement. Until that
    // mapping is proven, force is a fresh-install option only: every admitted
    // root must be positively missing or an empty ordinary native directory.
    internal static void RequireVacantForceDestinations(IReadOnlyList<string> destinations,
        GhSkillInstallService.Options options, CancellationToken token)
    {
        if (!options.Overwrite) return;
        if (destinations.Count is 0 or > 128) throw new IOException("Force install destination coverage is incomplete.");
        foreach (var destination in destinations)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var placement = NativePlacementLease.Open(destination, token);
                if (placement.Evidence.MissingSuffix.IsEmpty)
                {
                    var canonical = placement.Evidence.CanonicalPath;
                    using var directory = NativeExposureDirectory.Open(canonical, Path.Combine(canonical, "force-install-vacancy"), token);
                    var observed = directory.Capture(token);
                    if ((observed.Root with { DirectoryFingerprint = null }) != (placement.Evidence.ExistingAncestor with { DirectoryFingerprint = null })
                        || observed.Parent != observed.Root || !observed.Entries.IsEmpty)
                        throw new IOException("Destination is occupied or its native namespace changed.");
                    placement.Revalidate(token);
                    directory.Validate(observed, token);
                }
                else placement.Revalidate(token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Force install refused for destination '{destination}': {error.Message} "
                    + "Existing or uncertain contents are not a reviewed local-change disposition. No selector-to-native target was inferred. "
                    + "Inspect the exact installation and its source; use a supported saved update/restore or local reimport/source-switch review with explicit retention where evidence permits. "
                    + "Unknown provenance does not imply an available repair.", error);
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
