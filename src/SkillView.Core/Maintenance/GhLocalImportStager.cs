using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Maintenance;

internal sealed class GhLocalImportStager(ProcessRunner runner, Logger logger)
{
    internal const string EnvironmentPolicy = "private-home-config-state-cwd-git-plugin; no-auth; reserved-loopback-http-proxies-v1; no OS network or filesystem quota";

    internal async Task<GhLocalImportWitness> StageAsync(string ghPath, NativeStateDirectory preview,
        CapturedPayloadArchive source, CapturedPayloadArchive.Reader reader, CancellationToken token)
    {
        var boundBinary = BindBinary(ghPath, token);
        var binary = boundBinary.Path;
        var binaryHash = boundBinary.Hash;
        using var sourceMetadata = PayloadMetadataReview.PreserveInstalled(source, reader, token);
        var skill = ReadSkill(reader, token);
        var input = InstalledPayloadMaterializer.Stage(preview, source, reader, sourceMetadata, skill, token, "gh-input");
        var inputPath = Path.Combine(preview.CanonicalPath, "gh-input");
        using (var inputLease = NativeDirectoryMoveLease.Open(preview.CanonicalPath, inputPath, token))
            _ = CapturedPayloadArchive.Capture(preview, "gh-input-capture", inputLease, token);
        using var inputArchive = Open(preview, "gh-input-capture", token);
        var inputFingerprint = CapturedPayloadArchive.Load(inputArchive, null, token).Fingerprint;
        using var home = preview.CreateStagingDirectory("gh-home", token);
        using (var config = home.OpenFile("empty-git-config", false, token)) config.Flush(true);
        using var destination = preview.CreateStagingDirectory("gh-output", token);
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var proxy = "http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndPoint!).Port;
        var environment = PrivateEnvironment(home.CanonicalPath, binary, proxy);
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(90),
            CancellationCallbackReporter.For(logger, "Local reimport gh witness"));
        var version = await Run(["--version"], "Local gh witness version").ConfigureAwait(false);
        var versionParts = version.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (version.StdOut.Length > 4096 || versionParts.Length < 3 || versionParts[0] != "gh" || versionParts[1] != "version" || !GhBinaryLocator.SatisfiesMinimum(versionParts[2]))
            throw new GhSourceRequestException("Local gh witness requires a bounded supported verified gh version.", version);
        var name = source.Snapshot.FrontMatter!.Name!;
        NativeStateDirectory.CheckName(name);
        var arguments = ImmutableArray.Create("skill", "install", "--from-local", "--dir", destination.CanonicalPath, "--", inputPath, name);
        var installed = await Run(arguments, "Private gh local staging").ConfigureAwait(false);
        var diagnostic = new GhSourceRequestException("Successful private gh local staging diagnostics", installed);
        logger.Info("reimport.gh", $"Private local staging stdout: {diagnostic.StdOut}\nstderr: {diagnostic.StdErr}");
        preview.ValidateLocation(token); destination.ValidateLocation(token);
        var children = destination.EnumerateEntries(token).Take(2).ToArray();
        if (children.Length != 1 || children[0].Name != name)
            throw new IOException("Private gh local staging did not produce exactly the selected root skill.");
        var outputPath = Path.Combine(destination.CanonicalPath, name);
        using var outputLease = NativeDirectoryMoveLease.Open(destination.CanonicalPath, outputPath, token);
        var output = CapturedPayloadArchive.Capture(preview, "gh-output-capture", outputLease, token).Snapshot;
        if (output.FrontMatter is not { } metadata || metadata.Name != name || !metadata.Diagnostics.IsEmpty
            || metadata.LocalPath != inputPath || metadata.Pinned || metadata.PinnedRef is not null
            || metadata.Upstream is not null || metadata.GithubRef is not null || metadata.GithubTreeSha is not null || metadata.GithubPath is not null
            || output.Entries.Any(entry => entry.Kind == "link" || Path.GetFileName(entry.Path) is ".skill-lock.json" or "skills-lock.json"))
            throw new IOException("Private gh local staging returned unsupported source, pin or payload evidence.");
        // gh omits empty directories and regenerates modes/YAML. Every other
        // copied file must nevertheless be the exact bounded captured input.
        var expectedFiles = input.Entries.Where(entry => entry.Kind == "file" && entry.Path != "SKILL.md")
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var actualFiles = output.Entries.Where(entry => entry.Kind == "file" && entry.Path != "SKILL.md").ToArray();
        if (actualFiles.Length != expectedFiles.Count || actualFiles.Any(entry => !expectedFiles.TryGetValue(entry.Path, out var expected)
            || entry.ContentHash != expected.ContentHash || entry.Bytes != expected.Bytes))
            throw new IOException("Private gh local staging copied unexpected or changed payload files.");
        using (var inputLease = NativeDirectoryMoveLease.Open(preview.CanonicalPath, inputPath, token))
        {
            var after = inputLease.Capture(token);
            if (after.Fingerprint != input.Fingerprint
                || after.ObjectStateFingerprint != input.ObjectStateFingerprint)
                throw new IOException("The private gh input changed during staging.");
        }
        using var outputArchive = Open(preview, "gh-output-capture", token);
        var outputFingerprint = CapturedPayloadArchive.Load(outputArchive, null, token).Fingerprint;
        if (BinaryHash(binary, token) != binaryHash) throw new IOException("The gh binary changed during its witness run.");
        return new(binary, binaryHash, version.StdOut.Trim(), inputPath, outputPath, inputFingerprint, outputFingerprint,
            arguments, EnvironmentPolicy, diagnostic.StdOut, diagnostic.StdErr);

        async Task<ProcessResult> Run(IReadOnlyList<string> argv, string description)
        {
            try
            {
                var result = await runner.RunAsync(binary, argv, home.CanonicalPath, deadline.Token,
                    retainCancellationOutput: true, environment: environment).ConfigureAwait(false);
                RequireChildResult(result, description, token);
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new GhSourceRequestException(description + " exceeded its private deadline before child completion.", "", "");
            }
        }
    }

    internal static byte[] ReadSkill(CapturedPayloadArchive.Reader reader, CancellationToken token)
    {
        if (reader.Entry("SKILL.md").Bytes > PayloadSnapshot.MaxSkillBytes) throw new IOException("Local SKILL.md exceeds its limit.");
        using var bytes = new MemoryStream(); reader.CopyEntry("SKILL.md", bytes, token); return bytes.ToArray();
    }

    internal static (string Path, string Hash) BindBinary(string path, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        var resolved = PathResolver.IsSymlink(full)
            ? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("The selected gh executable link is missing or inaccessible.")
            : full;
        return CaptureBinary(resolved, token);
    }

    internal static string BinaryHash(string path, CancellationToken token) => CaptureBinary(path, token).Hash;

    private static (string Path, string Hash) CaptureBinary(string path, CancellationToken token)
    {
        string? result = null;
        string? canonical = null;
        RegularFileInspection.Capture(path, item =>
        {
            var input = item.Content ?? throw new IOException("gh must be a regular executable file.");
            canonical = item.Identity.CanonicalPath;
            if (input.Length is <= 0 or > 128 * 1024 * 1024) throw new IOException("gh binary exceeds its witness budget.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested(); var read = input.Read(buffer);
                if (read == 0) break;
                count += read; if (count > 128 * 1024 * 1024) throw new IOException("gh binary grew during capture.");
                hash.AppendData(buffer, 0, read);
            }
            var raw = Convert.ToHexString(hash.GetHashAndReset());
            result = raw.ToLowerInvariant();
            return new PayloadFileEvidence(raw, count);
        }, token);
        return (canonical ?? throw new IOException("No gh executable address was captured."), result ?? throw new IOException("No gh binary witness was captured."));
    }

    internal static void RequireChildResult(ProcessResult result, string description, CancellationToken callerToken)
    {
        if (result.Cancelled)
        {
            var diagnostic = new GhSourceRequestException(description, result);
            if (callerToken.IsCancellationRequested)
                throw new OperationCanceledException(description + " canceled.\nstdout: " + diagnostic.StdOut + "\nstderr: " + diagnostic.StdErr, callerToken);
            throw new GhSourceRequestException(description + " exceeded its private deadline; bounded child diagnostics retained.", result);
        }
        if (!result.Succeeded || result.StdOutTruncated || result.StdErrTruncated)
            throw new GhSourceRequestException(description + " failed or diagnostic coverage was truncated.", result);
    }

    internal static IReadOnlyDictionary<string, string> PrivateEnvironment(string home, string binary, string proxy)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["GH_CONFIG_DIR"] = Path.Combine(home, "gh-config"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "localappdata"),
            ["CODEX_HOME"] = Path.Combine(home, "codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(home, "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
            ["DOTNET_CLI_HOME"] = Path.Combine(home, "dotnet"),
            ["GIT_DIR"] = Path.Combine(home, "git-dir"),
            ["GIT_WORK_TREE"] = home,
            ["GIT_COMMON_DIR"] = Path.Combine(home, "git-dir"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(home, "empty-git-config"),
            ["GIT_CONFIG_SYSTEM"] = Path.Combine(home, "empty-git-config"),
            ["GH_TOKEN"] = "",
            ["GITHUB_TOKEN"] = "",
            ["GH_ENTERPRISE_TOKEN"] = "",
            ["GITHUB_ENTERPRISE_TOKEN"] = "",
            ["GH_HOST"] = "github.com",
            ["GH_PATH"] = binary,
            ["GH_EXTENSION"] = "1",
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1",
            ["GH_TELEMETRY"] = "false"
        };
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) result[key] = proxy;
        result["NO_PROXY"] = result["no_proxy"] = "";
        return result;
    }

    private static NativeStateDirectory Open(NativeStateDirectory parent, string name, CancellationToken token) =>
        NativeStateDirectory.OpenOrCreate(Path.Combine(parent.CanonicalPath, name), [], token, allowCreate: false);
}
