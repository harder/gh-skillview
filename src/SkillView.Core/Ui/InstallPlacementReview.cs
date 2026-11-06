using System.Text;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;

namespace SkillView.Ui;

// Read-only guidance. Uses the same catalogs/guards as admission, but grants no authority.
internal sealed record InstallPlacementObservation(string Detail, IReadOnlyList<string> Roots, string ContextKey);

internal static class InstallPlacementReview
{
    internal static async Task<InstallPlacementObservation> CaptureAsync(ProcessRunner context, Logger logger, string ghPath,
        string repo, string? skill, GhSkillInstallService.Options options, CancellationToken token)
    {
        options = options with { WorkingDirectory = context.EffectiveWorkingDirectory };
        var text = new StringBuilder($"Source: {repo}\nSkill: {skill ?? "ALL / repository listing"}\nVersion: {options.Version ?? "gh default (not resolved)"}\n");
        text.AppendLine(options.Path is not null ? "Custom folder overrides scope and agents." : $"Scope: {options.Scope}\nAgents: {string.Join(", ", options.Agents ?? [])}");
        text.AppendLine("Universal uses shared .agents/skills. Shared placement does not mean every client is enabled.");
        var roots = await GhPlacementCatalog.DestinationsAsync(options, context, token).ConfigureAwait(false);
        var locations = new StringBuilder("Installation folders (catalog alternatives are not verified final placement):\n");
        foreach (var root in roots) locations.AppendLine(root);
        try
        {
            // Tabular listing proves selectors/materialization names, not source subpaths.
            // Never turn this information into an exact-copy navigation anchor.
            IReadOnlyList<string>? selectors = null;
            if (!options.All && !options.Upstream && skill is { Length: > 0 })
            {
                var requested = skill;
                var version = options.Version;
                if (requested.LastIndexOf('@') is >= 0 and var at)
                { version ??= requested[(at + 1)..]; requested = requested[..at]; }
                var listing = await new GhSkillInstallService(context, logger).ListRepoSkillsAsync(ghPath, repo,
                    version, options.AllowHiddenDirs, token, options.ProcessEnvironment, options.WorkingDirectory, options.FromLocal).ConfigureAwait(false);
                if (!listing.Succeeded) throw new IOException("Exact materialization names are unknown: " + listing.ErrorMessage);
                selectors = GhSkillInstallService.ResolveWriteSelectors(listing.Skills, requested, false);
                if (selectors.Count != 1) throw new IOException("Exact materialization name is unresolved or ambiguous.");
                locations.AppendLine("Named-skill destinations from the current catalog/listing:");
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = Path.Combine(root, selectors[0].Split('/')[^1]);
                    locations.AppendLine(destination);
                    // Existing objects, including dangling links, are only observations.
                    try
                    {
                        _ = File.GetAttributes(destination);
                        locations.AppendLine("  Existing object: conflict; inspect before installing.");
                    }
                    catch (FileNotFoundException) { locations.AppendLine("  No object observed at this address; admission rechecks."); }
                    catch (DirectoryNotFoundException) { locations.AppendLine("  No object observed at this address; admission rechecks."); }
                }
            }
            else text.AppendLine("Exact target set unknown (ALL, upstream or unnamed request). No per-skill paths or conflict-free result can be promised.");
            try
            {
                var ownership = GhInstallOwnershipGuard.Capture(roots, context, options, logger, token);
                GhInstallOwnershipGuard.Validate(ownership, roots, selectors, token);
                GhInstallOwnershipGuard.RequireVacantForceDestinations(roots, options, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { text.AppendLine("Ownership / managed conflict: " + ex.Message); }
            var home = context.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", options.ProcessEnvironment);
            if (home is not null) home = Path.GetFullPath(home, context.EffectiveWorkingDirectory);
            var registry = GhSharedLockGuard.ValidateCurrent(token, home);
            if (selectors?.Any(registry.ContainsKey) == true)
                text.AppendLine("Conflict: an existing shared selector would be replaced. Use a supported saved operation; installation stays refused.");
            else text.AppendLine("This observation cannot promise absence of conflicts. Installation rechecks shared records, managed resources and owners immediately before launch.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { text.AppendLine("Conflict / unknown: " + ex.Message); }
        text.AppendLine("Guidance only. Changed context, source or files require fresh install admission; no overwrite/removal/retargeting is approved here.");
        return new(InstallFailureDetails.Literal(text.ToString() + "\n" + locations, 64 * 1024), roots, InstallNavigationEvidence.Context(context));
    }
}
