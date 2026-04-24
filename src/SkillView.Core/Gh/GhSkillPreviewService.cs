using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill preview`. Output is plain text (SKILL.md + associated
/// files). The adapter keeps the raw body for direct display and also runs
/// a tolerant section-split so UI panes and the CLI `--json` output can
/// distinguish the markdown body from the associated-files listing
/// for preview operations.
public sealed class GhSkillPreviewService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillPreviewService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<PreviewResult> PreviewAsync(
        string ghPath,
        string repo,
        string? skillName,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgs(repo, skillName, version);
        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var err = result.StdErr.Trim();
            _logger.Warn("gh.skill.preview", $"exit={result.ExitCode} err={err}");
            return PreviewResult.Failure(repo, skillName, version, result.ExitCode, err);
        }

        var (markdown, files) = Split(result.StdOut);
        return new PreviewResult
        {
            Repo = repo,
            SkillName = skillName,
            Version = version,
            Body = result.StdOut,
            MarkdownBody = markdown,
            AssociatedFiles = files,
            Succeeded = true,
            ExitCode = 0,
            ErrorMessage = null,
        };
    }

    internal static IReadOnlyList<string> BuildArgs(string repo, string? skillName, string? version)
    {
        var args = new List<string> { "skill", "preview" };
        if (!string.IsNullOrEmpty(version))
        {
            // `gh skill preview <repo>@<ref>` — versioned preview per the
            // cli/cli manual. The suffix is concatenated onto the repo
            // identifier, not passed as a separate flag.
            args.Add($"{repo}@{version}");
        }
        else
        {
            args.Add(repo);
        }
        if (!string.IsNullOrEmpty(skillName))
        {
            args.Add(skillName);
        }
        return args;
    }

    /// Tolerant split: strips the file-tree preamble that `gh skill preview`
    /// emits before the actual skill content, and captures any "Associated
    /// files" section at the end. The preamble is a directory tree followed
    /// by a `── filename ──` separator line.
    internal static (string Markdown, ImmutableArray<string> Files) Split(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, ImmutableArray<string>.Empty);
        }

        var lines = body.Replace("\r\n", "\n").Split('\n');

        // Strip leading file-tree preamble: look for a `── filename ──`
        // separator line and skip everything up to and including it.
        int contentStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (IsFileContentSeparator(trimmed))
            {
                contentStart = i + 1;
                break;
            }
        }

        // Skip blank lines after the separator
        while (contentStart < lines.Length && lines[contentStart].Trim().Length == 0)
        {
            contentStart++;
        }

        int headingIndex = -1;
        for (var i = contentStart; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart('#', ' ', '\t').TrimEnd(':', ' ', '\t');
            if (IsFileSectionHeading(trimmed))
            {
                headingIndex = i;
                break;
            }
        }

        if (headingIndex < 0)
        {
            var markdown = string.Join('\n', lines.Skip(contentStart)).TrimEnd();
            return (markdown, ImmutableArray<string>.Empty);
        }

        var markdownContent = string.Join('\n', lines.Skip(contentStart).Take(headingIndex - contentStart)).TrimEnd();
        var files = ImmutableArray.CreateBuilder<string>();
        for (var i = headingIndex + 1; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0) continue;

            // Accept `- path`, `* path`, `path` (one per line). Stop on the
            // next heading-shaped line so we don't over-capture trailing
            // prose.
            if (trimmed.StartsWith('#')) break;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var name = trimmed[2..].Trim();
                if (name.Length > 0) files.Add(name);
                continue;
            }

            // Treat bare lines as filenames only when they look like paths
            // (no spaces, or a recognizable extension); otherwise skip so
            // closing prose isn't captured as a file.
            if (LooksLikeFilename(trimmed))
            {
                files.Add(trimmed);
            }
        }

        return (markdownContent, files.ToImmutable());
    }

    /// Detects `── filename.ext ──` separator lines emitted by `gh skill preview`.
    /// These use em-dashes (U+2500 BOX DRAWINGS LIGHT HORIZONTAL or U+2014 EM DASH)
    /// or regular dashes around a filename.
    private static bool IsFileContentSeparator(string trimmed)
    {
        // Pattern: starts and ends with dash-like characters, has a filename in the middle
        // Examples: "── SKILL.md ──", "--- SKILL.md ---"
        if (trimmed.Length < 5) return false;
        var hasLeadingDashes = trimmed[0] == '─' || trimmed[0] == '—' || trimmed[0] == '-';
        var hasTrailingDashes = trimmed[^1] == '─' || trimmed[^1] == '—' || trimmed[^1] == '-';
        if (!hasLeadingDashes || !hasTrailingDashes) return false;
        // Extract middle part — should contain a filename-like string
        var inner = trimmed.Trim('─', '—', '-', ' ', '\t');
        return inner.Length > 0 && !inner.Contains('\n') && LooksLikeFilename(inner);
    }

    private static bool IsFileSectionHeading(string trimmed)
    {
        return trimmed.Equals("associated files", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("bundled files", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("files", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("additional files", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFilename(string s)
    {
        if (s.Contains(' ')) return false;
        return s.Contains('.') || s.Contains('/');
    }
}
