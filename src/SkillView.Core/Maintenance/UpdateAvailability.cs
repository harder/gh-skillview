using System.Security.Cryptography;
using System.Text;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Maintenance;

// Session observations only. Neither this key nor a successful upstream check
// admits a mutation or certifies the installed payload's integrity.
internal enum UpdateAvailabilityStatus
{
    NotChecked, Checking, NoUpdate, Available, Pinned, Unsupported, Unknown, Failed, Canceled, Stale
}

internal sealed record UpdateAvailability(UpdateAvailabilityStatus Status, string Reason,
    DateTimeOffset? CheckedAt = null, string Diagnostic = "")
{
    // Inventory captures the address off the UI thread (path case policy can
    // require native inspection). Renderers reuse that frozen value.
    internal static string Address(InstalledSkill skill) => skill.ObservedSubject?.InstallationId ?? skill.InstallationId;
    internal string Label => Status switch
    {
        UpdateAvailabilityStatus.NotChecked => "Not checked",
        UpdateAvailabilityStatus.NoUpdate => "No upstream update",
        UpdateAvailabilityStatus.Available => "Update available",
        _ => Status.ToString()
    };

    internal string TableLabel => Status switch
    {
        UpdateAvailabilityStatus.Available => "Available",
        UpdateAvailabilityStatus.NoUpdate => "No update",
        _ => Label
    };

    internal string Detail => $"**Updates: {Label}**\n\n"
        + (CheckedAt is { } at ? $"Checked {at:yyyy-MM-dd HH:mm:ss zzz}.\n\n" : "")
        + $"{SkillView.Ui.TerminalEscapeSanitizer.Sanitize(Reason)}\n\n"
        + "Upstream availability does not verify installed file integrity.\n";

    internal static string Evidence(InstalledSkill skill)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        void Add(object? value) => writer.Write(value?.ToString() ?? "");
        Add(Address(skill)); Add(skill.ResolvedPath); Add(skill.ScanRoot); Add(skill.Scope); Add(skill.Validity);
        var m = skill.FrontMatter;
        Add(m.Name); Add(m.Upstream); Add(m.GithubPath); Add(m.GithubRef); Add(m.GithubTreeSha);
        Add(m.LocalPath); Add(m.Pinned); Add(m.PinnedRef);
        writer.Write(m.Extra.Count);
        foreach (var pair in m.Extra.OrderBy(p => p.Key, StringComparer.Ordinal)) { Add(pair.Key); Add(pair.Value); }
        writer.Write(skill.MetadataConflicts.Length);
        foreach (var conflict in skill.MetadataConflicts) Add(conflict);
        writer.Write(skill.OwnershipDiagnostics.Length);
        foreach (var diagnostic in skill.OwnershipDiagnostics) Add(diagnostic);
        var owners = skill.AllOwnershipClaims.ToArray();
        writer.Write(owners.Length);
        foreach (var owner in owners)
        {
            Add(owner.Source); Add(owner.SourceType); Add(owner.SourceUrl); Add(owner.Provider);
            Add(owner.LockFilePath); Add(owner.InstallationRoot); Add(owner.ContextRoot); Add(owner.SkillPath);
            Add(owner.RepositoryRef); Add(owner.ContentHash); Add(owner.HashKind); Add(owner.PinnedRef);
            Add(owner.PluginName); Add(owner.ReadOnly); Add(owner.IsExclusiveOwner); Add(owner.Selector);
            Add(owner.EntryFingerprint); Add(owner.RecordFingerprint); Add(owner.Diagnostic);
            Add(owner.SchemaVersion); Add(owner.InstallationMode); Add(owner.PackageVersion); Add(owner.ManagedRegion);
            writer.Write(owner.CoveredRoots.Length);
            foreach (var root in owner.CoveredRoots) Add(root);
        }
        writer.Write(skill.Agents.Length);
        foreach (var agent in skill.Agents) Add(agent);
        if (skill.OwnershipRunner is { } runner) Add(Context(runner, null, null));
        if (skill.ObservedSubject is { } subject)
        {
            Add(subject.NativeObject with { CapturedAt = null });
            Add(subject.ExposureCoverageComplete); Add(subject.ExposureCoverage);
        }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    internal static string Context(ProcessRunner runner, string? ghPath, string? version)
    {
        var values = new[] { "HOME", "USERPROFILE", "GH_CONFIG_DIR", "XDG_CONFIG_HOME", "XDG_STATE_HOME",
            "XDG_DATA_HOME", "APPDATA", "LOCALAPPDATA", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
            "CLAUDE_CODE_PLUGIN_CACHE_DIR", "CLAUDE_CODE_PLUGIN_DIRS", "CLAUDE_CODE_PLUGIN_SEED_DIR", "PI_CODING_AGENT_DIR" };
        return string.Join('\0', values.Select(key => key + "=" + runner.GetEffectiveEnvironmentVariable(key)))
            + $"\0{runner.EffectiveWorkingDirectory}\0{ghPath}\0{version}";
    }
}
