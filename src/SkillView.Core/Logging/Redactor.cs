using System.Text.RegularExpressions;

namespace SkillView.Logging;

/// Redacts GitHub tokens, Authorization header values, and URL userinfo from any
/// arbitrary text fragment. Applied once at the log-writer layer (§18.2).
public static partial class Redactor
{
    [GeneratedRegex(@"(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{20,}", RegexOptions.Compiled)]
    private static partial Regex GhTokenRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled)]
    private static partial Regex GhPatRegex();

    [GeneratedRegex(@"(?im)^(Authorization:\s*).+$", RegexOptions.Compiled)]
    private static partial Regex AuthHeaderRegex();

    [GeneratedRegex(@"(https?://)([^/\s:@]+:[^/\s@]+)@", RegexOptions.Compiled)]
    private static partial Regex UrlUserInfoRegex();

    private const string Mask = "[REDACTED]";

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var s = GhTokenRegex().Replace(input, Mask);
        s = GhPatRegex().Replace(s, Mask);
        s = AuthHeaderRegex().Replace(s, m => m.Groups[1].Value + Mask);
        s = UrlUserInfoRegex().Replace(s, m => m.Groups[1].Value + Mask + "@");
        return s;
    }
}
