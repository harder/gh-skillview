using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using SkillView.Subprocess;
using SkillView.Inventory;

namespace SkillView.Maintenance;

/// Fetches an immutable recorded Git tree through gh's authenticated API. A
/// moving branch is not used as evidence of the original installed content.
internal sealed class GhRecordedTreeStager(ProcessRunner runner)
{
    internal async Task<string> StageAsync(string ghPath, string repositoryUrl,
        string treeSha, string destination, CancellationToken cancellationToken)
    {
        using var parent = NativeStateDirectory.OpenOrCreate(Path.GetDirectoryName(destination)!, [], cancellationToken);
        using var writer = parent.OpenFile("tree-stage-writer.lock", true, cancellationToken);
        if (writer.Length != 0) throw new IOException("Staging writer lock must be empty.");
        using var stage = parent.CreateStagingDirectory(Path.GetFileName(destination), cancellationToken);
        await StageIntoAsync(ghPath, repositoryUrl, treeSha, stage, null, cancellationToken).ConfigureAwait(false);
        stage.SealStagingDirectory(493, cancellationToken);
        parent.FlushPublication(cancellationToken);
        return stage.CanonicalPath;
    }

    internal async Task StageIntoAsync(string ghPath, string repositoryUrl, string treeSha,
        NativeStateDirectory destination, Func<byte[], byte[]>? skillTransform, CancellationToken cancellationToken)
    {
        try { await StageCoreAsync(ghPath, repositoryUrl, treeSha, destination, skillTransform, cancellationToken).ConfigureAwait(false); }
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

    private async Task StageCoreAsync(string ghPath, string repositoryUrl,
        string treeSha, NativeStateDirectory destination, Func<byte[], byte[]>? skillTransform, CancellationToken token)
    {
        if (treeSha.Length != 40 || !treeSha.All(char.IsAsciiHexDigit))
            throw new IOException("Recorded tree is not a full Git object identifier.");
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) || uri.Host != "github.com" || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new IOException("Unsupported recorded repository URL.");
        var repo = uri.AbsolutePath.Trim('/');
        if (repo.EndsWith(".git", StringComparison.Ordinal)) repo = repo[..^4];
        if (repo.Split('/').Length != 2) throw new IOException("Recorded repository is ambiguous.");
        var result = await runner.RunAsync(ghPath,
            ["api", "--hostname", "github.com", "--", $"repos/{repo}/git/trees/{treeSha}?recursive=1"],
            cancellationToken: token).ConfigureAwait(false);
        if (result.StdOutTruncated || result.StdErrTruncated) throw new GhSourceRequestException("Recorded tree capture was truncated.", result);
        if (!result.Succeeded) throw new GhSourceRequestException($"Recorded tree is inaccessible: {result.StdErr}", result);
        using var document = ParseTreeResponse(result.StdOut, treeSha);
        var tree = document.RootElement.GetProperty("tree");
        await MaterializeAsync(tree, treeSha, destination, async (blobSha, ct) =>
        {
            var blob = await runner.RunAsync(ghPath,
                ["api", "--hostname", "github.com", "--jq", ".content", "--", $"repos/{repo}/git/blobs/{blobSha}"],
                cancellationToken: ct).ConfigureAwait(false);
            if (blob.StdOutTruncated || blob.StdErrTruncated) throw new GhSourceRequestException("Recorded blob capture was truncated.", blob);
            if (!blob.Succeeded) throw new GhSourceRequestException($"Recorded blob is inaccessible: {blob.StdErr}", blob);
            return Convert.FromBase64String(blob.StdOut);
        }, skillTransform, token).ConfigureAwait(false);
    }

    internal static async Task MaterializeAsync(JsonElement tree, string treeSha, NativeStateDirectory destination,
        Func<string, CancellationToken, Task<byte[]>> readBlob, Func<byte[], byte[]>? skillTransform, CancellationToken token)
    {
        VerifyTreeIdentity(tree, treeSha);
        var frames = new Stack<(string Relative, NativeStateDirectory Directory)>();
        frames.Push(("", destination));
        long totalBytes = 0;
        try
        {
            foreach (var entry in tree.EnumerateArray().OrderBy(entry => entry.GetProperty("path").GetString()
                + (entry.GetProperty("type").GetString() == "tree" ? "/" : ""), StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var relative = entry.GetProperty("path").GetString()!;
                var slash = relative.LastIndexOf('/');
                var parentPath = slash < 0 ? "" : relative[..slash];
                var name = relative[(slash + 1)..];
                while (frames.Peek().Relative != parentPath)
                {
                    if (frames.Count == 1) throw new IOException("Recorded source hierarchy is incomplete.");
                    var completed = frames.Pop();
                    try { completed.Directory.SealStagingDirectory(493, token); }
                    finally { completed.Directory.Dispose(); }
                }
                var parent = frames.Peek().Directory;
                var mode = entry.GetProperty("mode").GetString();
                if (entry.GetProperty("type").GetString() == "tree")
                {
                    frames.Push((relative, parent.CreateStagingDirectory(name, token)));
                    continue;
                }
                var size = entry.GetProperty("size").GetInt64();
                if (size < 0 || size > PayloadSnapshot.MaxBytes - totalBytes || size > 512 * 1024)
                    throw new IOException("Recorded source blob exceeds capture budget.");
                var blobSha = entry.GetProperty("sha").GetString()!;
                var bytes = await readBlob(blobSha, token).ConfigureAwait(false);
                if (bytes.LongLength != size || GitObjectHash("blob", bytes) != blobSha.ToLowerInvariant())
                    throw new IOException("Recorded blob bytes do not match its immutable Git identity and size.");
                totalBytes += bytes.LongLength;
                if (relative == "SKILL.md" && skillTransform is not null) bytes = skillTransform(bytes);
                using var file = parent.OpenFile(name, false, token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle,
                    mode == "100755" ? (UnixFileMode)493 : (UnixFileMode)420);
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                file.Flush(true);
                parent.FlushPublication(token);
            }
            while (frames.Count > 1)
            {
                var completed = frames.Pop();
                try { completed.Directory.SealStagingDirectory(493, token); }
                finally { completed.Directory.Dispose(); }
            }
            destination.FlushPublication(token);
        }
        finally { while (frames.Count > 1) frames.Pop().Directory.Dispose(); }
    }

    internal static string GitObjectHash(string kind, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"{kind} {bytes.Length}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void VerifyTreeIdentity(JsonElement tree, string expected)
    {
        if (tree.GetArrayLength() > PayloadSnapshot.MaxEntries) throw new IOException("Recorded source entry limit exceeded.");
        var entries = new Dictionary<string, (string Mode, string Sha, bool Directory)>(StringComparer.Ordinal);
        foreach (var item in tree.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()!;
            var mode = item.GetProperty("mode").GetString()!;
            var sha = item.GetProperty("sha").GetString()!;
            var parts = path.Split('/');
            var directory = item.GetProperty("type").GetString() == "tree" && mode == "040000";
            if (parts.Length > PayloadSnapshot.MaxDepth || parts.Any(part => part is "" or "." or ".." or ".git"
                    || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl))
                || sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)
                || !directory && (item.GetProperty("type").GetString() != "blob" || mode is not ("100644" or "100755"))
                || !entries.TryAdd(path, (directory ? "40000" : mode, sha.ToLowerInvariant(), directory)))
                throw new IOException("Recorded tree has unsafe, unsupported or duplicate entries.");
        }
        foreach (var path in entries.Keys)
        {
            var slash = path.LastIndexOf('/');
            if (slash >= 0 && (!entries.TryGetValue(path[..slash], out var parent) || !parent.Directory))
                throw new IOException("Recorded tree omits a parent directory.");
        }
        var childrenByParent = entries.GroupBy(entry => entry.Key.LastIndexOf('/') is var slash && slash < 0 ? "" : entry.Key[..entry.Key.LastIndexOf('/')])
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var directory in entries.Where(entry => entry.Value.Directory).Select(entry => entry.Key).Append(""))
        {
            using var bytes = new MemoryStream();
            var children = (childrenByParent.GetValueOrDefault(directory) ?? []).Select(entry => (Name: entry.Key[(entry.Key.LastIndexOf('/') + 1)..], entry.Value));
            foreach (var child in children.OrderBy(entry => Encoding.UTF8.GetBytes(entry.Name + (entry.Value.Directory ? "/" : "")), ByteComparer.Instance))
            {
                bytes.Write(Encoding.UTF8.GetBytes(child.Value.Mode + " " + child.Name + "\0"));
                bytes.Write(Convert.FromHexString(child.Value.Sha));
            }
            if (GitObjectHash("tree", bytes.ToArray()) != (directory.Length == 0 ? expected.ToLowerInvariant() : entries[directory].Sha))
                throw new IOException("Recorded tree listing does not match its immutable Git object identifier.");
        }
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        internal static readonly ByteComparer Instance = new();
        public int Compare(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);
    }

}
