using System.Text.Json;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui;

// Disposable process-death proof host. Production binaries have no pause/fault
// switches. Python kills this process at a published production checkpoint and
// inspects/repairs the state through the actual product CLI after restart.
try
{
    if (args.Length != 6) throw new ArgumentException("root gh id fingerprint apply|undo|repair checkpoint");
    if (!SecureRemovalBackend.TryCanonicalizePath(Path.GetFullPath(args[0]), out var root, out var rootError))
        throw new IOException("Cannot establish native proof-root authority: " + rootError);
    var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
    var state = Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME");
    if (!SecureRemovalBackend.TryCanonicalizePath(Environment.CurrentDirectory, out var workingDirectory, out _)
        || !PathIdentity.Equals(workingDirectory, root)
        || !PrivateExistingDirectory(home) || !PrivateExistingDirectory(state)
        || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable process proof\n")
        throw new IOException("A marked disposable HOME/CWD/state workspace is required.");
    if (args[4] is not ("apply" or "undo" or "repair")
        || args[5] is not ("retain-moved" or "install-moved" or "displace-moved" or "restore-moved"
            or "registry-truncated" or "registry-chunk-written" or "registry-flushed" or "owner-written"
            or "owner-repair-intent" or "owner-repaired" or "compensation-truncated"))
        throw new ArgumentException("Unsupported proof action or checkpoint.");
    var logger = new Logger();
    using var lifetime = new CancellationSource(TimeSpan.FromMinutes(3), CancellationCallbackReporter.For(logger, "Process-death proof"));
    var services = TuiServices.Build(logger, workingDirectory: root);
    var inventory = await services.InventoryService.CaptureAsync(args[1],
        new(ScanRoots: [Path.Combine(root, "custom")], AllowHiddenDirs: true), lifetime.Token);
    var store = new ReplacementPlanStore(Path.Combine(Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(services.Runner))!, "replacements"),
        logger, Checkpoint, services.Runner);
    var result = args[4] switch
    {
        "apply" => store.Apply(args[2], args[3], inventory, lifetime.Token),
        "undo" => store.Recover(args[2], args[3], inventory, true, lifetime.Token),
        _ => store.RepairOwner(args[2], args[3], inventory, lifetime.Token)
    };
    Console.WriteLine(JsonSerializer.Serialize(result, ReplacementJsonContext.Default.ReplacementResult));
    return result.Succeeded ? 0 : 2;

    bool PrivateExistingDirectory(string? path) => path is not null
        && Directory.Exists(path) && SecureRemovalBackend.TryCanonicalizePath(path, out var canonical, out _)
        && PathIdentity.IsInside(canonical, root) && !PathIdentity.Equals(canonical, root);

    void Checkpoint(string name)
    {
        if (args[5] == "compensation-truncated" && name == "registry-truncated")
            throw new IOException("Proof starts automatic preimage compensation.");
        if (name != args[5] && !(args[5] == "compensation-truncated" && name == "compensation-registry-truncated")) return;
        Console.WriteLine("READY " + args[5]); Console.Out.Flush();
        // The parent must kill us; bounded failure prevents an abandoned proof
        // process from retaining registry/state locks indefinitely.
        lifetime.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(2));
        throw new IOException("Proof parent did not kill the process before the bounded pause ended.");
    }
}
catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine("maintenance-proof: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
    return 2;
}
