namespace SkillView.Inventory.Models;

/// Source identity never derives from a display name. Recorded provenance is
/// evidence about origin; it is separate from a payload review or native owner.
public sealed record SkillSourceIdentity(string Provider, string? Origin, string? Subpath,
    string Evidence, bool Conflicted)
{
    public string? Key
    {
        get
        {
            if (Origin is null || Conflicted || (Provider == "repository" && Subpath is null)) return null;
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Provider); writer.Write(Origin); writer.Write(Subpath is not null);
                if (Subpath is not null) writer.Write(Subpath);
            }
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        }
    }

    public static SkillSourceIdentity From(InstalledSkill skill)
    {
        if (skill.FrontMatter.LocalPath is { Length: > 0 } local)
        {
            try
            {
                return new("local", Path.IsPathRooted(local) ? PathIdentity.Normalize(local) : null, null,
                    Path.IsPathRooted(local) ? "recorded-local-path" : "relative-local-path-unresolved", !skill.MetadataConflicts.IsEmpty);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return new("local", null, null, "invalid-recorded-local-path", !skill.MetadataConflicts.IsEmpty); }
        }
        if (skill.FrontMatter.Upstream is { Length: > 0 } remote)
            return new("repository", NormalizeRepository(remote), NormalizeSubpath(skill.FrontMatter.GithubPath),
                "recorded-tracking", !skill.MetadataConflicts.IsEmpty);
        var claims = skill.AllOwnershipClaims.ToArray();
        if (claims.Length > 1)
        {
            var sources = claims.Select(claim => From(skill with { Package = claim, OwnershipClaims = [] }))
                .DistinctBy(source => (source.Provider, source.Origin, source.Subpath, source.Conflicted)).ToArray();
            return sources.Length == 1 ? sources[0]
                : new("unknown", null, null, "conflicting-owner-sources", true);
        }
        if ((skill.Package ?? skill.AllOwnershipClaims.FirstOrDefault()) is { } package)
        {
            if (package.ReadOnly || package.SourceType == "unknown")
                return new("unknown", null, null, "unresolved-owner-record", !skill.MetadataConflicts.IsEmpty);
            if (package.SourceType == "local")
            {
                try
                {
                    var origin = Path.IsPathRooted(package.Source) ? PathIdentity.Normalize(package.Source)
                        : package.Provider == "vercel-project" && package.ContextRoot is { } context
                            ? Path.GetFullPath(package.Source, context) : null;
                    return new("local", origin, null, origin is null ? "relative-local-path-unresolved" : "contextual-lock", !skill.MetadataConflicts.IsEmpty);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { return new("local", null, null, "invalid-recorded-local-path", !skill.MetadataConflicts.IsEmpty); }
            }
            var locator = package.SourceUrl ?? package.Source;
            if (package.SourceType == "github" && package.Source.Split('/').Length == 2
                && (!Uri.TryCreate(package.SourceUrl, UriKind.Absolute, out var uri) || uri.Host == "github.com"))
                locator = "https://github.com/" + package.Source;
            return new(package.SourceType is "github" or "git" ? "repository" : package.SourceType,
                package.SourceType is "github" or "git" ? NormalizeRepository(locator) : locator,
                NormalizeSubpath(package.SkillPath), "contextual-lock", !skill.MetadataConflicts.IsEmpty);
        }
        return new("unknown", null, null, "unknown", !skill.MetadataConflicts.IsEmpty);
    }

    internal static string NormalizeRepository(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return value.Trim();
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) path = path.ToLowerInvariant();
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{path}{uri.Query}{uri.Fragment}";
    }

    internal static string? NormalizeSubpath(string? value)
    {
        if (value is null) return null;
        if (value.StartsWith('/') || value.Contains('\\') || value.Any(char.IsControl)
            || value.Split('/').Any(part => part == "..")) return null;
        var path = value.TrimEnd('/');
        if (path == ".") return "";
        if (path.EndsWith("/SKILL.md", StringComparison.Ordinal)) path = path[..^9];
        return path == "SKILL.md" ? "" : path;
    }
}

public sealed record SkillRevision(string? AuthoredVersion, string? RepositoryRef,
    string? RecordedTree, string? PinnedRef, string? PayloadFingerprint,
    string? OwnerRecordedHash = null, string? OwnerHashKind = null, string? DistributorVersion = null);

public sealed record PhysicalSkillInstallation(string InstallationId, string Path,
    string Materialization, IReadOnlyList<SkillPackage> OwnerRecords);
