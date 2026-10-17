using System.Text.Json;

namespace SkillView.Maintenance;

internal sealed record SelectedRevisionRequest(string Kind, string Value, bool Pin)
{
    internal void Validate()
    {
        if (Kind == "commit")
        {
            if (Value is not { Length: 40 } || !Value.All(char.IsAsciiHexDigit))
                throw new IOException("Select a full 40-character commit identifier.");
            return;
        }
        if (Kind != "tag" || string.IsNullOrWhiteSpace(Value) || Value.Length > 1024 || Value == "@"
            || Value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c))
            || Value.Contains("..", StringComparison.Ordinal) || Value.Contains("@{", StringComparison.Ordinal)
            || Value.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            throw new IOException("Select an unambiguous Git tag or a full commit; branch tracking is unavailable.");
    }
    internal string Selector => Kind == "commit" ? Value.ToLowerInvariant() : Value;
}

// A full commit has no invented symbolic ref. The provider's installed ref is
// observed separately during private materialization, not inferred here.
internal sealed record SelectedSkillRevision(SelectedRevisionRequest Request, string Repository,
    string? Ref, string Commit, string Tree, string Subpath);

internal sealed partial class GhRevisionResolver
{
    internal async Task<SelectedSkillRevision> ResolveSelectedAsync(string repositoryUrl, string? sourcePath,
        SelectedRevisionRequest selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        selection.Validate();
        var repo = Repository(repositoryUrl);
        var path = SourcePath(sourcePath);
        try
        {
            var commit = selection.Kind == "commit" ? selection.Selector
                : await ResolveRef(repo, "tags", selection.Value, token).ConfigureAwait(false);
            var tree = await ResolveTree(repo, commit, path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(selection, repositoryUrl, selection.Kind == "tag" ? "refs/tags/" + selection.Value : null,
                commit, tree, path);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new IOException("Selected revision response is malformed or unsupported.", ex); }
    }

    internal async Task VerifySelectedAsync(SelectedSkillRevision frozen, CancellationToken token)
    {
        var current = await ResolveSelectedAsync(frozen.Repository, frozen.Subpath, frozen.Request, token).ConfigureAwait(false);
        if (current != frozen) throw new IOException("The selected revision moved during capture; review a fresh immutable capture.");
    }
}
