using System.Text.Json;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

/// Fetches an immutable recorded Git tree through gh's authenticated API. A
/// moving branch is not used as evidence of the original installed content.
internal sealed class GhRecordedTreeStager(ProcessRunner runner)
{
    internal async Task<string> StageAsync(string ghPath, string repositoryUrl,
        string treeSha, string destination, CancellationToken cancellationToken)
    {
        try { return await StageCoreAsync(ghPath, repositoryUrl, treeSha, destination, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or OverflowException)
        { throw new IOException("Recorded source response has an unsupported or malformed schema.", ex); }
    }

    internal static JsonDocument ParseTreeResponse(string json, string expectedSha)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("sha").GetString() != expectedSha
                || root.GetProperty("truncated").GetBoolean()
                || root.GetProperty("tree").ValueKind != JsonValueKind.Array)
                throw new IOException("Recorded tree response does not establish complete coverage.");
            foreach (var item in root.GetProperty("tree").EnumerateArray())
            {
                if (string.IsNullOrWhiteSpace(item.GetProperty("path").GetString())
                    || string.IsNullOrWhiteSpace(item.GetProperty("type").GetString())
                    || string.IsNullOrWhiteSpace(item.GetProperty("mode").GetString())
                    || string.IsNullOrWhiteSpace(item.GetProperty("sha").GetString()))
                    throw new IOException("Recorded tree entry is incomplete.");
                if (item.GetProperty("type").GetString() == "blob"
                    && (item.GetProperty("size").ValueKind != JsonValueKind.Number
                        || !item.GetProperty("size").TryGetInt64(out _)))
                    throw new IOException("Recorded blob size is invalid.");
            }
            return document;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or OverflowException or IOException)
        {
            document?.Dispose();
            throw new IOException("Recorded source response has an unsupported or malformed schema.", ex);
        }
    }

    private async Task<string> StageCoreAsync(string ghPath, string repositoryUrl,
        string treeSha, string destination, CancellationToken cancellationToken)
    {
        if (treeSha.Length != 40 || !treeSha.All(char.IsAsciiHexDigit))
            throw new IOException("Recorded tree is not a full Git object identifier.");
        var uri = new Uri(repositoryUrl);
        if (uri.Host != "github.com" || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new IOException("Unsupported recorded repository URL.");
        var repo = uri.AbsolutePath.Trim('/');
        if (repo.EndsWith(".git", StringComparison.Ordinal)) repo = repo[..^4];
        if (repo.Split('/').Length != 2) throw new IOException("Recorded repository is ambiguous.");
        var result = await runner.RunAsync(ghPath,
            ["api", "--hostname", "github.com", "--", $"repos/{repo}/git/trees/{treeSha}?recursive=1"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException($"Recorded tree is inaccessible: {result.StdErr}");
        var document = ParseTreeResponse(result.StdOut, treeSha);
        using (document)
        {
            var root = document.RootElement;
            if (root.GetProperty("sha").GetString() != treeSha
                || !root.TryGetProperty("truncated", out var truncated) || truncated.GetBoolean()
                || !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
                throw new IOException("Recorded tree response does not establish complete coverage.");
            if (tree.GetArrayLength() > PayloadSnapshot.MaxEntries)
                throw new IOException("Recorded source entry limit exceeded.");
            Directory.CreateDirectory(destination);
            long totalBytes = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in tree.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = entry.GetProperty("path").GetString() ?? throw new IOException("Source path is missing.");
                var parts = relative.Split('/');
                if (parts.Length > PayloadSnapshot.MaxDepth || parts.Any(part => part is "" or "." or ".."
                        || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl)) || !seen.Add(relative))
                    throw new IOException("Recorded tree contains an unsafe or duplicate path.");
                var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                var kind = entry.GetProperty("type").GetString();
                var mode = entry.GetProperty("mode").GetString();
                if (kind == "tree" && mode == "040000")
                {
                    Directory.CreateDirectory(path);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    continue;
                }
                if (kind != "blob" || mode is not ("100644" or "100755"))
                    throw new IOException("Recorded source contains a link, submodule or unsupported mode; use a reviewed source plan.");
                var size = entry.GetProperty("size").GetInt64();
                if (size < 0 || size > PayloadSnapshot.MaxBytes - totalBytes || size > 512 * 1024)
                    throw new IOException("Recorded source blob exceeds capture budget.");
                var blobSha = entry.GetProperty("sha").GetString();
                if (blobSha is null || blobSha.Length != 40 || !blobSha.All(char.IsAsciiHexDigit))
                    throw new IOException("Recorded source blob identity is invalid.");
                var blob = await runner.RunAsync(ghPath,
                    ["api", "--hostname", "github.com", "--jq", ".content", "--", $"repos/{repo}/git/blobs/{blobSha}"],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!blob.Succeeded) throw new IOException($"Recorded blob is inaccessible: {blob.StdErr}");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(blob.StdOut); }
                catch (FormatException ex) { throw new IOException("Recorded blob response is incomplete or invalid.", ex); }
                if (bytes.LongLength != size) throw new IOException("Recorded blob length changed or was truncated.");
                totalBytes += bytes.LongLength;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                    mode == "100755" ? (UnixFileMode)Convert.ToInt32("755", 8) : (UnixFileMode)Convert.ToInt32("644", 8));
            }
        }
        return destination;
    }
}
