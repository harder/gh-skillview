using System.Text.Json;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

internal sealed record ResolvedSkillRevision(string Repository, string Ref, string Commit, string Tree, string Subpath);
internal sealed record GhApiPayload(int Status, string Body)
{
    internal string StdOut { get; init; } = "";
    internal string StdErr { get; init; } = "";
}

/// Resolves the same latest-release/default-branch policy as gh, once. Every
/// subsequent tree request is addressed by a full immutable Git object ID.
internal sealed class GhRevisionResolver
{
    private readonly Func<string, CancellationToken, Task<GhApiPayload>> request;
    internal GhRevisionResolver(ProcessRunner runner, string ghPath) : this(async (endpoint, token) =>
    {
        var result = await runner.RunAsync(ghPath, ["api", "--include", "--hostname", "github.com", "--", endpoint],
            cancellationToken: token).ConfigureAwait(false);
        return CaptureResponse(result);
    })
    { }
    internal static GhApiPayload CaptureResponse(ProcessResult result)
    {
        if (result.StdOutTruncated || result.StdErrTruncated) throw new GhSourceRequestException("Revision response exceeded capture limits.", result);
        GhApiPayload payload;
        try { payload = ParseResponse(result.StdOut); }
        catch (IOException ex) { throw new GhSourceRequestException(ex.Message, result); }
        if (!result.Succeeded && payload.Status is >= 200 and < 300)
            throw new GhSourceRequestException("Revision request failed: " + result.StdErr, result);
        return payload with { StdOut = result.StdOut, StdErr = result.StdErr };
    }
    internal GhRevisionResolver(Func<string, CancellationToken, Task<GhApiPayload>> request) => this.request = request;

    internal async Task<ResolvedSkillRevision> ResolveLatestAsync(string repositoryUrl, string? sourcePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var repo = Repository(repositoryUrl);
        if (sourcePath is null) throw new IOException("An observed source subpath is required; update does not guess from a skill name.");
        // gh accepts a recorded directory, a trailing slash, or its SKILL.md.
        var path = SkillSourceIdentity.NormalizeSubpath(sourcePath)
            ?? throw new IOException("The recorded source subpath is unsupported.");
        if (path.Length > 4096 || path.Split('/').Length > 16
            || path.Length > 0 && path.Split('/').Any(part => part is "" or "." or ".." or ".git"
                || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl)))
            throw new IOException("The recorded source subpath is unsupported.");
        try
        {
            var release = await request($"repos/{repo}/releases/latest", token).ConfigureAwait(false);
            string reference;
            string commit;
            if (release.Status == 404 || release.Status == 200 && string.IsNullOrEmpty(Text(release.Body, "tag_name")))
            {
                var repository = await Get($"repos/{repo}", token).ConfigureAwait(false);
                var branch = RequiredText(repository, "default_branch");
                reference = "refs/heads/" + branch;
                commit = await ResolveRef("heads", branch).ConfigureAwait(false);
            }
            else
            {
                RequireSuccess(release);
                var tag = RequiredText(release.Body, "tag_name");
                reference = "refs/tags/" + tag;
                commit = await ResolveRef("tags", tag).ConfigureAwait(false);
            }
            var commitBody = await Get($"repos/{repo}/git/commits/{commit}", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(commitBody);
            if (document.RootElement.GetProperty("sha").GetString() != commit) throw new IOException("Resolved commit identity disagrees.");
            var tree = FullId(document.RootElement.GetProperty("tree").GetProperty("sha").GetString());
            foreach (var component in path.Length == 0 ? [] : path.Split('/'))
            {
                using var listing = JsonDocument.Parse(await Get($"repos/{repo}/git/trees/{tree}", token).ConfigureAwait(false));
                var root = listing.RootElement;
                if (root.GetProperty("sha").GetString() != tree || root.GetProperty("truncated").GetBoolean())
                    throw new IOException("Source subpath tree coverage is incomplete.");
                var items = root.GetProperty("tree");
                if (items.GetArrayLength() > PayloadSnapshot.MaxEntries) throw new IOException("Source subpath tree entry limit exceeded.");
                var selected = items.EnumerateArray().Where(item => item.GetProperty("path").GetString() == component).ToArray();
                if (selected.Length != 1 || selected[0].GetProperty("type").GetString() != "tree"
                    || selected[0].GetProperty("mode").GetString() != "040000")
                    throw new IOException("The recorded source subpath is absent or is no longer a directory; review a source migration.");
                tree = FullId(selected[0].GetProperty("sha").GetString());
            }
            token.ThrowIfCancellationRequested();
            return new(repositoryUrl, reference, commit, tree, path);

            async Task<string> ResolveRef(string kind, string name)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Any(char.IsControl))
                    throw new IOException("Source ref is invalid.");
                var body = await Get($"repos/{repo}/git/ref/{kind}/{Uri.EscapeDataString(name)}", token).ConfigureAwait(false);
                using var refDocument = JsonDocument.Parse(body);
                if (refDocument.RootElement.GetProperty("ref").GetString() != "refs/" + kind + "/" + name)
                    throw new IOException("Source reference response disagrees with the selected ref.");
                var value = refDocument.RootElement.GetProperty("object");
                for (var depth = 0; depth < 8; depth++)
                {
                    var sha = FullId(value.GetProperty("sha").GetString());
                    if (value.GetProperty("type").GetString() == "commit") return sha;
                    if (kind != "tags" || value.GetProperty("type").GetString() != "tag")
                        throw new IOException("Source ref does not resolve to a commit.");
                    using var tag = JsonDocument.Parse(await Get($"repos/{repo}/git/tags/{sha}", token).ConfigureAwait(false));
                    if (tag.RootElement.GetProperty("sha").GetString() != sha) throw new IOException("Annotated tag identity disagrees.");
                    value = tag.RootElement.GetProperty("object").Clone();
                }
                throw new IOException("Annotated tag depth limit exceeded.");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new IOException("Source revision response is malformed or unsupported.", ex); }
    }

    private async Task<string> Get(string endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = await request(endpoint, token).ConfigureAwait(false);
        RequireSuccess(response);
        return response.Body;
    }
    private static void RequireSuccess(GhApiPayload response)
    {
        if (response.Status != 200) throw new GhSourceRequestException(
            $"Source request failed (HTTP {response.Status}); no fallback or payload change was admitted.", response.StdOut, response.StdErr);
        if (response.Body.Length > 1024 * 1024) throw new IOException("Source response byte limit exceeded.");
    }
    private static string? Text(string body, string property)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }
    private static string RequiredText(string body, string property) => Text(body, property) is { Length: > 0 } value
        ? value : throw new IOException("Source response is missing " + property + ".");
    private static string FullId(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit)
        ? value.ToLowerInvariant() : throw new IOException("Source did not provide a full Git object identifier.");
    internal static string Repository(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
            throw new IOException("Only unambiguous GitHub repository URLs are supported.");
        var repo = uri.AbsolutePath.Trim('/');
        if (repo.EndsWith(".git", StringComparison.Ordinal)) repo = repo[..^4];
        var parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".."
            || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
            throw new IOException("Repository URL is ambiguous.");
        return repo;
    }
    internal static GhApiPayload ParseResponse(string output)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var line = normalized.AsSpan(0, Math.Max(0, normalized.IndexOf('\n'))).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (end < 0 || line.Length < 2 || !line[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(line[1], out var status) || status is < 100 or > 599)
            throw new IOException("GitHub CLI did not return a complete HTTP status and body; no ref fallback was admitted.");
        return new(status, normalized[(end + 2)..]);
    }
}
