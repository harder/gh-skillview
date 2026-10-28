using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Threading;

internal static class LocationsRecoveryProof
{
    internal static int Run(string root, string action, string pause)
    {
        root = Path.GetFullPath(root);
        if (!SecureRemovalBackend.TryCanonicalizePath(root, out var canonical, out _)
            || !SecureRemovalBackend.TryCanonicalizePath(Environment.CurrentDirectory, out var cwd, out _)
            || !PathIdentity.Equals(canonical, cwd)
            || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable locations proof\n")
            throw new IOException("A marked private Locations fixture and matching CWD are required.");
        foreach (var key in new[] { "HOME", "USERPROFILE", "SKILLVIEW_STATE_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is null || !PathIdentity.IsInside(value, root) || PathIdentity.Equals(value, root))
                throw new IOException("Private captured HOME/state required for Locations proof.");
        }
        var store = new LocationPreferences(Path.Combine(root, "state", "SkillView", "locations"), "locations-proof:" + canonical);
        var first = Directory.CreateDirectory(Path.Combine(root, "selected-first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "selected-second")).FullName;
        using var lifetime = new CancellationSource(TimeSpan.FromSeconds(90), CancellationCallbackReporter.For(new Logger(), "locations-proof"));
        var token = lifetime.Token;
        var selected = new SelectedLocation("workspace", PathIdentity.Normalize(first));
        if (action is "seed" or "capacity")
        {
            var read = store.Read([], token);
            var one = store.Save(read, [selected], [], root, token);
            one = store.Save(one, [new("workspace", PathIdentity.Normalize(second))], [], root, token);
            if (action == "capacity")
                for (var i = 3; i <= LocationPreferences.MaxRecords; i++)
                    one = store.Save(one, [new("workspace", PathIdentity.Normalize(i % 2 == 0 ? second : first))], [], root, token);
            Console.WriteLine("SEEDED"); return 0;
        }
        store.Checkpoint = step =>
        {
            if (step != pause) return;
            Console.WriteLine("READY " + step); Console.Out.Flush();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(60));
            throw new IOException("Proof parent did not kill the paused process.");
        };
        LocationRead result;
        if (action == "read")
        {
            var read = store.Read([], token);
            Console.WriteLine("READ " + read.Fingerprint + " " + read.Locations.Length + " " + (read.Diagnostic ?? "ok"));
            return read.Diagnostic is null ? 0 : 2;
        }
        if (action == "save") result = store.Save(store.Read([], token), [selected], [], root, token);
        else if (action is "reset" or "compact")
        {
            var review = store.PrepareReset([], token);
            result = action == "reset" && !review.CanReset && !Directory.GetDirectories(store.Root, "records-*").Any()
                ? store.Read([], token) : store.Reset(review, action == "compact", [], token);
        }
        else throw new ArgumentException("Unsupported Locations proof action.");
        Console.WriteLine("DONE " + result.UsedRecords + " " + (result.Diagnostic ?? "ok"));
        return result.Diagnostic is null ? 0 : 2;
    }
}
