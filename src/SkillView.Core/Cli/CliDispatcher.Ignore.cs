using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Preferences;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record IgnoreArguments(string? Path, string? InstallId, bool Clear, bool Json, string? Error);
    internal static IgnoreArguments ParseIgnoreArgs(IReadOnlyList<string> args)
    {
        string? path = null, id = null;
        var clear = false; var json = false; var hidden = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] == "--clear" && !clear) { clear = true; continue; }
            if (args[i] == "--allow-hidden-dirs" && !hidden) { hidden = true; continue; }
            if (args[i] is "--path" or "--install-id" && path is null && id is null && i + 1 < args.Count)
            {
                var flag = args[i]; var value = args[++i];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl))
                    return new(null, null, clear, json, "Exact selector needs a nonblank value without control characters.");
                if (flag == "--path") path = value; else id = value;
                continue;
            }
            return new(path, id, clear, json, "Unsupported or duplicate ignore option: " + args[i]);
        }
        return new(path, id, clear, json, path is null && id is null ? "ignore requires exactly one --path or --install-id." : null);
    }

    private static async Task<int> IgnoreAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var args = ParseIgnoreArgs(options.SubcommandArgs);
        if (args.Error is not null) { Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(args.Error)); return ExitCodes.InvalidUsage; }
        try
        {
            var inventory = await CaptureInventoryAsync(options, services, cancellationToken: token).ConfigureAwait(false);
            var path = args.Path is null ? null : Path.GetFullPath(args.Path, services.Runner.EffectiveWorkingDirectory);
            if (path is not null) path = ResolveIgnorePath(path);
            var selected = inventory.Skills.Where(skill => args.InstallId is not null ? skill.InstallationId == args.InstallId
                : OwnershipPath.Same(skill.ResolvedPath, path!)).ToArray();
            if (selected.Length != 1) throw new IOException("Select one exact physical installation; put --scan-root before the command.");
            var result = await Task.Run(() => CleanupSuppressionStore.For(services.Runner).Set(selected[0], inventory, args.Clear, token), token).ConfigureAwait(false);
            if (args.Json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", true);
                w.WriteBoolean("changed", result.Changed); w.WriteString("path", result.Path);
                w.WriteString("effect", "cleanup-suggestions-for-this-physical-installation; agent loading and update policy unchanged");
                WriteSuppression(w, result.Observation); w.WriteEndObject();
            });
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{result.Observation.State}  {result.Path}\nCleanup suggestions only; affects this physical installation across its consumer contexts.\n{result.Observation.Diagnostic}"));
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            if (args.Json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            return ExitCodes.UserError;
        }
    }

    internal static string ResolveIgnorePath(string path) =>
        SecureRemovalBackend.TryCanonicalizePath(path, out var canonical, out var error)
            ? canonical : throw new IOException(error ?? "Selected path is unavailable.");

    private static void WriteSuppression(Utf8JsonWriter w, CleanupSuppression? value)
    {
        w.WriteStartObject("cleanupSuppression");
        if (value is not null)
        {
            w.WriteBoolean("legacyMarker", value.LegacyMarker); w.WriteBoolean("external", value.External);
            w.WriteString("state", value.State); w.WriteString("bindingMode", value.BindingMode);
            if (value.Sequence is { } sequence) w.WriteNumber("sequence", sequence); else w.WriteNull("sequence");
            w.WriteString("diagnostic", value.Diagnostic);
        }
        w.WriteEndObject();
    }
}
