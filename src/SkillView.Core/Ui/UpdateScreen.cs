using SkillView.Gh.Models;
using SkillView.Maintenance;

namespace SkillView.Ui;

/// Static helpers for rendering update results as Markdown. The interactive
/// view is now <see cref="Tabs.UpdatesTabView"/>; the modal Show() subloop
/// was retired in Phase 5 of the winget-tui redesign. RenderResult stays on
/// this type so the UpdateScreenTests assertions don't need updating.
public static class UpdateScreen
{
    internal static string RenderResult(UpdateResult result, bool dryRun, bool allChecked, List<string> skills, bool unpinOnly = false, bool localReimport = false, bool localSourceSwitch = false)
    {
        var sb = new System.Text.StringBuilder();
        var heading = localSourceSwitch || result.SavedPlan?.LocalSourceSwitch is not null ? "Local source switch results" : dryRun ? "Dry-run results" : localReimport || result.SavedPlan?.LocalImport is not null ? "Local reimport results" : unpinOnly || result.SavedPlan?.PinPolicy is not null ? "Unpin-only results" : "Update results";
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        if (result.SavedPlan is { } plan) sb.AppendLine(RenderPlan(plan));
        var scope = allChecked
            ? "_all skills_"
            : skills.Count == 1
                ? MarkdownTableFormatter.FormatCodeSpan(skills[0])
                : $"{skills.Count} skills";
        sb.AppendLine($"**Scope:** {scope}  ·  **Exit:** {result.ExitCode}  ·  **Entries:** {result.Entries.Length}");
        sb.AppendLine();
        if (!result.Succeeded)
        {
            sb.AppendLine("### ⛔ Failed or refused");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) AppendFencedBlock(sb, result.ErrorMessage);
        }
        if (!result.Outcomes.IsDefaultOrEmpty)
        {
            sb.AppendLine("| Installation | Outcome | Evidence |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var outcome in result.Outcomes)
            {
                sb.AppendLine($"| {MarkdownTableFormatter.FormatCodeSpan(outcome.Path)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(outcome.Status)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(outcome.Message ?? outcome.ChangedFileCount + " changed file(s)")} |");
                if (outcome.Impact is { } impact) OperationImpactFormatter.Append(sb, impact);
                else foreach (var exposure in outcome.Exposures)
                    sb.AppendLine($"Affected exposure: {MarkdownTableFormatter.FormatCodeSpan(exposure)}  ");
            }
            sb.AppendLine();
        }
        if (result.Entries.Length > 0)
        {
            sb.AppendLine("| Skill | Status | Change |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var entry in result.Entries)
            {
                var change = string.IsNullOrWhiteSpace(entry.FromVersion) && string.IsNullOrWhiteSpace(entry.ToVersion)
                    ? "—" : MarkdownTableFormatter.FormatCodeSpan($"{entry.FromVersion ?? "?"} -> {entry.ToVersion ?? "?"}");
                sb.AppendLine($"| {MarkdownTableFormatter.FormatCodeSpan(entry.Name)} | " +
                    $"{MarkdownTableFormatter.FormatTableCell(entry.Status)} | {change} |");
            }
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            sb.AppendLine("### Standard output");
            AppendFencedBlock(sb, result.StdOut);
        }
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            sb.AppendLine("### Notices and errors");
            AppendFencedBlock(sb, result.StdErr);
        }
        foreach (var notice in result.Notices)
        {
            sb.AppendLine($"### {MarkdownTableFormatter.FormatTableCell(notice.Stream)}");
            AppendFencedBlock(sb, notice.Text);
        }
        if (result.Succeeded && result.Outcomes.IsDefaultOrEmpty && string.IsNullOrWhiteSpace(result.StdOut)
            && string.IsNullOrWhiteSpace(result.StdErr))
            sb.AppendLine("_Completed with no output; availability was not independently established._");
        return sb.ToString();
    }

    internal static string RenderPlan(SkillView.Maintenance.ReplacementResult plan)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string label, string? value) => sb.AppendLine($"**{label}:** {MarkdownTableFormatter.FormatCodeSpan(value ?? "unknown")}  ");
        sb.AppendLine(plan.LocalSourceSwitch is not null ? "### Saved local source-switch review" : plan.LocalImport is not null ? "### Saved local reimport review" : plan.PinPolicy is not null ? "### Saved unpin-only review" : "### Saved update review");
        if (plan.Revision is { } tagRevision && tagRevision.Source.Ref.StartsWith("refs/tags/", StringComparison.Ordinal)
            && !tagRevision.DesiredMetadata.Pinned && tagRevision.DesiredMetadata.PinnedRef is null)
        {
            var previousRef = plan.Subject.Metadata.GithubRef ?? "unknown";
            var agents = plan.Subject.Agents.Select(agent => agent.AgentId).Distinct(StringComparer.Ordinal).ToArray();
            Field("Version", $"{TuiHelpers.Truncate(previousRef, 64)} -> {TuiHelpers.Truncate(tagRevision.Source.Ref, 64)}");
            Field("Affected recorded agents", agents.Length == 0 ? "none recorded; inspect exposure evidence below"
                : string.Join(", ", agents.Take(4).Select(agent => TuiHelpers.Truncate(agent, 24))));
            Field("Local changes", tagRevision.LocalChanges.Length.ToString());
            sb.AppendLine("Apply retains original, including local edits, for undo. " + VersionUpdateGuidance.FutureUpdates);
            if (previousRef != TuiHelpers.Truncate(previousRef, 64) || tagRevision.Source.Ref != TuiHelpers.Truncate(tagRevision.Source.Ref, 64)
                || agents.Length > 4 || agents.Take(4).Any(agent => agent != TuiHelpers.Truncate(agent, 24)))
                sb.AppendLine("Summary shortened; complete refs and affected consumer evidence remain below.");
            sb.AppendLine();
        }
        Field("State", plan.Status);
        Field("Installation", plan.OriginalPath);
        Field("Installation ID", plan.Subject.InstallationId);
        Field("Plan ID", plan.Id);
        Field("Plan fingerprint", plan.PlanFingerprint);
        Field("Manifest", plan.PlanPath);
        Field("Retained original", plan.RetainedPath);
        Field("Candidate", plan.CandidatePath);
        Field("Disposition", plan.LocalImport is not null ? plan.LocalImport.Disposition : plan.PinPolicy is not null ? "unpin-only; retain original" : plan.LocalDisposition?.Kind ?? "replace; retain original");
        Field("Review fingerprint", plan.ReviewFingerprint);
        if (plan.LocalSourceSwitch is { } change)
        {
            Field("Previous recorded source", change.PreviousRecordedPath);
            Field("Previous source placement", change.PreviousPlacement.CanonicalPath);
            Field("Placement coverage", change.PreviousPlacement.MissingSuffix.IsEmpty ? "existing ordinary directory; placement only" : "first missing boundary proven; remaining suffix unreachable; placement only");
            Field("Captured C recorded source", change.CapturedSourceRecordedPath ?? "absent");
            Field("Future update policy", change.FutureUpdatePolicy);
            sb.AppendLine("Old source placement is not a captured base or old-content freshness requirement. The selected source may be a copied local installation; no ancestry or independent physical fork is inferred. Git-managed sources remain unsupported.");
        }
        sb.AppendLine("### Frozen source changes");
        // Indented code preserves diff lines and prevents authored Markdown from
        // becoming review controls, links or headings.
        foreach (var line in SourceReviewScreen.RenderText(plan).Split('\n')) sb.Append("    ").AppendLine(line);
        sb.AppendLine();
        if (plan.LocalImport is { } import)
        {
            Field("Recorded local source", import.SourcePath);
            Field("Captured canonical source", import.CanonicalSourcePath);
            Field("Installed archive I", import.InstalledArchiveFingerprint);
            Field("Source archive C", import.SourceArchiveFingerprint);
            Field("Native metadata policy", import.MetadataPolicy);
            Field("gh witness version", import.Gh.Version);
            Field("gh witness binary SHA256", import.Gh.BinarySha256);
            Field("Authored version", $"{plan.Subject.Metadata.Version ?? "unspecified"} -> {import.DesiredMetadata.Version ?? "unspecified"}");
            Field("Metadata recipes", import.Metadata.Length.ToString());
            foreach (var recipe in import.Metadata.Take(128))
            {
                var desired = recipe.Desired;
                Field("Desired native metadata", $"{(recipe.Path.Length == 0 ? "/" : recipe.Path)}: {desired.Platform} {desired.Kind}; mode={desired.UnixMode}; uid={desired.UserId}; gid={desired.GroupId}; coverage={desired.EnumerationCoverage}; basis={recipe.BasisPath}");
                foreach (var attribute in desired.Attributes)
                    Field("Attribute", $"{attribute.Name}: {attribute.Value.Length} bytes; SHA256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(attribute.Value)).ToLowerInvariant()}");
                if (desired.Windows is { } windows)
                {
                    Field("Windows security", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(windows.SecurityDescriptor)).ToLowerInvariant());
                    Field("Windows file attributes", windows.FileAttributes.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var stream in windows.Streams)
                        Field("Windows stream", $"{stream.Name}: {stream.Bytes.Length} bytes; SHA256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.Bytes)).ToLowerInvariant()}");
                }
            }
            if (import.Metadata.Length > 128) Field("Additional metadata recipes", $"{import.Metadata.Length - 128} omitted from display; complete evidence remains in {plan.PlanPath}");
            if (plan.LocalSource is { } source) Field("Local source status", source.Status + ": " + source.Message);
            sb.AppendLine("Baseline-less replacement: source C replaces installed I, including source deletions and extras. No recorded base or automatic merge is available. Review every changed path and affected consumer.");
            sb.AppendLine("The gh staging output is a compatibility witness. SkillView materializes captured C with only the root local-path tracking splice; it preserves I root privacy and supported C descendant metadata. Active object IDs and timestamps change. The exact original native objects are retained for undo.");
            sb.AppendLine("Source identity, bytes, metadata and lexical/canonical address must remain fresh before the first retain move. After that move, recovery and undo use frozen evidence even if the source changes or disappears; a stale source observation does not veto them.");
        }
        if (plan.PinPolicy is { } pinPolicy)
        {
            Field("Pin", $"{plan.Subject.Metadata.PinnedRef ?? "verified owner pin"} -> explicitly unpinned");
            Field("Recorded source ref", plan.Subject.Metadata.GithubRef);
            Field("Recorded source tree", plan.Subject.Metadata.GithubTreeSha);
            Field("Native metadata policy", pinPolicy.MetadataPolicy);
            if (pinPolicy.Owner is { } owner)
            {
                Field("Registry", owner.RegistryPath);
                Field("Selected owner entry", owner.Selector);
                Field("Owner fingerprints", owner.BeforeFingerprint + " -> " + owner.DesiredFingerprint);
            }
            sb.AppendLine("Only verified pin tracking changes. Installed instructions, extras and source revision remain as captured; every consumer of this physical installation is affected.");
            sb.AppendLine("Apply swaps in a verified directory with new active object IDs and timestamps. Supported native attributes are preserved; the exact original native directory is retained for undo. This does not detach an individual consumer.");
        }
        if (plan.LocalDisposition is { } local)
        {
            Field("Source baseline candidate", local.SourceCandidate.Identity.CanonicalPath);
            Field("Source baseline payload", local.SourceCandidate.Payload);
            Field("Local choice fingerprint", local.SelectionFingerprint);
            Field("Native metadata policy", local.MetadataPolicy);
        }
        Field("Root mode", $"{plan.OriginalRootMode} -> {plan.CandidateRootMode}");
        if (plan.Revision is { } revision)
        {
            Field("Repository", revision.Source.Repository);
            Field("Recorded ref", plan.Subject.Metadata.GithubRef);
            Field("Resolved ref", revision.Source.Ref);
            Field("Commit", revision.Source.Commit);
            Field("Git tree", revision.Source.Tree);
            var resultVersion = plan.LocalDisposition is { } disposition ? disposition.ResultAuthoredVersion : revision.DesiredMetadata.Version;
            Field("Authored version", $"{plan.Subject.Metadata.Version ?? "unspecified"} -> {resultVersion ?? "unspecified"}");
            if (plan.LocalDisposition is not null) Field("Source authored version", revision.DesiredMetadata.Version ?? "unspecified");
            Field("Pin", plan.Subject.Metadata.Pinned || plan.Subject.Metadata.PinnedRef is not null
                ? $"{plan.Subject.Metadata.PinnedRef ?? plan.Subject.Metadata.GithubRef} -> explicitly unpinned" : "unpinned -> unpinned");
            Field("Local changes", revision.LocalChanges.Length.ToString());
            foreach (var path in revision.LocalChanges) Field("Local", path);
            if (revision.Owner is { } owner)
            {
                Field("Registry", owner.RegistryPath);
                Field("Selected owner entry", owner.Selector);
                Field("Owner fingerprints", owner.BeforeFingerprint + " -> " + owner.DesiredFingerprint);
            }
        }
        if (plan.Impact is { } impact) OperationImpactFormatter.Append(sb, impact);
        else foreach (var agent in plan.Subject.Agents) Field("Affected consumer", $"{agent.AgentId}: {agent.Path}");
        foreach (var path in plan.ChangedPaths.Take(128)) Field("Changed path", path);
        if (plan.ChangedPaths.Length > 128) Field("Omitted changed paths (complete saved plan retained)", (plan.ChangedPaths.Length - 128).ToString());
        if (plan.Comparison is { } comparison)
        {
            sb.AppendLine("### Installed → saved candidate comparison");
            Field("Installed payload", comparison.InstalledPayload);
            Field("Candidate payload", comparison.CandidatePayload);
            var rendered = MaintenanceQueueFormatter.RenderFields(comparison.Paths.Take(128)
                .Select(change => ("Path " + change.Path, (string?)$"{Describe(change.Installed)} -> {Describe(change.Candidate)}")),
                CancellationToken.None, MaintenanceQueueFormatter.MaxDetailCharacters);
            sb.Append(rendered.Text);
            Field("Comparison paths omitted from preview", (Math.Max(0, comparison.Paths.Length - 128) + rendered.OmittedFields).ToString());
            sb.AppendLine("Regular-file SHA256, kind, bytes and mode compare frozen evidence. Inspect instructions and supporting files before approval; this is not behavioral evaluation. Complete file evidence remains in plan JSON.");
        }
        sb.AppendLine("The original directory, including local edits and extras, is retained for explicit recovery/undo. Agent session state is unknown.");
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? "";
    }

    private static string Describe(SkillView.Maintenance.PayloadEntry? entry) => entry is null ? "absent"
        : $"{entry.Kind}; SHA256={entry.ContentHash}; bytes={entry.Bytes}; mode={entry.UnixMode}; link={entry.LinkTarget ?? "none"}";

    private static void AppendFencedBlock(System.Text.StringBuilder sb, string text)
    {
        var sanitized = TerminalEscapeSanitizer.Sanitize(text)?.TrimEnd() ?? string.Empty;
        var fenceLength = 3;
        var currentRun = 0;
        foreach (var ch in sanitized)
        {
            if (ch == '`')
            {
                currentRun++;
                if (currentRun >= fenceLength)
                {
                    fenceLength = currentRun + 1;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        var fence = new string('`', fenceLength);
        sb.AppendLine(fence);
        sb.AppendLine(sanitized);
        sb.AppendLine(fence);
    }
}
