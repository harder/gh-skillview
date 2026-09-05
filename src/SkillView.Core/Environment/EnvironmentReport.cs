using SkillView.Gh;

namespace SkillView.Diagnostics;

/// Composite environment snapshot used by Doctor (CLI + TUI) and startup
/// checks. Built by `EnvironmentProbe`.
///
/// SkillView requires gh ≥ 2.97.0 (see <see cref="GhBinaryLocator.MinimumVersion"/>),
/// which guarantees the full `gh skill` surface — `list`/`install --all`/
/// `update --all` and their flags — so there is no per-flag capability probe:
/// meeting the minimum implies the feature set.
public sealed record EnvironmentReport
{
    public required string? GhPath { get; init; }
    public required string? GhVersionRaw { get; init; }
    public required SemVer? GhVersion { get; init; }
    public required bool GhMeetsMinimum { get; init; }
    public required GhAuthStatus Auth { get; init; }

    /// True when `gh skill --help` responds — a cheap smoke check that the
    /// preview `skill` command is compiled into this `gh` build.
    public required bool GhSkillAvailable { get; init; }
    public required string? LogDirectory { get; init; }

    public bool GhFound => GhPath is not null;

    /// True when we have a usable baseline: gh present, ≥ minimum version,
    /// `gh skill` subcommand responds. Auth state is reported but not required
    /// for the baseline to be "ok" because local inventory works offline.
    public bool BaselineOk => GhFound && GhMeetsMinimum && GhSkillAvailable;
}
