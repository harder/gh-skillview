using System.Collections.Immutable;

namespace SkillView.Gh.Models;

/// Structured output of `gh skill preview`. `gh skill preview`
/// currently emits plain text — SKILL.md body followed by an associated-files
/// listing when present. We keep the raw text verbatim for display, and
/// opportunistically extract the list of associated file names so UI can show
/// them in the detail pane and CLI consumers can emit them as JSON.
public sealed record PreviewResult
{
    public required string Repo { get; init; }
    public required string? SkillName { get; init; }
    public required string? Version { get; init; }

    /// Entire captured stdout, untransformed, suitable for direct display.
    public required string Body { get; init; }

    /// Best-effort extraction of the SKILL.md markdown portion — the content
    /// above the "Associated files" section when `gh` emits one, or the full
    /// body otherwise.
    public required string MarkdownBody { get; init; }

    /// Filenames parsed from an "Associated files" / "Files" / "Bundled files"
    /// section, if `gh` emits one. Empty when none found.
    public required ImmutableArray<string> AssociatedFiles { get; init; }

    public required bool Succeeded { get; init; }
    public required int ExitCode { get; init; }
    public required string? ErrorMessage { get; init; }

    public static PreviewResult Failure(string repo, string? skillName, string? version, int exit, string err) =>
        new()
        {
            Repo = repo,
            SkillName = skillName,
            Version = version,
            Body = err,
            MarkdownBody = err,
            AssociatedFiles = ImmutableArray<string>.Empty,
            Succeeded = false,
            ExitCode = exit,
            ErrorMessage = err,
        };
}
