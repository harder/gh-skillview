using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill preview`. Output is plain text (SKILL.md + metadata),
/// surfaced directly in the preview pane (§7.1.C).
public sealed class GhSkillPreviewService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillPreviewService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> PreviewAsync(
        string ghPath,
        string repo,
        string? skillName,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "skill", "preview", repo };
        if (!string.IsNullOrEmpty(skillName))
        {
            args.Add(skillName);
        }

        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.preview", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return $"(preview failed: exit {result.ExitCode})\n\n{result.StdErr}";
        }
        return result.StdOut;
    }
}
