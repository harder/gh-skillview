using System.IO;

namespace SkillView.Inventory;

/// <summary>
/// Central path equality, keying, and containment semantics. Case behavior is
/// detected from an existing entry when possible so case-insensitive macOS and
/// Windows volumes deduplicate aliases without collapsing distinct entries on
/// Linux or in Windows case-sensitive directories.
/// </summary>
public static class PathIdentity
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    public static string NormalizeKey(string path)
    {
        var normalized = Normalize(path);
        return NormalizeKey(normalized, IsCaseSensitive(normalized));
    }

    internal static string NormalizeKey(string path, bool caseSensitive)
    {
        var normalized = Normalize(path);
        return caseSensitive ? normalized : normalized.ToUpperInvariant();
    }

    public static bool Equals(string left, string right)
    {
        var leftNormalized = Normalize(left);
        var rightNormalized = Normalize(right);
        var comparison = IsCaseSensitive(leftNormalized) && IsCaseSensitive(rightNormalized)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(leftNormalized, rightNormalized, comparison);
    }

    public static bool IsInside(string candidate, string root)
    {
        var candidateNormalized = Normalize(candidate);
        var rootNormalized = Normalize(root);
        var comparison = ComparisonFor(rootNormalized);
        var rootPrefix = rootNormalized.EndsWith('/')
            ? rootNormalized
            : rootNormalized + "/";
        return string.Equals(candidateNormalized, rootNormalized, comparison)
            || candidateNormalized.StartsWith(rootPrefix, comparison);
    }

    internal static StringComparison ComparisonFor(string path) =>
        IsCaseSensitive(path)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    internal static bool IsCaseSensitive(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }

        if (EntryExists(full))
        {
            if (TryProbeExistingEntry(full, out var caseSensitive))
            {
                return caseSensitive;
            }

            // A root has no parent/name to flip, and a non-ASCII leaf may not
            // have a flippable spelling. Probe another entry in the same
            // containing directory rather than climbing and measuring the
            // wrong directory's lookup semantics.
            var containingDirectory = Path.GetDirectoryName(full);
            if (containingDirectory is null && Directory.Exists(full))
            {
                containingDirectory = full;
            }
            if (containingDirectory is not null
                && TryProbeDirectory(containingDirectory, out caseSensitive))
            {
                return caseSensitive;
            }

            return true;
        }

        // For a missing direct child, only its containing directory determines
        // whether case variants can be distinct. Never probe that directory's
        // name in its parent: Windows case sensitivity is configurable per
        // directory. If the parent itself is missing or empty/unprobeable,
        // preserve spellings conservatively instead of collapsing distinct
        // paths into one key.
        var parent = Path.GetDirectoryName(full);
        return parent is null
            || !Directory.Exists(parent)
            || !TryProbeDirectory(parent, out var parentCaseSensitive)
            || parentCaseSensitive;
    }

    private static bool TryProbeDirectory(string directory, out bool caseSensitive)
    {
        caseSensitive = true;
        IEnumerator<string>? entries = null;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            while (entries.MoveNext())
            {
                if (TryProbeExistingEntry(entries.Current, out caseSensitive))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            entries?.Dispose();
        }

        return false;
    }

    private static bool TryProbeExistingEntry(string path, out bool caseSensitive)
    {
        caseSensitive = true;
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        var alternateName = FlipAsciiCase(name);
        if (alternateName is null)
        {
            return false;
        }

        var alternatePath = Path.Combine(parent, alternateName);
        if (!EntryExists(alternatePath))
        {
            caseSensitive = true;
            return true;
        }

        // Both spellings resolve. On an insensitive directory they name one
        // entry; on a sensitive directory they can be two distinct entries.
        // Targeted case-sensitive enumeration distinguishes those cases
        // without repeatedly materializing a large parent directory.
        try
        {
            caseSensitive = HasExactEntry(parent, name)
                && HasExactEntry(parent, alternateName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The alternate spelling resolving is the strongest available
            // signal when exact directory enumeration is unavailable.
            caseSensitive = false;
            return true;
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasExactEntry(string parent, string name)
    {
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseSensitive,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
        };
        return Directory.EnumerateFileSystemEntries(parent, name, options)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.Ordinal));
    }

    private static string? FlipAsciiCase(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            char replacement;
            if (ch is >= 'a' and <= 'z') replacement = char.ToUpperInvariant(ch);
            else if (ch is >= 'A' and <= 'Z') replacement = char.ToLowerInvariant(ch);
            else continue;

            return value[..i] + replacement + value[(i + 1)..];
        }

        return null;
    }
}
