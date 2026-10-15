using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Loading;

/// Owned callers await capture; only the frozen result may reach the UI.
/// This service never starts an AI client or executes skill content.
internal sealed class EffectiveSkillLoader(ProcessRunner runner)
{
    internal async Task<EffectiveSkillAnalysis> CaptureInventoryAsync(LocalInventoryService inventoryService, string? ghPath,
        LoadingCaptureRequest request, IReadOnlyList<string> scanRoots, IReadOnlyList<string> workspaces,
        IReadOnlyList<ScanRootResolver.AgentProfileRoot> profiles, CancellationToken token)
    {
        var configuration = await Task.Run(() => new LoadingConfigurationCapture(runner).Capture(request, token), token).ConfigureAwait(false);
        var discovery = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            string[] supplemental = configuration.Context.AgentId switch
            {
                "claude-code" => [Path.Combine(configuration.Context.ConfigDirectory, "skills"), Path.Combine(configuration.SystemDirectory, ".claude", "skills")],
                "gemini-cli" => [Path.Combine(configuration.Context.ConfigDirectory, "skills"), Path.Combine(configuration.Context.HomeDirectory, ".agents", "skills")],
                "codex" => [Path.Combine(configuration.Context.ConfigDirectory, "skills"), Path.Combine(configuration.Context.HomeDirectory, ".agents", "skills"), Path.Combine(configuration.SystemDirectory, "skills")],
                _ => []
            };
            return LoadingDiscoveryCapture.Probe(supplemental
                .Concat(configuration.GeminiExtensions.Select(extension => Path.Combine(extension.PayloadDirectory, "skills")))
                .Concat(configuration.DiscoveryRoots.Where(root => root.Applicability != "excluded").Select(root => root.Path)), token);
        }, token).ConfigureAwait(false);
        var roots = scanRoots.Concat(discovery.Roots).Distinct(StringComparer.Ordinal).ToArray();
        var contexts = workspaces.Concat(new[] { configuration.Context.ProjectDirectory, configuration.Context.WorkingDirectory }).Distinct(StringComparer.Ordinal).ToArray();
        token.ThrowIfCancellationRequested();
        var selectedEnvironment = configuration.Context.AgentId switch
        {
            "claude-code" => new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configuration.Context.ConfigDirectory },
            "codex" => new Dictionary<string, string> { ["CODEX_HOME"] = configuration.Context.ConfigDirectory },
            _ => new Dictionary<string, string>()
        };
        var selectedRunner = runner.WithContext(configuration.Context.WorkingDirectory, selectedEnvironment);
        var inventory = await inventoryService.InContext(selectedRunner).CaptureAsync(ghPath,
            new(roots, configuration.Context.AgentId == "opencode", WorkspaceDirectories: contexts, Profiles: profiles)
            { DiscoverNestedSkillFiles = configuration.Context.AgentId == "opencode" }, token).ConfigureAwait(false);
        return await Task.Run(() => Capture(discovery.Apply(inventory), configuration, token), token).ConfigureAwait(false);
    }

    internal Task<EffectiveSkillAnalysis> CaptureAsync(InventorySnapshot inventory, LoadingCaptureRequest request,
        CancellationToken token) => Task.Run(() =>
    {
        var configuration = new LoadingConfigurationCapture(runner).Capture(request, token);
        return Capture(inventory, configuration, token);
    }, token);

    internal static EffectiveSkillAnalysis Capture(InventorySnapshot inventory, CapturedLoadingConfiguration configuration,
        CancellationToken token, NativeLoadingObservation? native = null)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.DiscoveryLimitations.IsEmpty) inventory = new LoadingRootObservation([], configuration.DiscoveryLimitations
            .Select(reason => new DiscoveryLocation(configuration.Context.ConfigDirectory, "unsupported", reason)).ToImmutableArray()).Apply(inventory);
        var metadata = new LoadingConfigurationReader(8192, 16 * 1024 * 1024);
        var observations = new Dictionary<string, (LoadingConfigurationDocument Header, LoadingConfigurationDocument? OpenAi)>(StringComparer.Ordinal);
        var pluginNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        var evidence = configuration.Settings.Evidence.ToBuilder();
        foreach (var region in inventory.ManagedRegions.Where(region => AgentIdentity.Matches(region.AgentId, configuration.Context.AgentId)
            && region.Provider is "claude-plugin" or "codex-plugin" && region.Kind is "plugin" or "registered plugin" or "synced plugin"))
        {
            token.ThrowIfCancellationRequested();
            if (pluginNames.ContainsKey(region.Path)) continue;
            var manifest = metadata.Read(Path.Combine(region.Path, region.Provider == "claude-plugin" ? ".claude-plugin" : ".codex-plugin", "plugin.json"),
                "plugin invocation namespace observation", token, format: LoadingContentFormat.Json);
            evidence.Add(manifest.Evidence);
            pluginNames[region.Path] = manifest.Root?.Get("name") is { Kind: "string", Text: { Length: > 0 } value }
                && !value.Any(character => char.IsControl(character) || character is ':' or '/' or '\\') ? value : null;
        }
        foreach (var skill in inventory.Skills)
        {
            token.ThrowIfCancellationRequested();
            var header = metadata.Read(Path.Combine(skill.ResolvedPath, "SKILL.md"), "bounded skill frontmatter", token, frontMatter: true);
            var openAi = configuration.Context.AgentId == "codex"
                ? metadata.Read(Path.Combine(skill.ResolvedPath, "agents", "openai.yaml"), "Codex invocation policy", token, format: LoadingContentFormat.Yaml) : null;
            observations[skill.ResolvedPath] = (header, openAi);
            evidence.Add(header.Evidence);
            if (openAi is not null) evidence.Add(openAi.Evidence);
        }
        var original = configuration.Context;
        var registrationFacts = inventory.ManagedRegions.Select(region => SkillDistributorIdentity.Digest(region.Path, region.ObservedCanonicalPath,
                region.Provider, region.AgentId, region.Kind, region.PluginId, region.PackageVersion, region.Context,
                region.RegistrationPath, region.RegistrationScope?.ToString())).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = SkillDistributorIdentity.Digest(["effective-loading-v1", original.ConfigurationFingerprint,
            registrationFacts.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), .. registrationFacts,
            evidence.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. evidence.OrderBy(file => file.Path, StringComparer.Ordinal).SelectMany(file => new[] { file.Path, file.State, file.Sha256 })]);
        var context = SkillLoadingContext.Capture(original.AgentId, original.Profile, original.ProjectDirectory,
            original.WorkingDirectory, original.HomeDirectory, original.ConfigDirectory, original.ClientVersion,
            original.AgentName, original.ProjectTrust, fingerprint, original.RuleRevision);
        var rules = AgentLoadingRules.Find(context.AgentId);
        native ??= AgentLoadingRules.NativeAvailability(context);
        var diagnostics = configuration.Diagnostics.ToBuilder();
        diagnostics.AddRange(configuration.Settings.UnobservedSources);
        diagnostics.AddRange(configuration.Settings.LocalPolicyBlockers);
        diagnostics.AddRange(configuration.DiscoveryLimitations);
        foreach (var location in inventory.Diagnostics.InaccessiblePaths) diagnostics.Add("Inventory location unobserved: " + location);
        foreach (var provider in inventory.Providers.Where(provider => !provider.Succeeded))
            diagnostics.Add("Inventory provider " + provider.Provider + " incomplete: " + (provider.Error ?? "unknown error"));
        foreach (var location in inventory.Diagnostics.Locations.Where(location => location.State == "unsupported"))
            diagnostics.Add("Inventory coverage limitation: " + location.Path + ": " + location.Reason);
        if (!inventory.ConfiguredRootsComplete) diagnostics.Add("Configured discovery roots are incomplete; omitted higher-tier candidates remain possible.");
        diagnostics.Add("Expected states are a conditional local-file policy preview under the cited rules; client applicability and active-session state are separate.");
        if (native.ContextId != context.ContextId || native.Provider != context.AgentId
            || native.ClientVersion != context.ClientVersion || native.State == "observed" && (native.CapturedAt is null || native.Schema == "none"))
        {
            diagnostics.Add("Native inventory context/version/schema did not match this capture; it was not used for candidate conclusions.");
            native = NativeLoadingObservation.Unavailable(context, "Rejected mismatched or incomplete native observation.");
        }
        var rows = new List<(EffectiveSkillCandidate Candidate, int Rank, string Family)>();
        foreach (var skill in inventory.Skills)
        {
            token.ThrowIfCancellationRequested();
            var subject = OperationSubject.From(skill);
            var observed = observations[skill.ResolvedPath];
            var exposures = subject.Exposures.IsEmpty ? skill.Agents.Select(member => new ObservedSkillExposure(member, null)).ToImmutableArray() : subject.Exposures;
            var entries = exposures.GroupBy(exposure => PathIdentity.NormalizeKey(exposure.Membership.Path), StringComparer.Ordinal)
                .Select(group => (Path: group.First().Membership.Path, Exposures: group.ToImmutableArray())).ToArray();
            if (entries.Length == 0) entries = [(skill.ResolvedPath, ImmutableArray<ObservedSkillExposure>.Empty)];
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var placement = Placement(context, configuration.SystemDirectory, inventory, entry.Path, entry.Exposures, pluginNames, configuration.GeminiExtensions, configuration.DiscoveryRoots, token);
                var name = observed.Header.Root?.Get("name") is { Kind: "string", Text: { Length: > 0 } declared }
                    ? declared : Path.GetFileName(entry.Path);
                var directoryName = Path.GetFileName(entry.Path);
                var aliases = ImmutableArray<string>.Empty;
                if (context.AgentId == "claude-code" && placement.Kind != "managed-plugin")
                {
                    if (name != directoryName) aliases = [name];
                    name = directoryName;
                }
                if (context.AgentId == "gemini-cli") name = Regex.Replace(name, "[:\\\\/<>*?\"|]", "-", RegexOptions.NonBacktracking);
                var invocation = placement.Namespace is { } ns ? name.StartsWith(ns + ":", StringComparison.Ordinal) ? name : ns + ":" + name : name;
                if (context.AgentId == "claude-code" && placement.Kind == "nested-project")
                    invocation = Path.GetRelativePath(context.WorkingDirectory, placement.Root!).Replace('\\', '/') + ":" + name;
                var expected = Expectation(context, configuration.Settings, skill, observed.Header, observed.OpenAi,
                    invocation, entry.Path, placement);
                if (!aliases.IsEmpty) expected = expected with
                {
                    Selection = "directory-command;alias-conflicts-unverified",
                    Reasons = expected.Reasons.Add("The directory command remains distinct from a frontmatter alias; alias collision/override behavior requires separate command resolution.")
                };
                var nativeEntries = native.State == "observed" ? native.Entries.Where(item =>
                    PathIdentity.Equals(item.Path, entry.Path) || PathIdentity.Equals(item.Path, Path.Combine(entry.Path, "SKILL.md"))).ToImmutableArray() : [];
                var candidate = new EffectiveSkillCandidate(SkillDistributorIdentity.Digest(context.ContextId, subject.InstallationId,
                    PathIdentity.NormalizeKey(entry.Path), invocation), subject, entry.Exposures, invocation, placement.Kind,
                    expected, nativeEntries, [], skill.FrontMatter.Diagnostics)
                {
                    EntryPath = entry.Path,
                    InvocationAliases = aliases,
                    GeminiExtensions = context.AgentId == "gemini-cli" ? configuration.GeminiExtensions.Where(extension =>
                        GeminiExtensionCovers(extension, entry.Path)).ToImmutableArray() : []
                };
                rows.Add((candidate, placement.Rank, placement.Family));
            }
        }
        foreach (var group in rows.Select((row, index) => (row, index)).GroupBy(value =>
            (value.row.Family, value.row.Candidate.InvocationName)))
        {
            token.ThrowIfCancellationRequested();
            var discoverable = group.Where(value => value.row.Rank > 0 && value.row.Candidate.Expected.Discoverability is "expected" or "unknown").ToArray();
            if (discoverable.Length < 2) continue;
            if (context.AgentId == "codex")
            {
                foreach (var value in discoverable) Replace(value.index, "coexists", false, "Codex does not select one same-name skill by scope; retain each installation.");
            }
            else if (context.AgentId == "opencode")
            {
                foreach (var value in discoverable) Replace(value.index, "ambiguous-same-name", false, "The cited OpenCode documentation does not establish cross-root same-name precedence.");
            }
            else
            {
                var highestKnown = discoverable.Where(value => value.row.Candidate.Expected.Discoverability == "expected")
                    .Select(value => value.row.Rank).DefaultIfEmpty(-1).Max();
                var highestUnknown = discoverable.Where(value => value.row.Candidate.Expected.Discoverability == "unknown")
                    .Select(value => value.row.Rank).DefaultIfEmpty(-1).Max();
                var rankCounts = discoverable.GroupBy(value => value.row.Rank).ToDictionary(rank => rank.Key, rank => rank.Count());
                foreach (var value in discoverable)
                {
                    var knownHigher = highestKnown > value.row.Rank;
                    var uncertainWinner = inventory.IsPartial || !inventory.ConfiguredRootsComplete || highestUnknown >= value.row.Rank;
                    var tied = rankCounts[value.row.Rank] > 1;
                    Replace(value.index, knownHigher ? "shadowed" : uncertainWinner ? "conditional-selection" : tied ? "ambiguous-same-name" : "selected",
                        knownHigher, "Same-name selection follows the selected agent's cited tier rules; uncertainty cannot establish definite shadowing.");
                }
            }
        }
        var commands = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Rank == 0) continue;
            foreach (var name in rows[index].Candidate.InvocationAliases.Add(rows[index].Candidate.InvocationName ?? "").Distinct(StringComparer.Ordinal))
            {
                if (!commands.TryGetValue(name, out var indices)) commands[name] = indices = [];
                indices.Add(index);
            }
        }
        for (var index = 0; index < rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[index]; var candidate = row.Candidate;
            var names = candidate.InvocationAliases.Add(candidate.InvocationName ?? "").Distinct(StringComparer.Ordinal).ToArray();
            var peerIndices = row.Rank == 0 ? [] : names.SelectMany(name => commands[name].Take(18)).Where(other => other != index).Distinct().Take(17).ToArray();
            var peers = peerIndices.Take(16).Select(other => rows[other].Candidate.LoadingKey).ToImmutableArray();
            var coverage = peerIndices.Length > 16 || row.Rank > 0 && names.Any(name => commands[name].Count > 17) ? "bounded-to-16; all candidates retained in analysis" : "complete";
            var expected = candidate.Expected;
            if (expected.Selection == "candidate" && (inventory.IsPartial || !inventory.ConfiguredRootsComplete))
                expected = expected with
                {
                    Selection = "conditional-selection",
                    ManualInvocation = UnknownIfPossible(expected.ManualInvocation),
                    AutomaticInvocation = UnknownIfPossible(expected.AutomaticInvocation),
                    SlashInvocation = UnknownIfPossible(expected.SlashInvocation),
                    Reasons = expected.Reasons.Add("Discovery coverage is incomplete; an omitted competing definition is possible.")
                };
            var disagreements = ImmutableArray.CreateBuilder<string>();
            if (candidate.NativeObservations.Any(item => item.Enabled == true) && expected.State is "disabled" or "shadowed" or "incompatible" or "broken")
                disagreements.Add("Native inventory reports enabled while the final local-file policy preview reports " + expected.State + ".");
            if (candidate.NativeObservations.Any(item => item.Enabled == false)
                && (expected.ManualInvocation == "yes" || expected.AutomaticInvocation == "yes" || expected.SlashInvocation == "yes"))
                disagreements.Add("Native inventory reports disabled while the final local-file policy permits invocation.");
            rows[index] = (candidate with { Expected = expected, Disagreements = disagreements.ToImmutable(), SelectionPeers = peers, SelectionPeerCoverage = coverage }, row.Rank, row.Family);
        }
        return new(context, rules, rules?.Applicability(context.ClientVersion) ?? "unsupported-agent", DateTimeOffset.UtcNow,
            !inventory.IsPartial && inventory.ConfiguredRootsComplete && !configuration.Settings.HasUncertainLocalPolicy
                && configuration.Settings.UnobservedSources.IsEmpty && native.State == "observed" && native.Coverage == "complete-selected-context"
                && evidence.All(file => file.State is "observed" or "missing"),
            rows.Select(row => row.Candidate).ToImmutableArray(), evidence.ToImmutable(), native, diagnostics.ToImmutable())
        { DiscoveryRoots = configuration.DiscoveryRoots };

        void Replace(int index, string selection, bool shadowed, string reason)
        {
            var row = rows[index];
            var expected = row.Candidate.Expected with { Selection = selection, Reasons = row.Candidate.Expected.Reasons.Add(reason) };
            if (shadowed && row.Candidate.InvocationAliases.IsEmpty) expected = expected with
            { State = expected.State == "disabled" ? "disabled" : "shadowed", ManualInvocation = "no", AutomaticInvocation = "no", SlashInvocation = "no" };
            if (shadowed && !row.Candidate.InvocationAliases.IsEmpty && expected.State != "disabled") expected = expected with
            { Selection = "directory-shadowed;alias-resolution-unknown", ManualInvocation = UnknownIfPossible(expected.ManualInvocation), AutomaticInvocation = UnknownIfPossible(expected.AutomaticInvocation), SlashInvocation = UnknownIfPossible(expected.SlashInvocation) };
            if (selection is "conditional-selection" or "ambiguous-same-name" && expected.State != "disabled") expected = expected with
            { ManualInvocation = UnknownIfPossible(expected.ManualInvocation), AutomaticInvocation = UnknownIfPossible(expected.AutomaticInvocation), SlashInvocation = UnknownIfPossible(expected.SlashInvocation) };
            rows[index] = (row.Candidate with { Expected = expected }, row.Rank, row.Family);
        }
    }

    private sealed record PlacementRule(string Kind, string Family, int Rank, string? Root = null, string? Namespace = null,
        string? DistributorId = null, bool Registered = false, GeminiExtensionExpectation? Extension = null, string? DiscoveryRestriction = null);

    private static PlacementRule Placement(SkillLoadingContext context, string system, InventorySnapshot inventory,
        string entry, ImmutableArray<ObservedSkillExposure> exposures, IReadOnlyDictionary<string, string?> pluginNames,
        ImmutableArray<GeminiExtensionExpectation> extensions, ImmutableArray<LoadingDiscoveryRoot> discoveryRoots, CancellationToken token)
    {
        if (exposures.Length > 0 && exposures.All(exposure => exposure.Membership.Profile is { } profile && profile != context.Profile))
            return new("other-profile", "outside", 0);
        var matchedOtherProjectPlugin = false;
        foreach (var region in inventory.ManagedRegions.Where(region => AgentIdentity.Matches(region.AgentId, context.AgentId)
            && region.Provider is "claude-plugin" or "codex-plugin" && region.Kind is "registered plugin" or "plugin" or "synced plugin")
            .OrderByDescending(region => region.Path.Length).ThenByDescending(region => region.Kind == "registered plugin"))
        {
            token.ThrowIfCancellationRequested();
            if (!PathIdentity.IsInside(entry, region.Path) && !(region.ObservedCanonicalPath is { } canonical && PathIdentity.IsInside(entry, canonical))) continue;
            if (region.RegistrationScope == Scope.Project && region.Context is { } project && !PathIdentity.IsInside(context.WorkingDirectory, project))
            { matchedOtherProjectPlugin = true; continue; }
            var name = pluginNames.GetValueOrDefault(region.Path);
            if (context.AgentId == "claude-code" && name is null) return new("plugin-namespace-unobserved", "outside", 0);
            return new("managed-plugin", context.AgentId == "claude-code" ? "plugin:" + name : "ordinary", 1, region.Path,
                context.AgentId == "claude-code" ? name : null, region.PluginId, region.Kind == "registered plugin" && region.RegistrationScope is not null);
        }
        if (matchedOtherProjectPlugin) return new("other-project-plugin", "outside", 0);
        bool Under(string path) => !PathIdentity.Equals(entry, path) && PathIdentity.IsInside(entry, path);
        if (context.AgentId == "claude-code")
        {
            var personal = Path.Combine(context.ConfigDirectory, "skills");
            if (PathIdentity.IsInside(entry, personal) && Path.GetRelativePath(personal, entry).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals("synced", StringComparison.OrdinalIgnoreCase))
                return new("account-cache-unobserved", "outside", 0);
            if (Under(Path.Combine(system, ".claude", "skills"))) return new("enterprise", "ordinary", 30);
            if (Under(personal)) return new("personal", "ordinary", 20);
            foreach (var directory in LoadingConfigurationCapture.Ancestors(context.WorkingDirectory, context.ProjectDirectory, token))
                if (Under(Path.Combine(directory, ".claude", "skills"))) return new("project", "ordinary", 10);
            // Nested definitions can coexist; their activation can depend on
            // files accessed in the current session, which remains unknown.
            var nested = exposures.Select(exposure => exposure.Membership.Context).OfType<string>()
                .FirstOrDefault(root => PathIdentity.IsInside(root, context.WorkingDirectory) && !PathIdentity.Equals(root, context.WorkingDirectory)
                    && Under(Path.Combine(root, ".claude", "skills")));
            if (nested is not null) return new("nested-project", "nested:" + nested, 10, nested);
        }
        else if (context.AgentId == "gemini-cli")
        {
            bool Shallow(string root) => PathIdentity.IsInside(entry, root) && Path.GetRelativePath(root, entry).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 1;
            if (Shallow(Path.Combine(context.WorkingDirectory, ".agents", "skills"))) return new("workspace-agents", "ordinary", 41);
            if (Shallow(Path.Combine(context.WorkingDirectory, ".gemini", "skills"))) return new("workspace-gemini", "ordinary", 40);
            if (Shallow(Path.Combine(context.HomeDirectory, ".agents", "skills"))) return new("user-agents", "ordinary", 21);
            if (Shallow(Path.Combine(context.ConfigDirectory, "skills"))) return new("user-gemini", "ordinary", 20);
            var deliveries = extensions.Where(extension => GeminiExtensionCovers(extension, entry)).ToArray();
            if (deliveries.Length > 0) return new("gemini-extension", "ordinary", 10, Extension: deliveries.Length == 1 ? deliveries[0]
                : deliveries[0] with { Enabled = null, Reasons = ["Multiple extension deliveries expose this physical entry; each saved enablement is retained, and runtime selection is unobserved."] });
        }
        else if (context.AgentId == "opencode")
        {
            var matches = discoveryRoots.Where(root => root.Covers(entry)).ToArray();
            var applicable = matches.FirstOrDefault(root => root.Applicability == "applied");
            if (applicable is not null) return new(applicable.Placement, "ordinary", 1, applicable.Path);
            var conditional = matches.FirstOrDefault(root => root.Applicability == "conditional");
            if (conditional is not null) return new("conditional-discovery-root", "ordinary", 1, DiscoveryRestriction: conditional.Reason);
            if (matches.Length > 0) return new("excluded-discovery-root", "outside", 0, DiscoveryRestriction: matches[0].Reason);
        }
        else if (context.AgentId == "codex")
        {
            var roots = new[] { Path.Combine(context.HomeDirectory, ".agents", "skills"),
                Path.Combine(context.ConfigDirectory, "skills", ".system"), Path.Combine(system, "skills") };
            if (roots.Any(Under)) return new("user-or-system", "ordinary", 1);
            foreach (var directory in LoadingConfigurationCapture.Ancestors(context.WorkingDirectory, context.ProjectDirectory, token))
                if (Under(Path.Combine(directory, ".agents", "skills"))) return new("project", "ordinary", 1);
        }
        return new("outside-documented-roots", "outside", 0);
    }

    private static ExpectedSkillLoading Expectation(SkillLoadingContext context, LoadingSettings settings, InstalledSkill skill,
        LoadingConfigurationDocument header, LoadingConfigurationDocument? openAi, string invocation, string entry, PlacementRule placement)
    {
        if (skill.Validity == ValidityState.BrokenSymlink || header.Evidence.State == "missing")
            return new("broken", "no", "no", "no", "not-applicable", "not-discoverable", "missing-payload-or-metadata", "repair-required", ["No readable skill payload/header was observed at this candidate."]) { SlashInvocation = "no" };
        if (header.Root is null || openAi is { Evidence.State: not ("observed" or "missing") })
            return ExpectedSkillLoading.Unknown("Skill invocation metadata is unreadable, unsupported or beyond capture bounds.");
        if (context.AgentId == "claude-code" && placement.Kind != "managed-plugin"
            && (Path.GetFileName(entry).Equals("synced", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry) == "anthropic-skills" || Path.GetFileName(entry).StartsWith("anthropic-skills:", StringComparison.Ordinal)))
            return new("incompatible", "no", "no", "no", "not-applicable", "reserved-name", "documented-reserved-ordinary-name", "rename-and-reload",
                ["Claude reserves this ordinary directory name for synced/account skill delivery."])
            { SlashInvocation = "no" };
        if (placement.Kind == "excluded-discovery-root") return new("disabled", "no", "no", "no", "not-applicable", "excluded-discovery-root",
            "not-proven-incompatible", "client-reload-session-unknown", [placement.DiscoveryRestriction ?? "Provider configuration excludes this discovery root."])
        { SlashInvocation = "no" };
        if (placement.Kind == "conditional-discovery-root") return ExpectedSkillLoading.Unknown(placement.DiscoveryRestriction ?? "Discovery root applicability is unknown.");
        if (placement.Rank == 0) return ExpectedSkillLoading.Unknown("This entry lies outside the selected profile/project's documented discovery paths.") with { Discoverability = "not-by-default", Selection = "outside-context" };
        if (context.AgentId == "gemini-cli" && (header.Root.Get("name")?.Kind != "string" || header.Root.Get("description")?.Kind != "string"))
            return ExpectedSkillLoading.Unknown("Gemini requires name/description; its fallback parser has not been modeled for this metadata, so eligibility is unproven.");
        if (context.AgentId == "opencode")
        {
            var name = header.Root.Get("name"); var description = header.Root.Get("description");
            if (name?.Kind != "string" || description is { Kind: not "string" })
                return ExpectedSkillLoading.Unknown("OpenCode native metadata acceptance is unproven for this name/description shape; portable lint alone cannot establish refusal.");
        }
        if (context.AgentId == "gemini-cli" && placement.Kind.StartsWith("workspace-", StringComparison.Ordinal) && settings.ExpectedProjectTrust != "trusted")
            return ExpectedSkillLoading.Unknown("Gemini workspace discovery requires trust; no effective trusted context was established.") with
            { State = settings.ExpectedProjectTrust == "untrusted" ? "trust-blocked" : "unknown", Discoverability = settings.ExpectedProjectTrust == "untrusted" ? "no" : "unknown" };
        var policy = LoadingPolicyEvaluator.Evaluate(context, settings, invocation, entry, header.Root, openAi?.Root,
            placement.Kind == "managed-plugin", placement.Kind == "enterprise");
        if (placement.Kind == "managed-plugin")
        {
            var enabled = context.AgentId == "claude-code" && placement.DistributorId is { } distributor
                ? settings.Merge(context.AgentId).Get("enabledPlugins", distributor) : null;
            if (enabled?.Boolean == false) policy = policy with
            { Enabled = false, Manual = false, Automatic = false, Slash = false, Reasons = policy.Reasons.Add("Local enabledPlugins configuration disables this distributor; its namespace is a separate manifest observation.") };
            else if (enabled?.Boolean != true || !placement.Registered) policy = policy with
            { Enabled = null, Manual = null, Automatic = null, Slash = null, Reasons = policy.Reasons.Add("Cached plugin presence/namespace does not establish selected delivery or configured enablement in this context.") };
        }
        if (placement.Extension is { } extension)
        {
            var merged = settings.Merge(context.AgentId);
            var security = merged.Get("security");
            var uncertainSecurity = security?.Get("allowedExtensions") is { } allowed && (allowed.Kind != "array" || !allowed.Items.IsEmpty)
                || security?.Get("blockGitExtensions") is { } blocked && (blocked.Kind != "boolean" || blocked.Boolean == true);
            if (extension.Enabled == false) policy = policy with
            {
                Enabled = false,
                Manual = false,
                Automatic = false,
                Slash = false,
                Reasons = policy.Reasons.AddRange(extension.Reasons).Add("Saved extension enablement disables this delivery in the selected workspace.")
            };
            else if (extension.Enabled is null || uncertainSecurity || header.Root.Get("name")?.Text?.Contains('$') == true)
                policy = LoadingInvocationPolicy.Unknown("Extension eligibility requires unresolved manifest/hydration/security or saved enablement evidence.") with
                { Reasons = extension.Reasons.Add("No extension initialization or variable expansion was performed.") };
            else policy = policy with { Reasons = policy.Reasons.AddRange(extension.Reasons) };
        }
        if (placement.Kind == "nested-project") return ExpectedSkillLoading.Unknown("Nested Claude discovery depends on active-session file access/additional roots; observed files alone do not establish invocation availability.") with
        { Selection = "coexists-session-activation-unknown" };
        var state = policy.Enabled == false ? "disabled" : policy.Enabled is null ? "unknown"
            : policy.Manual == true && policy.Automatic == false ? "manual-only" : "eligible";
        if (context.AgentId == "opencode" && header.Root.Get("description") is null && policy.Enabled == true)
        { policy = policy with { Automatic = false, Reasons = policy.Reasons.Add("OpenCode 1.19.1 can retain a name without a description for exact requests while omitting it from model-facing guidance.") }; state = "manual-only"; }
        return new(state, placement.Extension?.Enabled == false ? "no" : policy.Enabled is null ? "unknown" : "expected", Flag(policy.Manual), Flag(policy.Automatic), policy.Approval,
            placement.Kind == "nested-project" ? "coexists-session-activation-unknown" : "candidate", "not-proven-incompatible",
            context.AgentId == "codex" ? "restart-after-config-change" : "client-reload-session-unknown", policy.Reasons)
        { SlashInvocation = Flag(policy.Slash) };
    }

    private static bool GeminiExtensionCovers(GeminiExtensionExpectation extension, string entry)
    {
        var root = Path.Combine(extension.PayloadDirectory, "skills");
        return PathIdentity.IsInside(entry, root) && Path.GetRelativePath(root, entry).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 1;
    }

    private static string Flag(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";
    private static string UnknownIfPossible(string value) => value == "no" ? "no" : "unknown";
}
