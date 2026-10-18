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
    if (args.Length == 2 && args[0] == "--exposure-lifetime-proof")
        return ExposureLifetimeProof.Run(args[1]);
    if (args.Length == 2 && args[0] == "--exposure-link-roundtrip")
        return ExposureLinkProof.Run(args[1]);
    var checkDiskFullRoot = args.Length == 2 && args[0] == "--check-disk-full-root";
    if (!checkDiskFullRoot && args.Length is not (6 or 8))
        throw new ArgumentException("root gh id fingerprint apply|apply-merged|apply-unpin|apply-reimport|apply-source-switch|undo|undo-unpin|undo-reimport|undo-source-switch|repair|repair-unpin [disk-full:]checkpoint; or --check-disk-full-root root");
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
    var desiredPause = args[4] is "apply-selected" or "apply-approved";
    if (desiredPause ? args.Length != 8 || args[6] != "--yes"
            || args[7] != "--disposition=" + (args[4] == "apply-selected" ? ReplacementPlanStore.SelectedRevisionDisposition : ReplacementPlanStore.ApprovedRestoreDisposition)
            || diskFullMode || checkpoint is not ("retain-moved" or "install-moved")
        : args.Length != 6)
        throw new ArgumentException("Selected/approved pause requires its exact purpose, --yes, matching explicit --disposition and a retain/install checkpoint.");
    if (args[4] is not ("apply" or "apply-merged" or "apply-unpin" or "apply-reimport" or "apply-source-switch" or "apply-selected" or "apply-approved" or "recover-source-switch-reviewed" or "undo" or "undo-unpin" or "undo-reimport" or "undo-source-switch" or "repair" or "repair-unpin")
        || checkpoint is not ("local-retain-intent" or "retain-moved" or "install-moved" or "displace-moved" or "restore-moved"
            or "registry-truncated" or "registry-chunk-written" or "registry-flushed" or "owner-written"
            or "owner-repair-intent" or "owner-repaired" or "compensation-truncated" or "workflow")
        || (args[4] == "recover-source-switch-reviewed") != (checkpoint == "workflow")
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
    var result = args[4] == "recover-source-switch-reviewed"
        ? await SourceSwitchWorkflowProof.RunAsync(services, root, args[1], args[2], args[3], inventory, lifetime.Token)
        : args[4] switch
        {
            "apply" => store.Apply(args[2], args[3], inventory, lifetime.Token),
            "apply-merged" => store.Apply(args[2], args[3], inventory, lifetime.Token, "merge-reviewed"),
            "apply-unpin" => store.Apply(args[2], args[3], inventory, lifetime.Token, "unpin-only", "unpin-only"),
            "apply-reimport" => store.Apply(args[2], args[3], inventory, lifetime.Token, "replace-local-retain-original", "local-reimport"),
            "apply-source-switch" => store.Apply(args[2], args[3], inventory, lifetime.Token, "replace-local-source-retain-original", "switch-local-source"),
            "apply-selected" => store.Apply(args[2], args[3], inventory, lifetime.Token, ReplacementPlanStore.SelectedRevisionDisposition, ReplacementPlanStore.SelectedRevisionPurpose),
            "apply-approved" => store.Apply(args[2], args[3], inventory, lifetime.Token, ReplacementPlanStore.ApprovedRestoreDisposition, ReplacementPlanStore.ApprovedRestorePurpose),
            "undo-unpin" => store.Recover(args[2], args[3], inventory, true, lifetime.Token, "unpin-only"),
            "undo-reimport" => store.Recover(args[2], args[3], inventory, true, lifetime.Token, "local-reimport"),
            "undo-source-switch" => store.Recover(args[2], args[3], inventory, true, lifetime.Token, "switch-local-source"),
            "repair-unpin" => store.RepairOwner(args[2], args[3], inventory, lifetime.Token, "unpin-only"),
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
