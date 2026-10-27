using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Maintenance;

public sealed partial class SkillUpdateCoordinator
{
    private sealed class AvailabilityTrackingChangedException(string message) : IOException(message);
    // One exact read path; no saved candidates, private installs, archives,
    // payload hashing, or mutation admission. The UI bounds concurrency.
    internal async Task<UpdateAvailability> CheckAvailabilityAsync(string ghPath, InstalledSkill target,
        ProcessRunner context, CancellationToken token)
    {
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(30),
            CancellationCallbackReporter.For(logger, "availability deadline"));
        var ct = deadline.Token;
        UpdateAvailability Result(UpdateAvailabilityStatus status, string reason, string diagnostic = "") =>
            new(status, reason, DateTimeOffset.Now, diagnostic);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!target.MetadataConflicts.IsDefaultOrEmpty || !target.OwnershipDiagnostics.IsDefaultOrEmpty)
                return Result(UpdateAvailabilityStatus.Unknown, "Source or owner observations conflict or are incomplete. Inspect Details before checking again.");
            if (target.AllOwnershipClaims.Any(owner => owner.IsExclusiveOwner || owner.ReadOnly
                || owner.PluginName is not null || !LocalInventoryService.SharedTrackingMatches(owner, target)))
                return Result(UpdateAvailabilityStatus.Unsupported, "Managed or package-owned copy: use its owning tool to check and update it.");
            ManagedSkillDiscovery.RefuseTarget(target, ct, context);
            var pin = PinDisplayPolicy.From(target);
            if (pin.State is PinDisplayState.Conflicting or PinDisplayState.Unknown)
                return Result(UpdateAvailabilityStatus.Unknown, pin.Guidance);
            if (pin.HasPin) return Result(UpdateAvailabilityStatus.Pinned, pin.Guidance);
            var m = target.FrontMatter;
            if (string.IsNullOrWhiteSpace(m.Upstream) || string.IsNullOrWhiteSpace(m.GithubRef)
                || string.IsNullOrWhiteSpace(m.GithubTreeSha) || m.LocalPath is not null)
                return Result(UpdateAvailabilityStatus.Unsupported, "No complete recorded GitHub source/ref/tree. When the historical ref is absent, remove and reinstall where admitted. Local copies use reimport; managed copies use their owning tool.");
            var parent = Directory.GetParent(target.ResolvedPath)?.FullName
                ?? throw new IOException("The selected copy has no containing directory.");
            if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent, target.ResolvedPath,
                out var identity, out var error, ct)) throw new IOException(error);
            if (target.ObservedSubject?.NativeObject is { Status: "observed" } captured
                && NativeObjectObservation.From(identity.Directory.Identity, default, captured.Role) with { CapturedAt = null }
                    != captured with { CapturedAt = null })
                return Result(UpdateAvailabilityStatus.Stale, "The physical copy changed since inventory capture. Refresh and check again.");
            var localCatalog = new GhSkillListAdapter(context, logger);
            var observed = await localCatalog.CaptureAsync(ghPath, directory: parent,
                cancellationToken: ct, bypassCache: true).ConfigureAwait(false);
            if (!observed.Succeeded) return Result(UpdateAvailabilityStatus.Failed, "Exact-directory inventory failed.", observed.Error ?? "");
            var affected = observed.Records.Where(record => record.Path is { } path
                && SecureRemovalBackend.TryCanonicalizePath(path, out var canonical, out _)
                && PathIdentity.Equals(canonical, identity.Directory.Identity.CanonicalPath)).ToArray();
            if (affected.Length != 1 || string.IsNullOrWhiteSpace(affected[0].Name)
                || observed.Records.Count(record => record.Name == affected[0].Name) != 1)
                return Result(UpdateAvailabilityStatus.Unknown, "gh cannot uniquely express this exact copy in its containing directory.");
            var selector = affected[0].Name!;
            var before = ReadTracking(target, ct);
            var result = await new GhSkillUpdateService(context, logger).UpdateAsync(ghPath,
                new(Skills: [selector], DryRun: true, Directory: parent, Targets: [target]), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent, target.ResolvedPath,
                out var latest, out _, ct) || latest.Directory.Identity != identity.Directory.Identity
                || before != ReadTracking(target, ct))
                return Result(UpdateAvailabilityStatus.Stale, "The selected copy or tracking metadata changed during the check. Refresh and check again.");
            return ClassifyAvailability(result, selector, m.Upstream);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Result(token.IsCancellationRequested ? UpdateAvailabilityStatus.Canceled : UpdateAvailabilityStatus.Failed,
                token.IsCancellationRequested ? "Check canceled; no availability conclusion." : "Check exceeded its 30-second deadline; retry when connected.");
        }
        catch (AvailabilityTrackingChangedException ex)
        {
            return Result(UpdateAvailabilityStatus.Stale, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Result(UpdateAvailabilityStatus.Unknown, "The exact copy could not be checked: " + ex.Message);
        }
    }

    private static string ReadTracking(InstalledSkill target, CancellationToken token)
    {
        using var stream = RegularFileInspection.Open(Path.Combine(target.ResolvedPath, "SKILL.md"));
        var buffer = new byte[LocalSkillScanner.MaxFrontMatterPrefixBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        var (_, metadata, parsed) = FrontMatterParser.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, count));
        var original = target.FrontMatter;
        if (!parsed || !metadata.Diagnostics.IsEmpty || metadata.Upstream != original.Upstream
            || metadata.GithubRef != original.GithubRef || metadata.GithubPath != original.GithubPath
            || metadata.GithubTreeSha != original.GithubTreeSha || metadata.PinnedRef != original.PinnedRef
            || metadata.Pinned != original.Pinned || metadata.LocalPath != original.LocalPath)
            throw new AvailabilityTrackingChangedException("Recorded tracking metadata changed or is invalid; refresh inventory.");
        return UpdateAvailability.Evidence(target with { FrontMatter = metadata, ObservedSubject = null });
    }

    internal static UpdateAvailability ClassifyAvailability(UpdateResult result, string selector, string upstream)
    {
        var raw = result.StdOut + "\n" + result.StdErr;
        var diagnostic = raw.Length <= 8192 ? raw : raw[..8192] + "\n[diagnostic shortened]";
        UpdateAvailability Outcome(UpdateAvailabilityStatus status, string reason) => new(status, reason, DateTimeOffset.Now, diagnostic);
        if (result.Cancelled) return Outcome(UpdateAvailabilityStatus.Canceled, "Check canceled; no availability conclusion.");
        if (!result.Succeeded) return Outcome(UpdateAvailabilityStatus.Failed, "gh could not check upstream. Check authentication, network or rate limits; no source-deletion conclusion.");
        if (result.OutputTruncated) return Outcome(UpdateAvailabilityStatus.Unknown, "gh output was truncated; check coverage is incomplete.");
        var lines = raw.Replace("\r\n", "\n").Split('\n').Select(line =>
            (SkillView.Ui.TerminalEscapeSanitizer.Sanitize(line) ?? "").Trim().TrimStart('•', '✓', '✔').Trim())
            .Where(line => line.Length > 0).ToArray();
        var entries = result.Entries;
        if (entries.Any(entry => entry.Name != selector || entry.Status is not ("available" or "up-to-date")
            || entry.Repo is { } repo && repo != upstream && "https://github.com/" + repo != upstream))
            return Outcome(UpdateAvailabilityStatus.Unknown, "gh reported a skipped, failed, conflicting or different target. Inspect the diagnostic output.");
        // Only understood target lines and the supported exact success summary
        // can establish an answer. In particular, a warning followed by the
        // success summary (gh sometimes exits zero after skips) is not current.
        bool ExactTargetLine(string line) => line == selector + ": up-to-date" || line == selector + ": up to date"
            || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\S+ \([^)]+\) \S* > \S+ \[[^\s\]]+\]$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var unknown = lines.Any(line => line != "All skills are up to date."
            && !(line == "1 update(s) available:" && entries.Count(entry => entry.Status == "available") == 1)
            && !ExactTargetLine(line));
        if (unknown || lines.Length == 0) return Outcome(UpdateAvailabilityStatus.Unknown, "gh output did not unambiguously establish availability. Inspect Details and retry.");
        var available = entries.Any(entry => entry.Status == "available");
        var unchanged = lines.Contains("All skills are up to date.") || entries.Any(entry => entry.Status == "up-to-date");
        if (available && unchanged) return Outcome(UpdateAvailabilityStatus.Unknown, "gh reported conflicting availability outcomes.");
        return available ? Outcome(UpdateAvailabilityStatus.Available, "gh reports a different upstream tree. Review this exact copy before applying.")
            : unchanged ? Outcome(UpdateAvailabilityStatus.NoUpdate, "gh reports no upstream tree update. Installed files have not been verified.")
            : Outcome(UpdateAvailabilityStatus.Unknown, "No understood availability result for this copy.");
    }
}
