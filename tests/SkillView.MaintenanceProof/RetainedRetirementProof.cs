using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;

// Offline retirement process-death host. No fault switches ship in the product.
internal static class RetainedRetirementProof
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (3 or 4 or 6)) throw new IOException("root inventory|inspect|retire|ordinary-inspect|ordinary-undo|supersede|edit-live|add-consumer|missing-live|registry-advanced id [fingerprint kind checkpoint]");
        var root = Path.GetFullPath(args[0]);
        if (Environment.CurrentDirectory != root || Environment.GetEnvironmentVariable("HOME") != Path.Combine(root, "home")
            || Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME") != Path.Combine(root, "state")
            || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable process proof\n")
            throw new IOException("Retirement proof requires its marked disposable HOME/CWD/state.");
        foreach (var name in new[] { "home", "state", "custom" })
            if (!SecureRemovalBackend.TryCaptureIdentity(Path.Combine(root, name), out var identity, out _)
                || identity.IsReparsePoint || identity.CanonicalPath != Path.Combine(root, name))
                throw new IOException("Proof paths must be exact native private directories.");
        var services = TuiServices.Build(new Logger(), workingDirectory: root);
        var store = new ReplacementPlanStore(ReplacementPlanStore.ResolveRoot(services.Runner), services.Logger, Checkpoint, services.Runner);
        Task<InventorySnapshot> Capture(CancellationToken token)
        {
            var configuration = new ScanRootConfiguration();
            var roots = services.ScanRootResolver.ResolveWithCancellation(new(root, Path.Combine(root, "home"), [Path.Combine(root, "custom")],
                ClaudeUserConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                PiCodingAgentDir: Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")), token, configuration: configuration);
            if (!configuration.IsComplete) throw new IOException("Incomplete disposable root configuration.");
            var diagnostics = new List<LockReadDiagnostic>();
            var owners = new SkillLockFileReader(services.Logger).LoadObservations(roots.Select(item => item.Path), token,
                new(Path.Combine(root, "home"), Path.Combine(root, "xdg-state")), diagnostics);
            var inaccessible = new List<string>();
            var locations = new List<DiscoveryLocation>();
            // Keep Scan's original null-options/default AllowHiddenDirs=false.
            // A convenience Scan discards coverage diagnostics; retirement must
            // never turn an unsupported linked container into complete evidence.
            var skills = new LocalSkillScanner(services.Logger).ScanWithCancellation(roots, null, token, inaccessible, locations);
            return Task.FromResult(InventorySnapshot.Empty with
            {
                Skills = LocalInventoryService.EnrichOwnership(skills, owners, token),
                CapturedAt = DateTimeOffset.UtcNow,
                Diagnostics = new() { InaccessiblePaths = inaccessible.ToImmutableArray(), Locations = locations.ToImmutableArray() },
                OwnershipRecords = owners.Select(item => item.Package).ToImmutableArray(),
                OwnershipReadDiagnostics = diagnostics.ToImmutableArray(),
                ScannedRoots = roots,
                ConfiguredRoots = configuration.Roots
            });
        }
        if (args[1].StartsWith("owner-", StringComparison.Ordinal))
            return await RetainedOwnerRetirementProof.RunAsync(args, store.Root, services.Logger, services.Runner, Capture);
        if (args[1] == "inventory")
        {
            var inventory = await Capture(CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new RetirementProofInventory(inventory.IsPartial,
                inventory.CapturedAt, inventory.Diagnostics.InaccessiblePaths, inventory.Diagnostics.Locations),
                RetirementProofJsonContext.Default.RetirementProofInventory));
            return 0;
        }
        if (args[1] == "ordinary-inspect")
        {
            Console.WriteLine(JsonSerializer.Serialize(store.Inspect(args[2], await Capture(CancellationToken.None), CancellationToken.None), ReplacementJsonContext.Default.ReplacementResult));
            return 0;
        }
        if (args[1] == "ordinary-undo" && args.Length == 4)
        {
            var result = store.Recover(args[2], args[3], await Capture(CancellationToken.None), true, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, ReplacementJsonContext.Default.ReplacementResult));
            return result.Succeeded ? 0 : 2;
        }
        if (args[1] is "supersede" or "edit-live" or "add-consumer" or "missing-live" or "registry-advanced")
        {
            var inspected = await store.InspectHistoryAsync(args[2], Capture, [], CancellationToken.None);
            var livePath = inspected.OriginalPath;
            if (args[1] == "edit-live") File.AppendAllText(Path.Combine(livePath, "SKILL.md"), "\nLater live edit\n");
            else if (args[1] == "missing-live") Directory.Move(livePath, Path.Combine(root, "held-live-" + inspected.Subject.Name));
            else if (args[1] == "add-consumer") Directory.CreateSymbolicLink(Path.Combine(root, "custom", inspected.Subject.Name + "-consumer"), livePath);
            else if (args[1] == "registry-advanced")
            {
                var agents = Directory.CreateDirectory(Path.Combine(root, "home", ".agents")).FullName;
                File.WriteAllText(Path.Combine(agents, ".skill-lock.json"), "{\"version\":3,\"skills\":{},\"retirement-proof\":\"advanced registry\"}");
            }
            else
            {
                var inventory = await Capture(CancellationToken.None);
                var target = inventory.Skills.Single(skill => skill.ResolvedPath == livePath);
                var before = File.ReadAllText(Path.Combine(livePath, "SKILL.md"));
                var later = before.Replace(target.TreeSha!, new string('d', 40)).Replace(target.FrontMatter.GithubRef!, "refs/tags/v3");
                var reviewedStore = new ReplacementPlanStore(store.Root, services.Logger, runner: services.Runner, sourceReviewRequired: true);
                var next = await reviewedStore.PlanUpdateAsync(target, inventory,
                    new("https://github.com/harder/fixture", "refs/tags/v3", new('e', 40), new('d', 40), "skills/" + target.Name),
                    (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), later + "\nActual successor update\n"); return Task.CompletedTask; },
                    (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), before); return Task.CompletedTask; }, false, CancellationToken.None);
                reviewedStore.AcknowledgeSourceReview(next.Id, next.PlanFingerprint, next.SourceReview!.Fingerprint, await Capture(CancellationToken.None), CancellationToken.None);
                var applied = reviewedStore.Apply(next.Id, next.PlanFingerprint, await Capture(CancellationToken.None), CancellationToken.None);
                if (!applied.Succeeded) throw new IOException(applied.Message);
                Console.WriteLine(JsonSerializer.Serialize(next, ReplacementJsonContext.Default.ReplacementResult));
            }
            return 0;
        }
        if (args[1] == "inspect" && args.Length == 3)
        {
            var result = await store.InspectHistoryAsync(args[2], Capture, [], CancellationToken.None);
            if (result.Retirement is null) throw new IOException(result.Message);
            Console.WriteLine(JsonSerializer.Serialize(result.Retirement, RetirementJsonContext.Default.RetirementReview));
            return 0;
        }
        if (args[1] != "retire" || args.Length != 6) throw new IOException("Invalid retirement proof action.");
        var review = new RetirementReview(args[2], args[4], args[3], false);
        await store.RetireAsync(review, args[4], Capture, [], CancellationToken.None);
        Console.WriteLine("RETIRED " + args[2]);
        return 0;

        void Checkpoint(string point)
        {
            if (args.Length == 6 && args[5] == "stop:" + point) throw new IOException("Owner proof stopped at " + point);
            if (args.Length == 6 && args[5] == "pending-owner:" + point)
            {
                File.WriteAllBytes(Path.Combine(root, "home", ".agents", ".skill-lock.json"), File.ReadAllBytes(Path.Combine(root, "owner-before.json")));
                return;
            }
            if (args.Length == 6 && args[5] == "missing-live:" + point)
            {
                Directory.Move(Path.Combine(root, "custom", "applied"), Path.Combine(root, "held-live-applied"));
                return;
            }
            if (args.Length != 6 || point != args[5]) return;
            Console.WriteLine("READY " + point); Console.Out.Flush();
            Thread.Sleep(TimeSpan.FromSeconds(45));
            throw new IOException("Proof parent did not kill the paused process.");
        }
    }
}

internal sealed record RetirementProofInventory(bool IsPartial, DateTimeOffset CapturedAt,
    ImmutableArray<string> InaccessiblePaths, ImmutableArray<DiscoveryLocation> Locations);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(RetirementProofInventory))]
internal partial class RetirementProofJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
