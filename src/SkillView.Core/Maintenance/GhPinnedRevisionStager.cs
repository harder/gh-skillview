using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed record GhPinnedRevisionWitness(string BinaryPath, string BinarySha256, string Version,
    SelectedSkillRevision Selection, string SourceArchiveFingerprint, string OutputArchiveFingerprint,
    ImmutableArray<RecordedGitEntry> GitEntries, ImmutableArray<string> Arguments, string EnvironmentPolicy,
    string ContextFingerprint, string StdOut, string StdErr);

/// Captures the provider's actual pinned installation representation. Resolving
/// a commit does not establish how gh writes that commit into SKILL metadata.
internal sealed class GhPinnedRevisionStager(ProcessRunner runner, Logger logger)
{
    internal const string EnvironmentPolicy = "private-home-config-state-cwd-git-plugin; captured-gh-auth-and-proxies; no OS network or filesystem quota-v1";

    internal async Task<GhPinnedRevisionWitness> StageAsync(string ghPath, NativeStateDirectory preview,
        CapturedPayloadArchive source, CapturedPayloadArchive.Reader reader, string sourceFingerprint,
        ImmutableArray<RecordedGitEntry> gitEntries, SelectedSkillRevision selection, string name, CancellationToken token,
        string? expectedBinaryHash = null)
    {
        token.ThrowIfCancellationRequested(); selection.Request.Validate(); NativeStateDirectory.CheckName(name);
        var process = runner.CaptureContext();
        _ = GhRevisionResolver.Repository(selection.Repository);
        if (selection.Commit is not { Length: 40 } || !selection.Commit.All(char.IsAsciiHexDigit)
            || selection.Tree is not { Length: 40 } || !selection.Tree.All(char.IsAsciiHexDigit)
            || !QuarantineStore.ValidHash(sourceFingerprint)) throw new IOException("Selected staging evidence is incomplete.");
        var binary = GhLocalImportStager.BindBinary(ghPath, token);
        if (expectedBinaryHash is not null && binary.Hash != expectedBinaryHash)
            throw new IOException("The gh executable changed before selected materialization.");
        // Verify every retained source blob before any child admission. The
        // caller's source archive must itself come from immutable Git staging.
        GhRecordedTreeStager.VerifyArchiveTree(source, reader, gitEntries, selection.Tree, token);
        using var home = preview.CreateStagingDirectory("selected-gh-home", token);
        using (var config = home.OpenFile("empty-git-config", false, token)) config.Flush(true);
        using (var temporary = home.CreateStagingDirectory("tmp", token)) temporary.FlushPublication(token);
        using var destination = preview.CreateStagingDirectory("selected-gh-output", token);
        var environment = PrivateEnvironment(process, home.CanonicalPath, binary.Path);
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(90),
            CancellationCallbackReporter.For(logger, "Selected revision gh witness"));
        var version = await Run(["--version"], "Selected gh witness version").ConfigureAwait(false);
        var parts = version.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (version.StdOut.Length > 4096 || parts.Length < 3 || parts[0] != "gh" || parts[1] != "version"
            || !GhBinaryLocator.SatisfiesMinimum(parts[2]))
            throw new GhSourceRequestException("Selected gh witness requires a bounded supported verified gh version.", version);
        var arguments = BuildArguments(destination.CanonicalPath, selection);
        var installed = await Run(arguments, "Private selected revision staging").ConfigureAwait(false);
        var diagnostics = new GhSourceRequestException("Selected staging diagnostics", installed);
        preview.ValidateLocation(token); destination.ValidateLocation(token);
        var children = destination.EnumerateEntries(token).Take(2).ToArray();
        if (children.Length != 1 || children[0].Name != name)
            throw new IOException("Private selected staging did not produce exactly the selected skill.");
        using var outputLease = NativeDirectoryMoveLease.Open(destination.CanonicalPath, Path.Combine(destination.CanonicalPath, name), token);
        _ = CapturedPayloadArchive.Capture(preview, "selected-gh-capture", outputLease, token);
        using var outputState = NativeStateDirectory.OpenOrCreate(Path.Combine(preview.CanonicalPath, "selected-gh-capture"), [], token, allowCreate: false);
        var captured = CapturedPayloadArchive.Load(outputState, null, token);
        ValidateOutput(source.Snapshot, reader, captured.Archive.Snapshot, captured.Archive.OpenReader(outputState), selection, name, token);
        // This witness records actual provider output, including unsupported
        // metadata observations. The publishing caller must separately require
        // complete metadata before admitting any clone or installed transition.
        if (GhLocalImportStager.BinaryHash(binary.Path, token) != binary.Hash)
            throw new IOException("The gh executable changed during selected staging.");
        return new(binary.Path, binary.Hash, version.StdOut.Trim(), selection, sourceFingerprint, captured.Fingerprint,
            gitEntries, arguments, EnvironmentPolicy, process.CaptureContextFingerprint(), diagnostics.StdOut, diagnostics.StdErr);

        async Task<ProcessResult> Run(IReadOnlyList<string> argv, string description)
        {
            try
            {
                var result = await process.RunAsync(binary.Path, argv, home.CanonicalPath, deadline.Token,
                    retainCancellationOutput: true, environment: environment).ConfigureAwait(false);
                GhLocalImportStager.RequireChildResult(result, description, token);
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new GhSourceRequestException(description + " exceeded its private deadline before completion.", "", ""); }
        }
    }

    internal static ImmutableArray<string> BuildArguments(string destination, SelectedSkillRevision selection)
    {
        selection.Request.Validate();
        var repo = GhRevisionResolver.Repository(selection.Repository);
        // A repository-root skill is selected by its observed source path, not
        // by a guessed installed name. All trusted options precede the boundary.
        return ["skill", "install", "--dir", destination, "--pin=" + selection.Request.Selector, "--", repo,
            selection.Subpath.Length == 0 ? "." : selection.Subpath];
    }

    internal static void ValidateOutput(PayloadSnapshot source, CapturedPayloadArchive.Reader sourceReader,
        PayloadSnapshot output, CapturedPayloadArchive.Reader outputReader, SelectedSkillRevision selection, string name, CancellationToken token)
    {
        var actual = output.FrontMatter;
        if (actual is null || actual.Name != name || !actual.Diagnostics.IsEmpty || actual.LocalPath is not null
            || actual.Upstream is null || GhRevisionResolver.Repository(actual.Upstream) != GhRevisionResolver.Repository(selection.Repository)
            || actual.GithubTreeSha != selection.Tree || SkillView.Inventory.Models.SkillSourceIdentity.NormalizeSubpath(actual.GithubPath) != selection.Subpath
            || string.IsNullOrWhiteSpace(actual.GithubRef) || actual.GithubRef.Length > 2048 || actual.GithubRef.Any(char.IsControl)
            || selection.Ref is not null && actual.GithubRef != selection.Ref
            || !actual.Pinned || actual.PinnedRef != selection.Request.Selector
            || output.Entries.Any(entry => entry.Kind == "link" || Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json"))
            throw new IOException("Private selected staging returned inconsistent source, revision, pin or payload evidence.");
        SelectedSkillSourceComparison.Require(GhLocalImportStager.ReadSkill(sourceReader, token), GhLocalImportStager.ReadSkill(outputReader, token),
            sourceReader.Entry("SKILL.md").UnixMode, outputReader.Entry("SKILL.md").UnixMode);
        var expected = source.Entries.Where(entry => entry.Kind == "file" && entry.Path != "SKILL.md").ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var files = output.Entries.Where(entry => entry.Kind == "file" && entry.Path != "SKILL.md").ToArray();
        if (files.Length != expected.Count || files.Any(entry => !expected.TryGetValue(entry.Path, out var before)
            || entry.ContentHash != before.ContentHash || entry.Bytes != before.Bytes
            || entry.UnixMode != before.UnixMode))
            throw new IOException("Private selected staging did not preserve the immutable source files and modes.");
    }

    internal static IReadOnlyDictionary<string, string> PrivateEnvironment(ProcessRunner runner, string home, string binary)
    {
        var captured = runner.CaptureEnvironment();
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // The existing private map deliberately contains both proxy spellings.
        // Assign into the platform comparer to collapse those Windows aliases.
        foreach (var pair in GhLocalImportStager.PrivateEnvironment(home, binary, "")) result[pair.Key] = pair.Value;
        foreach (var key in captured.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))) result[key] = "";
        // Reassert the deliberate private Git context after clearing all
        // inherited redirection, including less common GIT_CONFIG_* variants.
        foreach (var pair in GhLocalImportStager.PrivateEnvironment(home, binary, "").Where(pair => pair.Key.StartsWith("GIT_", StringComparison.Ordinal)))
            result[pair.Key] = pair.Value;
        string? Value(string key) => captured.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        var originalHome = Path.GetFullPath(Value(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? throw new IOException("Selected staging requires a captured HOME context."), runner.EffectiveWorkingDirectory);
        var config = SkillUpdateCoordinator.ResolveGhConfigDirectory(originalHome, Value("GH_CONFIG_DIR"), Value("XDG_CONFIG_HOME"), Value("APPDATA"), OperatingSystem.IsWindows());
        result["GH_CONFIG_DIR"] = Path.GetFullPath(config, runner.EffectiveWorkingDirectory);
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy" })
            result[key] = captured.GetValueOrDefault(key) ?? "";
        result["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "skillview-state");
        result["TEMP"] = result["TMP"] = Path.Combine(home, "tmp");
        result["TMPDIR"] = OperatingSystem.IsWindows() ? Path.Combine(home, "tmp") : "/var/tmp";
        return result;
    }
}
