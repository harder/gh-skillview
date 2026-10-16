using System.Text.Json;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;
using SkillView.Ui;

// Disposable process-death / bounded tmpfs ENOSPC proof host. Production binaries
// have no pause/fault switches. Recovery runs through the actual CLI on restart.
try
{
    var checkDiskFullRoot = args.Length == 2 && args[0] == "--check-disk-full-root";
    if (!checkDiskFullRoot && args.Length != 6)
        throw new ArgumentException("root gh id fingerprint apply|apply-merged|undo|repair [disk-full:]checkpoint; or --check-disk-full-root root");
    if (!SecureRemovalBackend.TryCanonicalizePath(Path.GetFullPath(args[checkDiskFullRoot ? 1 : 0]), out var root, out var rootError))
        throw new IOException("Cannot establish native proof-root authority: " + rootError);
    var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
    var state = Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME");
    if (!SecureRemovalBackend.TryCanonicalizePath(Environment.CurrentDirectory, out var workingDirectory, out _)
        || !PathIdentity.Equals(workingDirectory, root)
        || !PrivateExistingDirectory(home) || !PrivateExistingDirectory(state)
        || File.ReadAllText(Path.Combine(root, "disposable-proof-marker")) != "SkillView disposable process proof\n")
        throw new IOException("A marked disposable HOME/CWD/state workspace is required.");
    var diskFullMode = checkDiskFullRoot || args[5].StartsWith("disk-full:", StringComparison.Ordinal);
    using var diskFull = diskFullMode ? DiskFullProof.Admit(root) : null;
    if (checkDiskFullRoot)
    {
        Console.WriteLine("DISK_FULL_ROOT_ADMITTED (read-only; no filler created)");
        return 0;
    }
    var checkpoint = diskFullMode ? args[5]["disk-full:".Length..] : args[5];
    if (args[4] is not ("apply" or "apply-merged" or "undo" or "repair")
        || checkpoint is not ("retain-moved" or "install-moved" or "displace-moved" or "restore-moved"
            or "registry-truncated" or "registry-chunk-written" or "registry-flushed" or "owner-written"
            or "owner-repair-intent" or "owner-repaired" or "compensation-truncated")
        || diskFullMode && (args[4] is not ("apply-merged" or "undo")
            || checkpoint is not ("retain-moved" or "install-moved" or "displace-moved" or "restore-moved")))
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
        "apply-merged" => store.Apply(args[2], args[3], inventory, lifetime.Token, "merge-reviewed"),
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
        if (checkpoint == "compensation-truncated" && name == "registry-truncated")
            throw new IOException("Proof starts automatic preimage compensation.");
        if (name != checkpoint && !(checkpoint == "compensation-truncated" && name == "compensation-registry-truncated")) return;
        if (diskFull is not null)
        {
            var witness = diskFull.Fill(lifetime.Token);
            Console.WriteLine("DISK_FULL " + JsonSerializer.Serialize(witness)); Console.Out.Flush();
            // Return normally: production's own reconciliation/journal write
            // must encounter the genuine exhausted filesystem and report it.
            return;
        }
        Console.WriteLine("READY " + checkpoint); Console.Out.Flush();
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
