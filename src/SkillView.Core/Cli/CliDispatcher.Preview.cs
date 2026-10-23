using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Gh.Models;
using SkillView.Ui;
using Terminal.Gui.Views;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> PreviewAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParsePreviewArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: preview requires a repo (OWNER/REPO)");
            Console.Error.WriteLine("usage: skillview preview <owner/repo> [<skill-name>] [--version <ref>] [--allow-hidden-dirs] [--rendered] [--json]");
            return ExitCodes.InvalidUsage;
        }

        if (!string.IsNullOrEmpty(parsed.Version) && string.IsNullOrEmpty(parsed.SkillName))
        {
            Console.Error.WriteLine("skillview: preview --version requires a skill selector");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.BaselineOk)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var preview = await services.PreviewService.PreviewAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            parsed.Version,
            parsed.AllowHiddenDirs,
            cancellationToken
        ).ConfigureAwait(false);

        if (!preview.Succeeded)
        {
            Console.Error.WriteLine($"skillview: preview failed (exit {preview.ExitCode}): {preview.ErrorMessage}");
            return ExitCodes.EnvironmentError;
        }

        if (parsed.Json) WritePreviewJson(preview);
        else Console.Out.WriteLine(RenderPreviewText(preview, parsed.Rendered));

        return ExitCodes.Success;
    }

    internal record ParsedPreviewArgs(string? Repo, string? SkillName, string? Version, bool AllowHiddenDirs, bool Rendered, bool Json);

    internal static ParsedPreviewArgs ParsePreviewArgs(IReadOnlyList<string> args)
    {
        string? repo = null, skill = null, version = null;
        var allowHiddenDirs = false;
        var rendered = false;
        var json = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--allow-hidden-dirs") { allowHiddenDirs = true; continue; }
            if (a == "--rendered") { rendered = true; continue; }
            if (a.StartsWith("--version=", StringComparison.Ordinal)) { version = a["--version=".Length..]; continue; }
            if (a == "--version" && i + 1 < args.Count) { version = args[++i]; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            positional.Add(a);
        }
        if (positional.Count > 0) repo = positional[0];
        if (positional.Count > 1) skill = positional[1];
        // Allow `owner/repo@ref` shorthand in the first positional.
        if (repo is not null)
        {
            var at = repo.LastIndexOf('@');
            if (at > 0 && at < repo.Length - 1)
            {
                version ??= repo[(at + 1)..];
                repo = repo[..at];
            }
        }
        return new ParsedPreviewArgs(repo, skill, version, allowHiddenDirs, rendered, json);
    }

    private static void WritePreviewJson(PreviewResult p)
        => WriteJson(w => WritePreviewJson(w, p));

    internal static string RenderPreviewText(PreviewResult preview, bool rendered)
    {
        if (!rendered)
        {
            return preview.Body.TrimEnd();
        }

        var markdown = new Markdown();
        var body = markdown.RenderToAnsi(preview.MarkdownBody, 80).TrimEnd();
        if (preview.AssociatedFiles.Length == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length + 64);
        if (body.Length > 0)
        {
            sb.AppendLine(body);
            sb.AppendLine();
        }

        sb.AppendLine("Associated files:");
        foreach (var file in preview.AssociatedFiles)
        {
            sb.Append("- ");
            sb.AppendLine(file);
        }

        return sb.ToString().TrimEnd();
    }

    internal static string RenderPreviewJson(PreviewResult p)
        => RenderJson(w => WritePreviewJson(w, p));

    private static void WritePreviewJson(Utf8JsonWriter w, PreviewResult p)
    {
        w.WriteStartObject();
        w.WriteString("repo", p.Repo);
        w.WriteString("skillName", p.SkillName);
        w.WriteString("version", p.Version);
        w.WriteString("markdown", p.MarkdownBody);
        w.WriteStartArray("associatedFiles");
        foreach (var f in p.AssociatedFiles) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
