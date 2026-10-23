using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> CompareAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var parsed = ParseComparisonArgs(options.SubcommandArgs);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"skillview: {parsed.Error}");
            return ExitCodes.InvalidUsage;
        }
        // Comparison uses offline inventory and never performs auth or remote
        // update checks. gh list supplements filesystem facts when available.
        var snapshot = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new LocalInventoryService.Options(options.ScanRoots, AllowHiddenDirs: false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
        InstalledSkill[] Match(string path) => snapshot.Skills.Where(skill => PathIdentity.Equals(skill.ResolvedPath, path)
            || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, path))).ToArray();
        var left = Match(parsed.LeftPath!);
        var right = Match(parsed.RightPath!);
        if (left.Length != 1 || right.Length != 1)
        {
            Console.Error.WriteLine("skillview: each comparison path must identify one captured installation; include its --scan-root before compare");
            return ExitCodes.NoMatches;
        }
        var result = await new SkillView.Maintenance.SkillComparisonService().CompareAsync(left[0], right[0], token,
            inventoryComplete: !snapshot.IsPartial).ConfigureAwait(false);
        if (parsed.Json) Console.WriteLine(RenderComparisonJson(result));
        else
        {
            Console.WriteLine($"source: {result.SourceRelationship} · revision: {result.RevisionRelationship}");
            Console.WriteLine($"payload: {result.PayloadRelationship} · contexts: {result.ContextRelationship}");
            Console.WriteLine(result.Recommendation);
            foreach (var path in result.DifferingFiles) Console.WriteLine("  " + SkillView.Ui.TerminalEscapeSanitizer.Sanitize(path));
            Console.WriteLine($"{result.DifferingFileCount} differing file(s); complete: {result.Complete}");
            foreach (var diagnostic in result.Diagnostics) Console.Error.WriteLine(SkillView.Ui.TerminalEscapeSanitizer.Sanitize(diagnostic));
        }
        return result.Complete ? ExitCodes.Success : ExitCodes.UserError;
    }

    internal sealed record ParsedComparisonArgs(string? LeftPath, string? RightPath, bool Json, string? Error);

    internal static ParsedComparisonArgs ParseComparisonArgs(IReadOnlyList<string> args)
    {
        string? left = null, right = null, error = null;
        var json = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--path" or "--other-path" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] == "--path") left = args[++i]; else right = args[++i];
                continue;
            }
            error = $"unsupported or incomplete compare argument: {args[i]}";
        }
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            error = "compare requires --path <installation> and --other-path <installation>";
        return new(left, right, json, error);
    }

    internal static string RenderComparisonJson(SkillView.Maintenance.SkillComparisonResult result) => RenderJson(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("leftInstallationId", result.LeftInstallationId);
        w.WriteString("rightInstallationId", result.RightInstallationId);
        w.WriteString("sourceRelationship", result.SourceRelationship);
        w.WriteString("revisionRelationship", result.RevisionRelationship);
        w.WriteString("contextRelationship", result.ContextRelationship);
        w.WriteString("payloadRelationship", result.PayloadRelationship);
        w.WriteString("leftFingerprint", result.LeftFingerprint);
        w.WriteString("rightFingerprint", result.RightFingerprint);
        w.WriteString("leftObjectStateFingerprint", result.LeftObjectStateFingerprint);
        w.WriteString("rightObjectStateFingerprint", result.RightObjectStateFingerprint);
        if (result.LeftRootUnixMode is { } leftMode) w.WriteNumber("leftRootUnixMode", leftMode); else w.WriteNull("leftRootUnixMode");
        if (result.RightRootUnixMode is { } rightMode) w.WriteNumber("rightRootUnixMode", rightMode); else w.WriteNull("rightRootUnixMode");
        w.WriteString("objectEvidenceContract", "native IDs, generations and observed Unix modes; full ACL/xattr/alternate-stream metadata is not attested");
        w.WriteBoolean("complete", result.Complete);
        w.WriteBoolean("requiresExposureMigration", result.RequiresExposureMigration);
        w.WriteNumber("differingFileCount", result.DifferingFileCount);
        w.WriteString("recommendation", result.Recommendation);
        w.WriteStartArray("differingFiles");
        foreach (var path in result.DifferingFiles) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteStartArray("diagnostics");
        foreach (var diagnostic in result.Diagnostics) w.WriteStringValue(diagnostic);
        w.WriteEndArray();
        w.WriteEndObject();
    });
}
