using System.Collections.Immutable;

namespace SkillView.Gh;

/// Parsed snapshot of `gh auth status`.
public sealed record GhAuthStatus
{
    public required bool LoggedIn { get; init; }
    public required string? ActiveHost { get; init; }
    public required string? Account { get; init; }
    public required ImmutableArray<string> Hosts { get; init; }
    public required string? RawOutput { get; init; }

    public static GhAuthStatus Unknown { get; } = new()
    {
        LoggedIn = false,
        ActiveHost = null,
        Account = null,
        Hosts = ImmutableArray<string>.Empty,
        RawOutput = null,
    };
}
