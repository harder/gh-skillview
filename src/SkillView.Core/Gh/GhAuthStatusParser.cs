using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace SkillView.Gh;

/// Pure parser for `gh auth status` text output. Separated from the subprocess
/// service so tests feed fixtures directly. `gh auth status` has no `--json`
/// mode as of v2.91.0 — text parsing is the only option.
public static partial class GhAuthStatusParser
{
    // "✓ Logged in to github.com account foo (keyring)" (v2.58+)
    // "✓ Logged in to github.com as foo (keyring)"      (earlier formatting)
    [GeneratedRegex(@"Logged in to\s+(?<host>\S+)\s+(?:account|as)\s+(?<user>\S+)", RegexOptions.Compiled)]
    private static partial Regex LoggedInRegex();

    // A host line appears before the indented details; it's a bare hostname
    // at column 0 followed by a colon or newline.
    [GeneratedRegex(@"(?m)^(?<host>[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\s*$", RegexOptions.Compiled)]
    private static partial Regex HostHeaderRegex();

    // "Active account: true" marks the current default host in recent gh.
    [GeneratedRegex(@"Active account:\s*true", RegexOptions.Compiled)]
    private static partial Regex ActiveAccountRegex();

    public static GhAuthStatus Parse(string? stdout, string? stderr, int exitCode)
    {
        // `gh auth status` prints to stderr historically; newer versions use
        // stdout. Concatenate and parse either.
        var raw = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).Trim();
        if (raw.Length == 0)
        {
            return GhAuthStatus.Unknown with { RawOutput = string.Empty };
        }

        var logins = LoggedInRegex().Matches(raw);
        if (logins.Count == 0 || exitCode != 0)
        {
            var hostsNotLoggedIn = ExtractHostHeaders(raw);
            return new GhAuthStatus
            {
                LoggedIn = false,
                ActiveHost = hostsNotLoggedIn.FirstOrDefault(),
                Account = null,
                Hosts = hostsNotLoggedIn,
                RawOutput = raw,
            };
        }

        string? activeHost = null;
        string? activeAccount = null;
        var hosts = ImmutableArray.CreateBuilder<string>();

        foreach (Match login in logins)
        {
            var host = login.Groups["host"].Value;
            var user = login.Groups["user"].Value;
            hosts.Add(host);

            // Look ahead a few lines after this login for an "Active account: true" marker.
            var windowStart = login.Index;
            var windowEnd = Math.Min(raw.Length, login.Index + 400);
            var window = raw[windowStart..windowEnd];
            if (ActiveAccountRegex().IsMatch(window))
            {
                activeHost = host;
                activeAccount = user;
            }
        }

        // If no explicit "Active account: true", first Logged-in host is the
        // effective default — matches older `gh` behavior.
        if (activeHost is null && logins.Count > 0)
        {
            activeHost = logins[0].Groups["host"].Value;
            activeAccount = logins[0].Groups["user"].Value;
        }

        return new GhAuthStatus
        {
            LoggedIn = true,
            ActiveHost = activeHost,
            Account = activeAccount,
            Hosts = hosts.ToImmutable(),
            RawOutput = raw,
        };
    }

    private static ImmutableArray<string> ExtractHostHeaders(string raw)
    {
        var matches = HostHeaderRegex().Matches(raw);
        if (matches.Count == 0) return ImmutableArray<string>.Empty;
        var b = ImmutableArray.CreateBuilder<string>();
        foreach (Match m in matches) b.Add(m.Groups["host"].Value);
        return b.ToImmutable();
    }
}
