using SkillView.Gh;

namespace SkillView.Diagnostics;

/// Composite environment snapshot used by Doctor (CLI + TUI) and startup
/// checks. Built by `EnvironmentProbe`.
public sealed record EnvironmentReport
{
    public required string? GhPath { get; init; }
    public required string? GhVersionRaw { get; init; }
    public required SemVer? GhVersion { get; init; }
    public required bool GhMeetsMinimum { get; init; }
    public required GhAuthStatus Auth { get; init; }
    public required CapabilityProfile Capabilities { get; init; }
    public required string? LogDirectory { get; init; }

    public bool GhFound => GhPath is not null;

    /// True when we have a usable baseline: gh present, ≥ minimum version,
    /// `gh skill` subcommand responds. Auth state is reported but not required
    /// for the baseline to be "ok" because local inventory works offline.
    public bool BaselineOk => GhFound && GhMeetsMinimum && Capabilities.SkillSubcommandPresent;
}
