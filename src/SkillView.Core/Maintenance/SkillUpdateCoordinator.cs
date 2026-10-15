using System.Collections.Immutable;
using System.Text;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Maintenance;

/// Low-level gh contract coordinator. Product callers use its dry-run path for
/// availability only; saved candidate preparation/apply uses SavedUpdateCoordinator.
public sealed class SkillUpdateCoordinator(
    GhSkillInstallService installer, GhSkillUpdateService updater,
    GhSkillListAdapter catalog, Logger logger, SkillView.Subprocess.ProcessRunner? sourceRunner = null)
{
    public const int MaxTargets = 128;
    private const int MaxOutputChars = ProcessOutputLimit;
    private const int ProcessOutputLimit = 1024 * 1024;

    public async Task<UpdateResult> UpdateAsync(string ghPath, GhSkillUpdateService.Options options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Targets is not { Count: > 0 } targets || targets.Count > MaxTargets)
            return UpdateResult.Failure(options.DryRun, 2,
                $"Select 1–{MaxTargets} exact physical installations before updating.", []);
        var outcomes = ImmutableArray.CreateBuilder<InstallationUpdateOutcome>();
        var entries = ImmutableArray.CreateBuilder<UpdateEntry>();
        var notices = ImmutableArray.CreateBuilder<UpdateNotice>();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var commands = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attempted = false;
        var canceled = false;
        var failed = false;
        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
            if (!seen.Add(target.InstallationId)) continue;
            var exposures = target.Agents.Select(a => $"{a.AgentId}: {a.Path}").ToImmutableArray();
            PayloadSnapshot? before = null;
            PayloadSnapshot? after = null;
            string? message = null;
            string status = "refused";
            string? staging = null;
            OwnedDirectoryLease? stagingOwner = null;
            var targetAttempted = false;
            IReadOnlyDictionary<string, string>? mutationEnvironment = null;
            try
            {
                if (!target.MetadataConflicts.IsDefaultOrEmpty)
                    throw new IOException("Resolve conflicting tracking observations before updating.");
                if (!target.OwnershipDiagnostics.IsDefaultOrEmpty)
                    throw new IOException("Owner-record inspection is incomplete; resolve its diagnostics before updating.");
                if (target.AllOwnershipClaims.Any(package => package.IsExclusiveOwner || package.ReadOnly
                    || package.PluginName is not null || !LocalInventoryService.SharedTrackingMatches(package, target)))
                    throw new IOException("This installation has a package owner; use an owner-aware maintenance plan.");
                if (target.Pinned && !options.Unpin)
                {
                    status = "pinned";
                    message = "Exact pin retained. Unpin must be explicitly selected for this installation.";
                }
                else if (string.IsNullOrWhiteSpace(target.FrontMatter.Upstream)
                    || string.IsNullOrWhiteSpace(target.FrontMatter.GithubTreeSha)
                    || string.IsNullOrWhiteSpace(target.FrontMatter.GithubRef)
                    || target.FrontMatter.LocalPath is not null)
                {
                    status = "untracked";
                    message = "A verified remote source, recorded revision and tree are required; use adoption or reimport for local sources.";
                }
                else
                {
                    var parent = Directory.GetParent(target.ResolvedPath)?.FullName
                        ?? throw new IOException("Installation has no containing skills directory.");
                    var selector = Path.GetFileName(target.ResolvedPath);
                    if (!SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent,
                            target.ResolvedPath, out var identity, out var identityError, cancellationToken))
                        throw new IOException($"Cannot pin selected installation: {identityError}");
                    catalog.Invalidate();
                    var observed = await catalog.CaptureAsync(ghPath, directory: parent,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!observed.Succeeded) throw new IOException($"Exact-directory inventory failed: {observed.Error}");
                    var affected = observed.Records.Where(r => r.Path is { } path
                        && SecureRemovalBackend.TryCanonicalizePath(path, out var canonical, out _)
                        && PathIdentity.Equals(canonical, identity.Directory.Identity.CanonicalPath)).ToArray();
                    if (affected.Length != 1 || affected[0].Path is not { } observedPath
                        || (!SecureRemovalBackend.TryCanonicalizePath(observedPath, out var observedCanonical, out _)
                            || !PathIdentity.Equals(observedCanonical, identity.Directory.Identity.CanonicalPath)))
                        throw new IOException("gh cannot express this exact physical target without affecting another installation.");
                    before = PayloadSnapshot.Capture(target.ResolvedPath, cancellationToken);
                    if (before.FrontMatter is not { } currentMetadata || currentMetadata.Diagnostics.Length != 0
                        || currentMetadata.Upstream != target.FrontMatter.Upstream
                        || currentMetadata.GithubRef != target.FrontMatter.GithubRef
                        || currentMetadata.GithubTreeSha != target.FrontMatter.GithubTreeSha
                        || currentMetadata.GithubPath != target.FrontMatter.GithubPath
                        || currentMetadata.PinnedRef != target.FrontMatter.PinnedRef)
                        throw new IOException("Selected tracking metadata changed or is invalid; refresh and review again.");
                    if (!options.DryRun)
                    {
                        var sharedRoot = Path.Combine(updater.EffectiveHome ?? throw new IOException("Cannot establish global lock context."), ".agents", "skills");
                        var writesSharedRecord = PathIdentity.Equals(parent, sharedRoot)
                            || target.AllOwnershipClaims.Any(claim => claim.Provider == "gh-vercel-global" && claim.LockFilePath is { } lockPath
                                && PathIdentity.Equals(lockPath, Path.Combine(updater.EffectiveHome!, ".agents", SkillLockFileReader.FileName)))
                            || (SecureRemovalBackend.TryCanonicalizePath(sharedRoot, out var canonicalSharedRoot, out _)
                                && PathIdentity.Equals(parent, canonicalSharedRoot));
                        if (writesSharedRecord) GhSharedLockGuard.ValidateCurrent(cancellationToken, updater.EffectiveHome);
                        var repository = target.FrontMatter.Upstream;
                        if (!Uri.TryCreate(repository, UriKind.Absolute, out var sourceUri)
                            || sourceUri.Scheme != "https" || sourceUri.Host != "github.com"
                            || sourceUri.AbsolutePath.Trim('/').Split('/').Length != 2
                            || sourceUri.UserInfo.Length != 0 || sourceUri.Query.Length != 0 || sourceUri.Fragment.Length != 0)
                            throw new IOException("Recorded source is unsupported or ambiguous; no source is guessed from the name.");
                        staging = Path.Combine(Path.GetTempPath(), "skillview-update-stage-" + Guid.NewGuid().ToString("N"));
                        using var stagingAuthority = NativeStateDirectory.OpenOrCreate(staging, [parent], cancellationToken);
                        using var stagingWriter = stagingAuthority.OpenFile("writer.lock", true, cancellationToken);
                        if (stagingWriter.Length != 0) throw new IOException("Staging writer lock must be empty.");
                        stagingOwner = OwnedDirectoryLease.Open(staging);
                        var stageEnvironment = CreateStagingEnvironment(staging);
                        // --dir fixes placement. Isolate incidental global registry
                        // writes for project/custom targets so they cannot replace
                        // a user's same-selector record for another installation.
                        if (!writesSharedRecord) mutationEnvironment = CreateStagingEnvironment(Path.Combine(staging, "mutation"));
                        var refDestination = Path.Combine(staging, "current-ref");
                        var recordedRef = ShortRef(target.FrontMatter.GithubRef);
                        var sourcePath = target.FrontMatter.GithubPath;
                        // Repository listings render multiline descriptions
                        // without escaping; they are not always an unambiguous
                        // selector protocol. Stage the recorded path in an
                        // empty private destination and inspect the result.
                        var sourceSelector = sourcePath ?? selector;
                        if (sourceSelector.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\')))
                            throw new IOException("Unsafe repository skill selector.");
                        var staged = await installer.InstallAsync(ghPath, repository, sourceSelector,
                            new GhSkillInstallService.Options(Path: refDestination, Version: recordedRef, Pin: true,
                                ProcessEnvironment: stageEnvironment),
                            cancellationToken).ConfigureAwait(false);
                        if (!staged.Succeeded) throw new IOException($"Cannot stage recorded base: {staged.ErrorMessage}");
                        var stagedPaths = Directory.EnumerateDirectories(refDestination).Take(2).ToArray();
                        if (stagedPaths.Length != 1) throw new IOException("Recorded source did not materialize one exact skill.");
                        var stagedPath = stagedPaths[0];
                        var basePayload = PayloadSnapshot.Capture(stagedPath, cancellationToken);
                        var frontMatter = basePayload.FrontMatter;
                        if (frontMatter is null || frontMatter.GithubTreeSha != target.FrontMatter.GithubTreeSha
                            || (sourcePath is not null && frontMatter.GithubPath != sourcePath))
                        {
                            var rawBase = await new GhRecordedTreeStager(sourceRunner ?? new SkillView.Subprocess.ProcessRunner(logger))
                                .StageAsync(ghPath, repository, target.FrontMatter.GithubTreeSha,
                                    Path.Combine(staging, "recorded-tree"), cancellationToken).ConfigureAwait(false);
                            // Use gh's own local materialization so frontmatter formatting and
                            // recognized installer metadata follow the actual supported host.
                            var materialized = Path.Combine(staging, "materialized-base");
                            var import = await installer.InstallAsync(ghPath, rawBase, null,
                                new GhSkillInstallService.Options(Path: materialized, FromLocal: true, All: true,
                                    ProcessEnvironment: stageEnvironment),
                                cancellationToken).ConfigureAwait(false);
                            if (!import.Succeeded) throw new IOException($"Cannot materialize recorded tree: {import.ErrorMessage}");
                            var basePaths = Directory.EnumerateDirectories(materialized).Take(2).ToArray();
                            if (basePaths.Length != 1) throw new IOException("Recorded tree did not materialize one exact skill.");
                            basePayload = PayloadSnapshot.Capture(basePaths[0], cancellationToken);
                        }
                        var changes = PayloadSnapshot.Differences(basePayload, before);
                        if (changes.Length != 0)
                            throw new IOException($"Local changes protected ({changes.Length} file(s)): {string.Join(", ", changes.Take(12))}. Use a compare/backup repair plan before replacement.");
                        var current = PayloadSnapshot.Capture(target.ResolvedPath, cancellationToken);
                        if (current.Fingerprint != before.Fingerprint
                            || !SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(parent,
                                target.ResolvedPath, out var latestIdentity, out _, cancellationToken)
                            || latestIdentity.Directory.Identity != identity.Directory.Identity)
                            throw new IOException("Selected installation changed during update preparation; review again.");
                    }
                    var exact = options with
                    {
                        Skills = [selector],
                        All = false,
                        ConfirmApply = true,
                        Directory = parent,
                        Targets = [target],
                        ProcessEnvironment = mutationEnvironment
                    };
                    var result = await updater.UpdateAsync(ghPath, exact, cancellationToken).ConfigureAwait(false);
                    targetAttempted = result.MutationAttempted;
                    attempted |= targetAttempted;
                    AppendBounded(stdout, result.StdOut);
                    AppendBounded(stderr, result.StdErr);
                    entries.AddRange(result.Entries.Take(Math.Max(0, 4096 - entries.Count)));
                    foreach (var notice in result.Notices.Take(Math.Max(0, 512 - notices.Count))) notices.Add(notice);
                    commands.AddRange(result.CommandLine);
                    canceled |= result.Cancelled;
                    if (result.Cancelled) status = "canceled";
                    else if (!result.Succeeded) { status = "failed"; failed = true; message = result.ErrorMessage; }
                    else if (result.Entries.Any(e => e.Status is "skipped" or "source-unavailable"))
                    { status = result.Entries.Any(e => e.Status == "source-unavailable") ? "source-unavailable" : "skipped"; message = "gh skipped this target; both output streams explain the reason."; }
                    else status = options.DryRun ? result.Entries.Any(e => e.Status is "available" or "restore-available")
                        ? "available" : result.StdErr.Contains("All skills are up to date.", StringComparison.Ordinal)
                            ? "unchanged" : "availability-unknown" : "unchanged";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { canceled = true; status = "canceled"; message = "Canceled; any attempted mutation is verified separately."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { failed = true; status = "refused"; message = ex.Message; }
            finally
            {
                if (targetAttempted)
                {
                    catalog.Invalidate();
                    using var verification = new CancellationSource(TimeSpan.FromSeconds(3),
                        CancellationCallbackReporter.For(logger, "update verification"));
                    try
                    {
                        after = PayloadSnapshot.Capture(target.ResolvedPath, verification.Token);
                        if (!canceled && status is not ("failed" or "refused" or "skipped")
                            && before?.Fingerprint != after.Fingerprint) status = "changed";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    { failed = true; status = "verification-incomplete"; message = $"{message} Post-attempt verification incomplete: {ex.Message}".Trim(); }
                }
                if (staging is not null && stagingOwner is not null)
                {
                    try { RemoveOwnedStaging(staging, stagingOwner); }
                    finally { stagingOwner.Dispose(); }
                }
            }
            var changedFiles = before is not null && after is not null
                ? PayloadSnapshot.Differences(before, after, ignoreInstallerMetadata: false) : [];
            outcomes.Add(new(target.InstallationId, target.ResolvedPath, status, before?.Fingerprint,
                after?.Fingerprint, changedFiles.Take(128).ToImmutableArray(), changedFiles.Length, exposures, message));
            if (canceled) break;
        }
        foreach (var target in targets.Where(t => !seen.Contains(t.InstallationId)))
            outcomes.Add(new(target.InstallationId, target.ResolvedPath, "not-attempted", null, null, [], 0,
                target.Agents.Select(a => $"{a.AgentId}: {a.Path}").ToImmutableArray(), "Canceled before this target."));
        return new UpdateResult
        {
            DryRun = options.DryRun,
            Succeeded = !failed && !canceled,
            ExitCode = canceled ? 130 : failed ? 2 : 0,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            ErrorMessage = canceled ? "Update canceled; inspect per-installation outcomes." : failed ? "One or more installations were refused or failed." : null,
            CommandLine = commands,
            Entries = entries.ToImmutable(),
            Notices = notices.ToImmutable(),
            Outcomes = outcomes.ToImmutable(),
            Cancelled = canceled,
            MutationAttempted = attempted
        };
    }

    private void RemoveOwnedStaging(string staging, OwnedDirectoryLease owner)
    {
        using var deadline = new CancellationSource(TimeSpan.FromSeconds(3),
            CancellationCallbackReporter.For(logger, "update staging cleanup"));
        try
        {
            var snapshot = owner.Capture(deadline.Token);
            SecureRemovalBackend.RemoveTree(staging, snapshot.Identity, false, PayloadSnapshot.MaxDepth + 2,
                _ => { }, (_, _) => { }, (_, _) => { },
                (_, error) => logger.Warn("update.staging", $"Retained staging {staging}: {error}"), deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { logger.Warn("update.staging", $"Retained staging {staging}: {ex.Message}"); }
    }

    private static string ShortRef(string value) => value.StartsWith("refs/heads/", StringComparison.Ordinal)
        ? value[11..] : value.StartsWith("refs/tags/", StringComparison.Ordinal) ? value[10..] : value;

    internal static IReadOnlyDictionary<string, string> CreateStagingEnvironment(string staging)
    {
        // gh installs write ~/.agents/.skill-lock.json even with --dir. Keep
        // all staging state private while explicitly retaining the user's auth
        // configuration location; never change the parent process environment.
        var originalHome = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = ResolveGhConfigDirectory(originalHome, Environment.GetEnvironmentVariable("GH_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetEnvironmentVariable("APPDATA"),
            OperatingSystem.IsWindows());
        var home = Path.Combine(staging, "private-home");
        Directory.CreateDirectory(home);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["GH_CONFIG_DIR"] = Path.GetFullPath(config),
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "localappdata"),
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1",
            ["GH_TELEMETRY"] = "false"
        };
    }

    internal static string ResolveGhConfigDirectory(string home, string? explicitDirectory,
        string? xdgDirectory, string? appData, bool windows) => !string.IsNullOrWhiteSpace(explicitDirectory)
        ? explicitDirectory : !string.IsNullOrWhiteSpace(xdgDirectory) ? Path.Combine(xdgDirectory, "gh")
        : windows && !string.IsNullOrWhiteSpace(appData) ? Path.Combine(appData, "GitHub CLI")
        : Path.Combine(home, ".config", "gh");

    private static void AppendBounded(StringBuilder destination, string text)
    {
        if (destination.Length >= MaxOutputChars) return;
        destination.Append(text.AsSpan(0, Math.Min(text.Length, MaxOutputChars - destination.Length)));
    }
}
