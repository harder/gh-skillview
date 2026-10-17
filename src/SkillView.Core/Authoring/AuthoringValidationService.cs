using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Authoring;

internal sealed record AuthoringDiagnostic(string Code, string Path, string Message, string Guidance);
internal sealed record AuthoringPayload(string Path, string BeforeFingerprint, string? AfterFingerprint);
internal sealed record AuthoringValidationReport(string Repository, string Status, string Execution,
    string? GhPath, string? GhVersion, int? CommandExitCode, string StdOut, string StdErr,
    bool OutputTruncated, string DiscoveryCoverage, string AuthoredBytes,
    ImmutableArray<AuthoringPayload> Payloads, ImmutableArray<AuthoringDiagnostic> Diagnostics)
{
    internal const int SchemaVersion = 1;
    internal const string Scope = "gh local authoring discovery and publish dry-run; candidate payload bytes only; repository .git namespace excluded";
    internal const string Limitations = "gh may skip invalid candidate names and advisory API failures; command success does not establish complete discovery, agent compatibility, safety, intact source or source review. Dry-run may contact GitHub and write cache/config state; it is not a universal zero-write guarantee.";
}

/// Explicit repository authoring only. No inventory, update, fix, publish or agent execution path.
internal sealed class AuthoringValidationService(ProcessRunner runner, Logger logger)
{
    internal const int MaxNamespaceEntries = 8192;
    internal const int MaxCandidates = 128;
    internal const long MaxPayloadBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    // Test seam substitutes the command boundary, never a second production execution policy.
    internal Func<ProcessRunner, string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? RunForTests { get; init; }

    internal async Task<AuthoringValidationReport> ValidateAsync(string repository, string? ghPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(repository, runner.EffectiveWorkingDirectory);
        var overrides = new Dictionary<string, string>
        {
            ["GH_NO_UPDATE_NOTIFIER"] = "1", ["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1", ["GH_TELEMETRY"] = "false",
            ["GH_PROMPT_DISABLED"] = "1", ["NO_COLOR"] = "1", ["GIT_TERMINAL_PROMPT"] = "0"
        };
        foreach (var name in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "GH_CONFIG_DIR", "TMPDIR", "TEMP", "TMP" })
            if (runner.GetEffectiveEnvironmentVariable(name) is { Length: > 0 } value)
                overrides[name] = Path.GetFullPath(value, runner.EffectiveWorkingDirectory);
        var child = runner.WithContext(path, overrides);
        using var deadline = new CancellationSource(token, Timeout, CancellationCallbackReporter.For(logger, "authoring validation deadline"));
        var diagnostics = ImmutableArray.CreateBuilder<AuthoringDiagnostic>();
        var payloads = ImmutableArray.CreateBuilder<AuthoringPayload>();
        ProcessResult? result = null;
        string? version = null;
        var execution = "none";
        var discovery = "unavailable";
        var bytes = "not-observed";
        try
        {
            foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR" })
                if (!string.IsNullOrEmpty(child.GetEffectiveEnvironmentVariable(variable)))
                    return Refuse("redirected-git-context", variable + " redirects the selected repository.", "Run with that Git redirection unset; the parent environment is never changed.");
            var candidates = Discover(path, deadline.Token);
            discovery = "bounded-candidates";
            if (candidates.Length == 0)
            {
                diagnostics.Add(new("no-authoring-candidates", path, "No non-hidden SKILL.md candidate fits the supported repository layouts.", "Select the source repository containing child skill directories; installed inventory is a separate check workflow."));
                return Report("skipped");
            }
            long remaining = MaxPayloadBytes;
            foreach (var candidate in candidates)
            {
                var capture = PayloadSnapshot.CaptureEvidence(candidate, deadline.Token, remaining);
                if (capture.Entries.Any(entry => entry.Path == "SKILL.md" && entry.Kind != "file"))
                    return Refuse("nonregular-skill-metadata", candidate, "Use a regular authored SKILL.md file; this command does not follow metadata links.");
                remaining -= capture.Entries.Sum(entry => entry.Bytes);
                payloads.Add(new(candidate, capture.Fingerprint, null));
            }
            bytes = "captured-before";
            if (ghPath is null) return Refuse("gh-unavailable", "GitHub CLI was not found.", "Install gh 2.97.0 or newer and retry this explicit command.");
            var versionResult = await Run(["--version"], deadline.Token).ConfigureAwait(false);
            if (versionResult.Cancelled) deadline.Token.ThrowIfCancellationRequested();
            var parts = versionResult.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            version = parts.Length >= 3 && parts[0] == "gh" && parts[1] == "version" ? parts[2] : null;
            if (!versionResult.Succeeded || versionResult.StdOutTruncated || versionResult.StdErrTruncated || !GhBinaryLocator.SatisfiesMinimum(version))
                return Refuse("gh-version-unavailable", "The selected gh version is unknown or below 2.97.0.", "Use a supported GitHub CLI; no publish command ran.");
            var help = await Run(["skill", "--help"], deadline.Token).ConfigureAwait(false);
            if (help.Cancelled) deadline.Token.ThrowIfCancellationRequested();
            if (!help.Succeeded || help.StdOutTruncated || help.StdErrTruncated)
                return Refuse("gh-skill-unavailable", "The supported gh skill command is unavailable or its smoke check was incomplete.", "Repair the gh installation and retry explicitly.");
            deadline.Token.ThrowIfCancellationRequested();
            execution = "unknown";
            result = await Run(["skill", "publish", "--dry-run", "--", path], deadline.Token).ConfigureAwait(false);
            if (!result.Started)
            {
                execution = "none";
                return Refuse("gh-start-failed", "The gh publish dry-run process did not start; its start diagnostic is retained in stderr.",
                    "Repair the selected gh executable or its launch environment, then retry explicitly. No publish dry-run ran.");
            }
            execution = "gh-skill-publish-dry-run";
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            diagnostics.Add(new(token.IsCancellationRequested ? "canceled" : "deadline", path,
                "Authoring validation stopped before complete evidence was available.", "Review retained output, then rerun explicitly if needed."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(new("capture-or-command-unavailable", path, exception.Message, "Resolve the reported access, native inspection or resource bound before retrying; no automatic repair is performed."));
        }

        // A canceled child must quiesce before observing bytes. Use an independent
        // bounded verification lifetime so cancellation cannot hide a mutation.
        if (execution != "none")
        {
            using var verification = new CancellationSource(TimeSpan.FromSeconds(15), CancellationCallbackReporter.For(logger, "authoring byte verification"));
            try
            {
                var afterCandidates = Discover(path, verification.Token);
                if (!afterCandidates.SequenceEqual(payloads.Select(item => item.Path), StringComparer.Ordinal))
                    diagnostics.Add(new("candidate-set-changed", path, "Authoring candidate paths changed during validation.", "Review source changes manually; no bytes are repaired or restored."));
                long remaining = MaxPayloadBytes;
                for (var index = 0; index < payloads.Count; index++)
                {
                    var after = PayloadSnapshot.CaptureEvidence(payloads[index].Path, verification.Token, remaining);
                    remaining -= after.Entries.Sum(entry => entry.Bytes);
                    payloads[index] = payloads[index] with { AfterFingerprint = after.Fingerprint };
                    if (payloads[index].BeforeFingerprint != after.Fingerprint)
                        diagnostics.Add(new("authored-payload-changed", payloads[index].Path, "Captured authored payload bytes or entry modes changed during validation.", "Inspect the changes yourself. SkillView never silently repairs authored content."));
                }
                bytes = diagnostics.Any(item => item.Code is "authored-payload-changed" or "candidate-set-changed") ? "changed" : "unchanged-observed-candidates";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
            {
                bytes = "verification-incomplete";
                diagnostics.Add(new("post-capture-incomplete", path, exception.Message, "Verify authored source bytes manually before further work."));
            }
        }
        if (result?.Cancelled == true)
            diagnostics.Add(new(token.IsCancellationRequested ? "canceled" : "deadline", path, "The dry-run was interrupted; output is partial.", "Review retained output and rerun explicitly if needed."));
        if (result is { StdOutTruncated: true } or { StdErrTruncated: true })
            diagnostics.Add(new("output-truncated", path, "gh output exceeded the per-stream capture bound.", "Resolve noisy output; this result is partial even if gh returned zero."));
        if (result is { ExitCode: not 0, Cancelled: false })
            diagnostics.Add(new("gh-command-failed", path, "gh did not complete authoring validation successfully; its concrete diagnostics are retained in stdout/stderr.", "Review those diagnostics and edit source only through your own reviewed changes. No --fix is invoked."));
        return Report(execution == "none" ? "refused" : diagnostics.Any(item => item.Code != "gh-command-failed") ? "partial" : result?.Succeeded == true ? "completed" : "failed");

        Task<ProcessResult> Run(IReadOnlyList<string> arguments, CancellationToken cancellation) => RunForTests is { } run
            ? run(child, ghPath!, arguments, cancellation)
            : child.RunAsync(ghPath!, arguments, cancellationToken: cancellation, retainCancellationOutput: true);
        AuthoringValidationReport Refuse(string code, string message, string guidance)
        { diagnostics.Add(new(code, path, message, guidance)); return Report("refused"); }
        AuthoringValidationReport Report(string status) => new(path, status, execution, ghPath, version, result?.ExitCode,
            result?.StdOut ?? "", result?.StdErr ?? "", result?.StdOutTruncated == true || result?.StdErrTruncated == true,
            discovery, bytes, payloads.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<string> Discover(string repository, CancellationToken token)
    {
        if ((File.GetAttributes(repository) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
            throw new IOException("Select a real source repository directory, not a directory link.");
        // Upstream root SKILL.md discovery short-circuits every child and its
        // publish directory-name comparison uses '.'. Do not fake child validation.
        if (HasEntry(Path.Combine(repository, "SKILL.md")))
            throw new IOException("Root SKILL.md short-circuits gh repository discovery. This authoring workflow requires a repository containing child skill directories; root-only validation is unavailable.");
        var candidates = ImmutableArray.CreateBuilder<string>();
        var stack = new Stack<(string Path, int Depth)>(); stack.Push((repository, 0));
        var entries = 0;
        while (stack.TryPop(out var frame))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(frame.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxNamespaceEntries) throw new IOException("Authoring discovery exceeds 8192 namespace entries.");
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.')) continue; // hidden conventions excluded by gh's public discoverer; includes .git
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked authoring namespace entry prevents complete candidate capture: " + entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (frame.Depth >= 16) throw new IOException("Authoring discovery exceeds 16 directory levels.");
                    stack.Push((entry, frame.Depth + 1));
                }
                else if (name == "SKILL.md")
                {
                    var relative = Path.GetRelativePath(repository, frame.Path).Replace(Path.DirectorySeparatorChar, '/');
                    var segments = relative.Split('/');
                    var fits = segments.Length == 1 && segments[0] is not ("skills" or "plugins")
                        || segments.Length >= 2 && segments[^2] == "skills" && !segments.Contains("plugins")
                        || segments.Length >= 3 && segments[^3] == "skills" && !segments.Contains("plugins")
                        || segments.Length == 4 && segments[0] == "plugins" && segments[2] == "skills";
                    if (!fits) continue;
                    if (candidates.Count >= MaxCandidates) throw new IOException("Authoring discovery exceeds 128 candidate skills.");
                    candidates.Add(frame.Path);
                }
            }
        }
        return candidates.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool HasEntry(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
