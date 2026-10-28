using SkillView.Maintenance;

namespace SkillView.Ui;

internal static class CapturedFindingFormatter
{
    internal static string Render(ChangesQueueRow row, CapturedFinding finding, CancellationToken token, bool technical)
    {
        var registry = finding.Subject is FindingRegistry;
        var heading = registry ? "gh registry observation" : finding.Kind switch
        { "recorded-revisions" => "Recorded revisions differ", "installation-methods" => "Installation methods differ", "same-source-copies" => "Same source, separate copies", _ => "Same name, separate sources" };
        var actions = registry
            ? "Inspect this captured record below. To include a known location, restart with **--scan-root ROOT** (before the subcommand), or configure an existing workspace/profile. **1 Discover** offers normal install review. gh has no supported exact-record remove/uninstall; registry cleanup and editing are unavailable.\n\n"
            : "**Enter:** choose an exact copy for **Review version alignment** (latest/tag saved review) or **Compare files** (explicit captured comparison). Each copy is reviewed separately.\n\n"
                + (finding.Difference is null ? "Keep both unavailable: family evidence exceeds its bounded binding limit.\n\n"
                    : finding.Intentional ? "**Intentional difference. k / Restore suggestions:** undo Keep both. Other problems, updates and coverage remain visible.\n\n"
                    : "**k / Keep both:** acknowledge this exact observed difference; cleanup suppression is separate.\n\n");
        var rendered = MaintenanceQueueFormatter.RenderFields(Fields(), token,
            (technical ? MaintenanceQueueFormatter.MaxDetailCharacters : MaintenanceQueueFormatter.MaxSummaryCharacters) - 1400);
        return "## " + heading + "\n\n" + actions + rendered.Text
            + (rendered.OmittedFields > 0 ? $"\n{rendered.OmittedFields} whole evidence fields omitted. F2 shows more captured evidence; CLI list/inspect with the same locations captures fresh evidence. Omission never establishes absence.\n" : "");

        IEnumerable<(string, string?)> Fields()
        {
            yield return ("Skill / selector", finding.Title);
            yield return ("Observation", finding.Explanation);
            yield return ("Coverage", finding.Partial ? "Partial/unknown; other copies or consumers may exist." : "Observed known locations only; other copies may exist.");
            yield return ("Captured", finding.CapturedAt.ToString("O"));
            if (finding.PreferenceCapacity is not null) yield return ("Saved intentional differences capacity", finding.PreferenceCapacity);
            foreach (var field in finding.Evidence) { token.ThrowIfCancellationRequested(); yield return field; }
            if (finding.Subject is FindingRegistry)
            {
                yield break;
            }
            var copies = ((FindingCopies)finding.Subject).Copies;
            for (var index = 0; index < copies.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var copy = copies[index];
                yield return ($"Copy {index + 1}", technical ? copy.Path : row.LocationLabels[index]);
                yield return ("Source", copy.Source.Origin);
                yield return ("Subpath", copy.Source.Subpath);
                yield return ("Recorded ref", copy.Revision.RepositoryRef);
                yield return ("Recorded tree", copy.Revision.RecordedTree);
                yield return ("Authored version (separate)", copy.Revision.AuthoredVersion);
                yield return ("Topology", copy.Materialization);
                if (technical)
                {
                    yield return ("Installation address ID", copy.InstallationId);
                    yield return ("Native object", copy.NativeObject.Volume + ":" + copy.NativeObject.ObjectId);
                    yield return ("Source evidence", copy.Source.Evidence);
                    yield return ("Source conflict", copy.Source.Conflicted.ToString());
                    yield return ("Exposure coverage", copy.ExposureCoverage);
                }
                foreach (var owner in copy.Owners)
                {
                    yield return ("Owner / method", $"{owner.Provider} / {owner.InstallationMode ?? "unknown"}; read-only={owner.ReadOnly}; exclusive={owner.IsExclusiveOwner}");
                    yield return ("Owner context", owner.ContextRoot);
                    if (!technical) continue;
                    yield return ("Owner source", owner.Source); yield return ("Owner registry", owner.LockFilePath);
                    yield return ("Owner selector", owner.Selector); yield return ("Owner diagnostic", owner.Diagnostic);
                    yield return ("Owner source URL", owner.SourceUrl); yield return ("Owner source subpath", owner.SkillPath);
                    yield return ("Owner ref", owner.RepositoryRef); yield return ("Owner hash domain", owner.HashKind);
                    yield return ("Owner hash", owner.ContentHash); yield return ("Owner pinned ref", owner.PinnedRef);
                    yield return ("Owner placement root", owner.InstallationRoot); yield return ("Owner schema", owner.SchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return ("Owner entry fingerprint", owner.EntryFingerprint); yield return ("Owner file fingerprint", owner.RecordFingerprint);
                    foreach (var root in owner.CoveredRoots) yield return ("Owner covered root", root);
                }
                if (copy.Owners.IsEmpty) yield return ("Owner / method", "No contextual owner claim captured; installation method unknown.");
                foreach (var exposure in copy.Exposures)
                {
                    yield return ("Exposure", exposure.Membership.Path);
                    yield return ("Consumer / context", $"{exposure.Membership.AgentId} / {exposure.Membership.Scope} / {exposure.Membership.Context ?? "unknown"}");
                    if (technical) { yield return ("Profile", exposure.Membership.Profile); yield return ("Link", exposure.Membership.IsSymlink.ToString()); }
                }
            }
        }
    }
}
