using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Preferences;

namespace SkillView.Maintenance;

internal enum FindingClass { Problem, Update, Review, Information, Coverage }
internal enum FindingNavigation { InspectCopies, ReviewCleanup, ReviewUpdate, InspectRegistry }

// A registry record is deliberately not an OperationSubject. These observations
// grant navigation only; every specialist operation still needs fresh admission.
internal abstract record FindingSubject;
internal sealed record FindingCopies(ImmutableArray<OperationSubject> Copies) : FindingSubject;
internal sealed record FindingRegistry(SkillPackage Record) : FindingSubject;

internal sealed record CapturedFinding(string Id, string Kind, string Title, FindingClass Classification,
    string Confidence, string Explanation, FindingSubject Subject, DateTimeOffset CapturedAt, bool Partial,
    ImmutableArray<FindingNavigation> Navigation)
{
    internal ImmutableArray<MaintenanceQueueEntry> Entries { get; init; } = [];
    internal DifferenceBinding? Difference { get; init; }
    internal bool Intentional { get; init; }
    internal string PreferenceVersion { get; init; } = "";
    internal string? PreferenceCapacity { get; init; }
    internal ImmutableArray<(string Label, string? Value)> Evidence { get; init; } = [];
}

/// Builds once off the UI thread from frozen inventory/queue facts. Preference
/// path keys use filesystem-sensitive policy; no payload capture or remote query.
internal static class CapturedFindingBuilder
{
    internal static ImmutableArray<CapturedFinding> Build(InventorySnapshot snapshot, MaintenanceQueue queue, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<CapturedFinding>();
        var partial = queue.Partial;
        foreach (var entry in queue.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Issue is "installation-review" or "Untracked" || entry.IsCopyReview) continue;
            var problem = entry.Issue is "Malformed" or "MetadataConflict" or "BrokenSymlink" or "BrokenSharedMapping" or "EmptyDirectory";
            result.Add(new(entry.Id, entry.Issue, entry.Title, problem ? FindingClass.Problem : FindingClass.Review,
                entry.Confidence, entry.Reason, new FindingCopies([entry.Subject]), snapshot.CapturedAt, partial,
                [entry.Cleanup is null ? FindingNavigation.ReviewUpdate : FindingNavigation.ReviewCleanup])
            { Entries = [entry] });
        }

        var captured = snapshot.Skills.Select(skill => (Skill: skill, Subject: OperationSubject.From(skill))).ToArray();
        var installations = queue.Entries.Where(entry => entry.Issue == "installation-review")
            .ToDictionary(entry => (entry.Subject.InstallationId, entry.Subject.Path));
        var records = captured.GroupBy(row => (row.Subject.InstallationId, row.Subject.Path)).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var named in captured.GroupBy(row => row.Skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            // Inventory has already reconciled native aliases. Repeated addresses
            // remain explicit ambiguous evidence, never extra physical copies.
            var members = named.OrderBy(row => row.Subject.Path, StringComparer.Ordinal).ToArray();
            if (members.Select(row => row.Subject.InstallationId).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var groups = members.GroupBy(row => row.Subject.Source.Key ?? "unknown:" + row.Subject.InstallationId, StringComparer.Ordinal).ToArray();
            foreach (var source in groups)
            {
                var copies = source.ToArray();
                if (copies.Select(row => row.Subject.InstallationId).Distinct(StringComparer.Ordinal).Count() < 2) continue;
                var revision = RecordedRevisionComparison.Observe(copies.Select(row => row.Skill));
                var methods = copies.Select(row => Method(row.Subject)).Distinct(StringComparer.Ordinal).Count() > 1;
                if (!revision.Different && !methods)
                {
                    var repository = copies.All(row => row.Subject.Source.Provider == "repository");
                    AddFamily(copies.Select(row => row.Subject).ToImmutableArray(), "same-source-copies",
                        repository && revision.Incomplete ? FindingClass.Review : FindingClass.Information,
                        !repository ? "Same recorded source, multiple independent physical copies. Repository revision comparison does not apply to this source. Local files have not been compared."
                            : revision.Incomplete ? "Same recorded source, multiple independent physical copies. Revision evidence is unknown or incomplete; this is not a clean/no-issue conclusion. Compare the captured files explicitly."
                            : "Same recorded source and matching comparable recorded revision evidence, multiple independent physical copies. No installation-method difference was observed; absent owner/method evidence remains unknown. Matching records do not prove identical or redundant payloads. Compare the captured files explicitly.");
                    continue;
                }
                AddFamily(copies.Select(row => row.Subject).ToImmutableArray(),
                    revision.Different ? "recorded-revisions" : "installation-methods",
                    revision.Different ? FindingClass.Review : FindingClass.Information,
                    (revision.Different ? "Different recorded revisions of the same source; neither is assumed newer. " : "")
                    + (methods ? "Installation methods differ; this alone is not an error. " : "")
                    + (revision.Incomplete ? "Some revision evidence is unknown or conflicting. " : "")
                    + "Local files have not been compared. Updates require an explicit availability check.");
            }
            if (groups.Length > 1)
                AddFamily(members.Select(row => row.Subject).ToImmutableArray(), "source-differences", FindingClass.Review,
                    "Same name with different or unknown sources. Copies may be intentional project overrides; keep their sources and locations separate. Names do not establish redundant content.");
        }

        // Match only the captured contextual claims. No same-name fallback and
        // no scan of unconfigured projects can establish worldwide absence.
        foreach (var record in snapshot.OwnershipRecords.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (record.Provider != "gh-vercel-global") continue;
            if (captured.Any(row => row.Skill.AllOwnershipClaims.Contains(record))) continue;
            var supported = record.SchemaVersion == 3 && !record.ReadOnly && record.Diagnostic is null
                && record.SourceType is "github" or "git" or "local"
                && record.Selector is { Length: > 0 } && record.LockFilePath is { Length: > 0 }
                && record.ContextRoot is { Length: > 0 } && record.EntryFingerprint is { Length: > 0 }
                && record.RecordFingerprint is { Length: > 0 } && snapshot.OwnershipReadDiagnostics.IsEmpty;
            var id = "registry:" + SkillDistributorIdentity.Digest(record.LockFilePath, record.ContextRoot, record.Selector,
                record.EntryFingerprint, record.RecordFingerprint);
            result.Add(new(id, supported ? "unmatched-gh-record" : "unknown-gh-record", record.Selector ?? "Unknown gh record",
                supported ? FindingClass.Review : FindingClass.Coverage, supported ? "captured-unmatched-record" : "protected-unknown",
                supported ? "Not matched to any observed copy in known locations. gh's shared record has no exclusive destination; custom-folder copies may exist elsewhere. This does not establish a stale, broken or orphaned record."
                    : "Registry evidence is ambiguous, diagnostic or unsupported and remains protected/unknown. No absence or removal conclusion is available.",
                new FindingRegistry(record), snapshot.CapturedAt, partial || !supported, [FindingNavigation.InspectRegistry])
            {
                Evidence = RegistryEvidence(snapshot, record)
            });
        }
        return result.ToImmutable();

        void AddFamily(ImmutableArray<OperationSubject> subjects, string kind, FindingClass classification, string explanation)
        {
            token.ThrowIfCancellationRequested();
            var entries = subjects.Select(subject => installations[(subject.InstallationId, subject.Path)]).ToImmutableArray();
            var diagnostics = subjects.SelectMany(subject => records[(subject.InstallationId, subject.Path)]
                .SelectMany(row => row.Skill.MetadataConflicts.Select(value => (Label: "Metadata conflict", Value: (string?)value))
                    .Concat(row.Skill.OwnershipDiagnostics.Select(value => (Label: "Owner coverage", Value: (string?)value)))))
                .ToImmutableArray();
            var binding = DifferenceBindingCapture.Capture(kind, subjects, token, diagnostics);
            var preferences = snapshot.IntentionalDifferences;
            var intentional = binding is not null && preferences.Active(binding);
            var id = "family:" + kind + ":" + SkillDistributorIdentity.Digest(subjects.Select(subject => subject.InstallationId).Distinct().Order(StringComparer.Ordinal).ToArray());
            result.Add(new(id, kind, subjects[0].Name ?? "Unknown skill", classification, kind == "same-source-copies" ? "captured-copies" : "captured-difference", explanation,
                new FindingCopies(subjects), snapshot.CapturedAt, partial || subjects.Any(subject => !subject.ExposureCoverageComplete),
                [FindingNavigation.InspectCopies])
            {
                Entries = entries,
                Difference = binding,
                Intentional = intentional,
                PreferenceVersion = preferences.Version,
                PreferenceCapacity = preferences.CapacityNotice,
                Evidence = diagnostics
            });
        }
    }

    internal static string Method(OperationSubject subject) => SkillDistributorIdentity.Digest(
        [subject.Materialization, string.Join(',', subject.Exposures.Select(exposure => exposure.Membership.IsSymlink).Distinct().Order()),
            .. subject.Owners.Select(owner => SkillDistributorIdentity.Digest(owner.Provider,
            owner.InstallationMode, owner.ReadOnly.ToString(), owner.IsExclusiveOwner.ToString())).Distinct().Order(StringComparer.Ordinal)]);

    private static ImmutableArray<(string Label, string? Value)> RegistryEvidence(InventorySnapshot snapshot, SkillPackage record) =>
        new (string, string?)[] { ("Selector", record.Selector), ("Source", record.Source), ("Source type", record.SourceType),
            ("Source URL", record.SourceUrl), ("Source path (recorded)", record.SkillPath), ("Registry", record.LockFilePath),
            ("Context", record.ContextRoot), ("Schema", record.SchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Entry fingerprint", record.EntryFingerprint), ("Registry fingerprint", record.RecordFingerprint), ("Diagnostic", record.Diagnostic),
            ("Capture", snapshot.CapturedAt.ToString("O")), ("Coverage", snapshot.IsPartial || !snapshot.ConfiguredRootsComplete ? "Partial; omitted or inaccessible locations/records may exist." : "Configured locations only; other custom copies may exist."),
            ("Configured roots complete", snapshot.ConfiguredRootsComplete.ToString()) }
        .Concat(record.CoveredRoots.Select(root => ("Record covered root", (string?)root)))
        .Concat(snapshot.ConfiguredRoots.Select(root => ("Configured location", (string?)root.Path)))
        .Concat(snapshot.ScannedRoots.Select(root => ("Scanned location", (string?)root.Path)))
        .Concat(snapshot.Diagnostics.Locations.Select(location => ("Location observation", (string?)$"{location.Path}: {location.State}: {location.Reason}")))
        .Concat(snapshot.Providers.Select(provider => ("Provider coverage", (string?)$"{provider.Provider}: success={provider.Succeeded}; {provider.Error}")))
        .Concat(snapshot.Providers.SelectMany(provider => provider.CoveredRoots.Select(root => ("Provider covered root", (string?)$"{provider.Provider}: {root}"))))
        .Concat(snapshot.OwnershipReadDiagnostics.Select(diagnostic => ("Registry coverage limitation", (string?)$"{diagnostic.Path}: {diagnostic.Message}")))
        .Concat(snapshot.Diagnostics.InaccessiblePaths.Select(path => ("Inaccessible", (string?)path))).ToImmutableArray();
}

internal static class RecordedRevisionComparison
{
    internal static (bool Different, bool Incomplete) Observe(IEnumerable<InstalledSkill> skills)
    {
        var captured = skills.ToArray();
        var eligible = captured.All(skill => skill.SourceIdentity is { Provider: "repository", Key: not null }
            && skill.MetadataConflicts.IsEmpty && skill.OwnershipDiagnostics.IsEmpty)
            && captured.Select(skill => skill.SourceIdentity.Key).Distinct(StringComparer.Ordinal).Count() == 1;
        var refs = captured.Select(skill => Evidence(skill, tree: false)).ToArray();
        var trees = captured.Select(skill => Evidence(skill, tree: true)).ToArray();
        // Compare within each domain only. Authored/distributor versions, pins
        // and Git trees cannot supply a missing repository ref.
        return (eligible && (Differs(refs) || Differs(trees)), !eligible || captured.Length == 0
            || refs.Any(string.IsNullOrWhiteSpace) && trees.Any(string.IsNullOrWhiteSpace));
    }

    private static string? Evidence(InstalledSkill skill, bool tree)
    {
        var values = skill.AllOwnershipClaims.Where(owner => !tree || owner.HashKind == "recorded-git-tree")
            .Select(owner => tree ? owner.ContentHash : owner.RepositoryRef)
            .Append(tree ? skill.Revision.RecordedTree : skill.Revision.RepositoryRef)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => tree ? value!.ToLowerInvariant() : value!)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return values.Length == 1 && (!tree || values[0].Length == 40 && values[0].All(char.IsAsciiHexDigit)) ? values[0] : null;
    }

    private static bool Differs(IEnumerable<string?> values) => values.Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal).Take(2).Count() > 1;
}
