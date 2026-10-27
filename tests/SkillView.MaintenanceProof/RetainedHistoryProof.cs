using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;

// Offline fixture creator for read-only History PTY checks. It runs only in a
// marked, fresh, private proof root and never opens a user's installed payload.
internal static class RetainedHistoryProof
{
    internal static async Task<int> CreateAsync(string path)
    {
        var root = Path.GetFullPath(path);
        var home = Environment.GetEnvironmentVariable("HOME");
        var state = Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME");
        if (Path.GetFullPath(Environment.CurrentDirectory) != root
            || home != Path.Combine(root, "home") || state != Path.Combine(root, "state")
            || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable process proof\n"
            || Directory.Exists(Path.Combine(root, "custom")) || Directory.Exists(state))
            throw new IOException("History fixtures require a fresh marked private HOME/CWD/state workspace.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) & (UnixFileMode)63) != 0)
            throw new IOException("History fixture root must be private.");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(root, "custom"));
        foreach (var name in new[] { "ready", "applied", "interrupted" })
        {
            var skill = Path.Combine(root, "custom", name);
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), Metadata(name) + "original\n");
        }
        var services = TuiServices.Build(new Logger(), workingDirectory: root);
        var configuration = new ScanRootConfiguration();
        var roots = services.ScanRootResolver.ResolveWithCancellation(new(root, home!, [Path.Combine(root, "custom")],
            ClaudeUserConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            PiCodingAgentDir: Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")), CancellationToken.None,
            configuration: configuration);
        if (!configuration.IsComplete) throw new IOException("Private fixture root configuration is incomplete.");
        var inventory = InventorySnapshot.Empty with
        {
            Skills = new LocalSkillScanner(services.Logger).Scan(roots).ToImmutableArray(),
            ScannedRoots = roots,
            ConfiguredRoots = configuration.Roots
        };
        var store = new ReplacementPlanStore(ReplacementPlanStore.ResolveRoot(services.Runner), services.Logger, runner: services.Runner);
        foreach (var target in inventory.Skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            var plan = await store.PlanAsync(target, inventory, (destination, token) =>
            {
                token.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), Metadata(target.Name) + "candidate\n");
                return Task.CompletedTask;
            }, CancellationToken.None);
            if (target.Name == "applied")
            {
                var result = store.Apply(plan.Id, plan.PlanFingerprint, inventory, CancellationToken.None);
                if (!result.Succeeded) throw new IOException(result.Message);
            }
            else if (target.Name == "interrupted")
            {
                var interrupted = new ReplacementPlanStore(store.Root, services.Logger, checkpoint: point =>
                { if (point == "retain-moved") throw new IOException("History fixture interruption after retain"); }, runner: services.Runner);
                var result = interrupted.Apply(plan.Id, plan.PlanFingerprint, inventory, CancellationToken.None);
                if (result.Succeeded || !Directory.Exists(plan.RetainedPath) || !Directory.Exists(plan.CandidatePath))
                    throw new IOException("Interrupted fixture did not retain the required topology.");
            }
            Console.WriteLine(JsonSerializer.Serialize(plan, ReplacementJsonContext.Default.ReplacementResult));
            var inspected = store.Inspect(plan.Id, inventory, CancellationToken.None);
            Console.WriteLine("native-history-inspect: " + inspected.Status);
            Console.WriteLine(JsonSerializer.Serialize(inspected, ReplacementJsonContext.Default.ReplacementResult));
        }
        return 0;
    }

    private static string Metadata(string name) => $"---\nname: {name}\ndescription: Offline retained history fixture\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";
}
