using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;

// Existing marked disposable host only; these switches never ship in a product.
internal static class RetainedOwnerRetirementProof
{
    internal static async Task<int> RunAsync(string[] args, string state, Logger logger, ProcessRunner runner,
        Func<CancellationToken, Task<InventorySnapshot>> capture)
    {
        var root = args[0];
        var registry = Path.Combine(root, "home", ".agents", ".skill-lock.json");
        var beforeFile = Path.Combine(root, "owner-before.json");
        var store = new ReplacementPlanStore(state, logger, runner: runner);
        if (args[1] == "owner-registry-before") { File.WriteAllBytes(registry, File.ReadAllBytes(beforeFile)); return 0; }
        if (args[1] == "owner-registry-foreign")
        { File.WriteAllText(registry, File.ReadAllText(beforeFile).Replace(new string('a', 40), new string('d', 40))); return 0; }
        if (args[1].StartsWith("owner-recover-", StringComparison.Ordinal))
        {
            var recoveryInventory = await capture(CancellationToken.None);
            var reviewed = store.Inspect(args[2], recoveryInventory, CancellationToken.None);
            var result = store.Recover(args[2], reviewed.ReviewFingerprint, recoveryInventory, args[1] == "owner-recover-undo", CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, ReplacementJsonContext.Default.ReplacementResult));
            if (!result.Succeeded) throw new IOException(result.Message);
            return 0;
        }
        if (args[1] != "owner-prepare") throw new IOException("Unsupported owner fixture action.");
        var mode = args[2];
        var skills = Directory.CreateDirectory(Path.Combine(root, "home", ".agents", "skills")).FullName;
        var live = Directory.CreateDirectory(Path.Combine(skills, "owner")).FullName;
        var metadata = "---\nname: owner\ndescription: Native owner proof\nmetadata:\n  github-repo: https://github.com/harder/fixture\n  github-ref: refs/heads/main\n  github-path: skills/owner\n  github-tree-sha: " + new string('a', 40) + "\n---\n";
        var original = metadata + "Original owner payload\n";
        File.WriteAllText(Path.Combine(live, "SKILL.md"), original);
        Directory.CreateSymbolicLink(Path.Combine(root, "custom", "owner-consumer"), live);
        File.WriteAllText(registry, "{\"version\":3,\"skills\":{\"owner\":{\"source\":\"harder/fixture\",\"sourceType\":\"github\",\"sourceUrl\":\"https://github.com/harder/fixture.git\",\"skillPath\":\"skills/owner\",\"skillFolderHash\":\"" + new string('a', 40) + "\",\"installedAt\":\"2020-01-01T00:00:00Z\",\"updatedAt\":\"2020-01-01T00:00:00Z\"}}}");
        File.WriteAllBytes(beforeFile, File.ReadAllBytes(registry));
        var inventoryBefore = await capture(CancellationToken.None);
        var target = inventoryBefore.Skills.Single(item => item.Name == "owner");
        var equal = mode == "equal";
        var plan = await store.PlanUpdateAsync(target, inventoryBefore,
            new("https://github.com/harder/fixture", equal ? "refs/heads/main" : "refs/tags/v2", new('c', 40), new(equal ? 'a' : 'b', 40), "skills/owner"),
            (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), equal ? original : original.Replace(new string('a', 40), new string('b', 40)).Replace("refs/heads/main", "refs/tags/v2") + "New source\n"); return Task.CompletedTask; },
            (destination, _) => { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), original); return Task.CompletedTask; }, false, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(plan, ReplacementJsonContext.Default.ReplacementResult)); Console.Out.Flush();
        if (mode is "restore-moved" or "pause-restore-moved")
        {
            var applied = store.Apply(plan.Id, plan.PlanFingerprint, await capture(CancellationToken.None), CancellationToken.None);
            if (!applied.Succeeded) throw new IOException(applied.Message);
        }
        var stopped = new ReplacementPlanStore(state, logger, point =>
        {
            if (mode == "pause-" + point)
            {
                Console.WriteLine("READY " + point); Console.Out.Flush(); Thread.Sleep(TimeSpan.FromSeconds(45));
                throw new IOException("Parent did not kill owner proof process.");
            }
            if (point != mode) return;
            if (mode == "owner-publish-reviewed")
            {
                AppendRetirementRegistryByte(registry);
            }
            else throw new IOException("Stop owner transition before publication");
        }, runner);
        var inventory = await capture(CancellationToken.None);
        var outcome = mode is "restore-moved" or "pause-restore-moved"
            ? stopped.Recover(plan.Id, store.Inspect(plan.Id, inventory, CancellationToken.None).ReviewFingerprint, inventory, true, CancellationToken.None)
            : stopped.Apply(plan.Id, plan.PlanFingerprint, inventory, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(outcome, ReplacementJsonContext.Default.ReplacementResult));
        if (mode is "completed-reverted" or "equal" && !outcome.Succeeded) throw new IOException(outcome.Message);
        if (mode is "completed-reverted") File.WriteAllBytes(registry, File.ReadAllBytes(beforeFile));
        else if (mode is not "equal" && outcome.Succeeded) throw new IOException("Owner interruption did not occur.");
        if (mode == "owner-publish-reviewed" && !outcome.Message.Contains("Registry changed after transition review", StringComparison.Ordinal))
            throw new IOException("Exact owner precheck was not exercised: " + outcome.Message);
        return 0;
    }
    // Fixture adversary: bypass advisory .NET/gh locks deliberately, append
    // outside Windows' first-byte range lock, and keep the same file object.
    private static void AppendRetirementRegistryByte(string path)
    {
        using var handle = OperatingSystem.IsWindows()
            ? OpenRetirementRegistryWindows(path, 0x40000000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero)
            : new SafeFileHandle(new IntPtr(OpenRetirementRegistryUnix(path, 1)), ownsHandle: true);
        if (handle.IsInvalid) throw new IOException("Fixture registry adversary open failed: " + Marshal.GetLastPInvokeError());
        using var change = new FileStream(handle, FileAccess.Write, bufferSize: 1);
        change.Position = change.Length; change.WriteByte(32); change.Flush(true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenRetirementRegistryUnix(string path, int flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenRetirementRegistryWindows(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

}
