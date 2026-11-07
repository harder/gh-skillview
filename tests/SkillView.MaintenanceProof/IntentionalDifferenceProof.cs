using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Threading;

internal static class IntentionalDifferenceProof
{
    // Only a marked, private, credential-free fixture can use this proof entry.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("proof-root inspect|write|reset [checkpoint/version]");
        var root = Path.GetFullPath(args[0]);
        if (!Directory.Exists(root) || File.ReadAllText(Path.Combine(root, "intentional-proof-marker")) != "SkillView disposable preference proof\n"
            || !PathIdentity.Equals(Environment.CurrentDirectory, root)
            || Environment.GetEnvironmentVariable("HOME") is not { } home || !PathIdentity.IsInside(home, root)
            || Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME") is not { } state || !PathIdentity.IsInside(state, root))
            throw new IOException("Marked private HOME/CWD/state required for preference proof.");
        var logger = new Logger();
        using var lifetime = new CancellationSource(TimeSpan.FromSeconds(60), CancellationCallbackReporter.For(logger, "preference-proof"));
        var token = lifetime.Token;
        var path = Path.Combine(state, "SkillView", "intentional-differences");
        var store = new IntentionalDifferenceStore(path)
        {
            Checkpoint = phase =>
            {
                if (args[1] != "reset" || args.Length != 3 || args[2] != phase) return;
                Console.WriteLine("READY " + phase); Console.Out.Flush();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(45));
                throw new IOException("Proof parent did not terminate the paused reset.");
            }
        };
        var inventory = InventorySnapshot.Empty;
        if (args[1] == "inspect") { Console.WriteLine(store.Read([], token).Version); return 0; }
        if (args[1] == "write" && args.Length == 3)
        {
            Console.WriteLine("READY writer"); Console.Out.Flush();
            while (!File.Exists(Path.Combine(root, "writer-go"))) await Task.Delay(20, token);
            try { store.Set(new(new('a', 64), new('b', 64)), args[2], false, inventory, token); Console.WriteLine("WRITE_PUBLISHED"); return 0; }
            catch (IOException ex) { Console.WriteLine("WRITE_REFUSED " + ex.Message); return 2; }
        }
        if (args[1] == "seed")
        {
            for (var index = 0; index < 3; index++)
                store.Set(new(new('a', 64), index.ToString("x64")), store.Read([], token).Version, false, inventory, token);
            Console.WriteLine(store.Read([], token).Version); return 0;
        }
        if (args[1] == "reset")
        {
            var review = store.InspectReset(inventory, "disposable-process-proof", token);
            var result = store.Reset(review, inventory, "disposable-process-proof", token);
            Console.WriteLine(result.State + ": " + result.BackupPath); return 0;
        }
        throw new ArgumentException("Unknown preference proof action.");
    }
}
