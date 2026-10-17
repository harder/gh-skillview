using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;

internal static class ExposureLifetimeProof
{
    internal static int Run(string path)
    {
        var root = Path.GetFullPath(path);
        var scenario = Environment.GetEnvironmentVariable("SKILLVIEW_EXPOSURE_LIFETIME_SCENARIO");
        if (!OperatingSystem.IsLinux() || root != Environment.CurrentDirectory
            || !File.Exists(Path.Combine(root, "disposable-owner-marker"))
            || File.ReadAllText(Path.Combine(root, "disposable-owner-marker")) != "SkillView disposable native owner proof\n"
            || Environment.GetEnvironmentVariable("HOME") != Path.Combine(root, "home")
            || Environment.GetEnvironmentVariable("SKILLVIEW_STATE_HOME") != Path.Combine(root, "state")
            || scenario is not ("dispose" or "retained-dispose" or "abandon" or "foreign-finalizer" or "idle" or "absolute" or "partial-init" or "command-failure" or "evidence-mismatch" or "canceled-command"))
            throw new IOException("Marked private Linux HOME/CWD/state and a fixed lifetime scenario are required.");
        foreach (var name in new[] { "targets", "exposures", "caller-sentinel" })
            if (Path.Exists(Path.Combine(root, name)) || PathResolver.IsSymlink(Path.Combine(root, name)))
                throw new IOException("Lifetime proof requires fresh ordinary fixture names.");
        foreach (var name in new[] { "home", "state" })
            if ((File.GetAttributes(Path.Combine(root, name)) & FileAttributes.ReparsePoint) != 0
                || Directory.EnumerateFileSystemEntries(Path.Combine(root, name)).Any())
                throw new IOException("Lifetime proof requires empty ordinary private HOME/state directories.");
        var targets = Path.Combine(root, "targets"); var payload = Path.Combine(targets, "payload");
        var exposures = Path.Combine(root, "exposures"); var entry = Path.Combine(exposures, "demo");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(exposures);
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: owner-fixture\ndescription: Native lifetime fixture\n---\nunchanged\n");
        Directory.CreateSymbolicLink(entry, Path.GetRelativePath(exposures, payload));
        var statePath = Path.Combine(root, "state", "operation");
        LinuxExposureOwnerRequest request;
        using (var target = NativeDirectoryMoveLease.Open(targets, payload, CancellationToken.None))
        using (var reviewed = NativeExposureLinkLease.Open(exposures, entry, target, CancellationToken.None))
        {
            var captured = target.Capture(CancellationToken.None);
            request = new(exposures, entry, targets, payload, reviewed.OriginalDirectory, reviewed.Original.Identity,
                reviewed.Original.RawTarget, reviewed.Original.MetadataFingerprint, target.RootIdentity, target.ParentIdentity,
                target.TargetIdentity, captured.Fingerprint, captured.ObjectStateFingerprint ?? throw new IOException("Complete native target evidence is required."), statePath,
                scenario == "partial-init" ? "invalid/slot" : "retained-link",
                TimeSpan.FromSeconds(scenario is "idle" or "absolute" ? 2 : 15),
                TimeSpan.FromSeconds(scenario == "absolute" ? 2 : 30));
        }
        if (scenario == "evidence-mismatch") request = request with { TargetFingerprint = "unreviewed" };
        if (scenario == "foreign-finalizer")
        {
            var report = ProveForeignFinalizer(request, root);
            VerifyWriterReleased(); VerifyNoPrivateDescriptors(); Print(report); return 0;
        }
        var started = Start(request);
        var box = started.Box; var ready = started.Ready; var completion = started.Completion;
        if (scenario is "partial-init" or "evidence-mismatch")
        {
            if (ready.Succeeded) throw new IOException("Invalid initialization was admitted.");
            var cleanup = completion.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            RequireClean(cleanup); VerifyWriterReleased(); VerifyNoPrivateDescriptors(); box.Proxy!.Dispose();
            Print(new(scenario, true, false, false, true, true, cleanup.Reason, cleanup.ResourcesOpened, cleanup.ResourcesReleased, 0, "private-root-fd-addresses-and-writer-lock-after-initialization-failure")); return 0;
        }
        if (!ready.Succeeded || ready.Observation is null) throw new IOException("Native owner initialization failed: " + ready.Error);
        if (scenario == "retained-dispose")
        {
            var moved = box.Proxy!.RetainAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!moved.Succeeded || moved.Canceled || moved.MoveState is not { MutationAttempted: true, MutationCompleted: true, Uncertain: false, Position: LinuxExposureLinkPosition.Retained }
                || moved.Observation is not { RetainedSlotEmpty: false } actual
                || !NativeDirectoryMoveLease.SameObject(ready.Observation.Original.Identity, actual.Current.Identity)
                || !ready.Observation.Original.RawTarget.AsSpan().SequenceEqual(actual.Current.RawTarget.AsSpan())
                || ready.Observation.TargetFingerprint != actual.TargetFingerprint || ready.Observation.TargetObjectFingerprint != actual.TargetObjectFingerprint
                || Path.Exists(entry) || PathResolver.IsSymlink(entry))
                throw new IOException("Actual retained original-object evidence was not published before foreign disposal.");
        }
        var ownerTask = ready.Observation.OwnerTask;
        // Retained-dispose samples AFTER rename, including the original pin's changed proc address.
        var descriptors = CaptureOwnerDescriptors(ownerTask, root);
        if (descriptors.Count < 5) throw new IOException("Insufficient actual owned native descriptors were observed.");
        var sentinelPath = Path.Combine(root, "caller-sentinel"); var sentinelBytes = Encoding.UTF8.GetBytes("foreign caller descriptor survives\n");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        Exception? failure = null; var protectedCount = 0; var finalized = false;
        LinuxExposureOwnerCleanup? final = null;
        WeakReference? abandoned = null;
        var foreign = new Thread(() =>
        {
            var duplicates = new List<int>();
            try
            {
                if (Native.unshare(0x400) != 0) throw new IOException("Required CLONE_FILES divergence refused: errno " + Marshal.GetLastPInvokeError());
                using var sentinel = File.OpenHandle(sentinelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var original = sentinel.DangerousGetHandle().ToInt32();
                foreach (var pair in descriptors)
                {
                    if (pair.Key == original) continue;
                    if (Native.dup2(original, pair.Key) != pair.Key) throw new IOException("Could not create a fixed-number foreign sentinel.");
                    duplicates.Add(pair.Key);
                }
                if (duplicates.Count < 5) throw new IOException("Too few conflicting foreign descriptor sentinels.");
                if (scenario == "command-failure")
                {
                    Directory.Delete(entry); Directory.CreateSymbolicLink(entry, payload);
                    if (box.Proxy!.InspectAsync(CancellationToken.None).GetAwaiter().GetResult().Succeeded)
                        throw new IOException("Changed command authority was admitted.");
                }
                else if (scenario == "canceled-command")
                {
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    if (!box.Proxy!.InspectAsync(canceled.Token).GetAwaiter().GetResult().Canceled)
                        throw new IOException("Canceled admission was not reported.");
                }
                if (scenario == "abandon") Volatile.Write(ref abandoned, Abandon(box));
                else if (scenario is not ("idle" or "absolute"))
                {
                    box.Proxy!.Dispose();
                    if (scenario != "retained-dispose") box.Proxy = null;
                }
                final = completion.WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
                RequireClean(final);
                foreach (var fd in duplicates)
                {
                    using var borrowed = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
                    var bytes = new byte[sentinelBytes.Length];
                    if (RandomAccess.Read(borrowed, bytes, 0) != bytes.Length || !bytes.AsSpan().SequenceEqual(sentinelBytes))
                        throw new IOException("Owner cleanup closed/redirected a foreign caller sentinel.");
                }
                protectedCount = duplicates.Count;
            }
            catch (Exception ex) { failure = ex; }
            finally { foreach (var fd in duplicates) Native.close(fd); }
        });
        foreign.Start();
        if (scenario == "abandon")
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
            { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(20); }
            finalized = Volatile.Read(ref abandoned) is { IsAlive: false };
        }
        if (!foreign.Join(TimeSpan.FromSeconds(30))) throw new IOException("Native lifetime subprocess worker did not drain.");
        if (failure is not null) throw new IOException("Foreign native lifetime proof failed.", failure);
        if (final is null || protectedCount < 5) throw new IOException("No positive foreign-table cleanup evidence.");
        if (scenario == "abandon" && (!finalized || final.Reason != "abandoned")) throw new IOException("Abandoned proxy did not finalize independently of idle expiry.");
        if (scenario == "idle" && final.Reason != "idle-expired" || scenario == "absolute" && final.Reason != "absolute-expired")
            throw new IOException("Finite owner deadline did not close the live session.");
        if (scenario == "retained-dispose")
        {
            if (final.MoveState is not { Position: LinuxExposureLinkPosition.Retained, LiveContinuity: false }
                || Path.Exists(entry) || PathResolver.IsSymlink(entry)
                || ReadLink(Path.Combine(statePath, request.SlotName, "entry")) != Encoding.UTF8.GetString(request.ExpectedRawTarget.AsSpan()))
                throw new IOException("Foreign disposal restored/lost the retained original or misstated live continuity.");
            var retry = box.Proxy!.RestoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (retry.Succeeded || retry.MoveState is not { MutationAttempted: false, LiveContinuity: false, LastKnownPosition: LinuxExposureLinkPosition.Retained })
                throw new IOException("Closed retained capability admitted a restore retry or misstated retained evidence.");
        }
        foreach (var pair in descriptors)
            if (ReadLink($"/proc/thread-self/fd/{pair.Key}") == pair.Value)
                throw new IOException("An original owned native descriptor remains open after owner cleanup.");
        VerifyWriterReleased(); VerifyNoPrivateDescriptors(); box.Proxy?.Dispose();
        Print(new(scenario, true, true, finalized, true, true, final.Reason, final.ResourcesOpened, final.ResourcesReleased, protectedCount, "captured-native-link-target-parent-proc-and-private-descriptor-addresses", TerminalMoveState: final.MoveState));
        return 0;

        void VerifyNoPrivateDescriptors()
        {
            foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/thread-self/fd"))
            {
                var raw = ReadLink(descriptor);
                if (raw == root || raw?.StartsWith(root + "/", StringComparison.Ordinal) == true)
                    throw new IOException("A private fixture descriptor remains open after native owner cleanup.");
            }
        }
        void VerifyWriterReleased()
        {
            using var state = NativeStateDirectory.OpenOrCreate(statePath, [targets, exposures], CancellationToken.None, allowCreate: false);
            using var writer = state.OpenExistingFile("writer.lock", true, CancellationToken.None);
        }
    }
    private static ExposureLifetimeProofReport ProveForeignFinalizer(LinuxExposureOwnerRequest request, string root)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") != "0")
            throw new IOException("Foreign-finalizer proof requires child-only runtime diagnostics disabled for controlled descriptor allocation.");
        var sentinelPath = Path.Combine(root, "caller-sentinel");
        var sentinelBytes = Encoding.UTF8.GetBytes("foreign finalizer descriptor survives\n");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        using var sentinel = File.OpenHandle(sentinelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sentinelDescriptor = sentinel.DangerousGetHandle().ToInt32();
        // Reserve only raw descriptors we own. The leader never replaces a runtime or managed-owned descriptor.
        var reserved = new HashSet<int>();
        Started? started = null;
        try
        {
            for (var index = 0; index < 128; index++)
            {
                var descriptor = UnixSecureRemovalBackend.DupWithRetry(sentinelDescriptor);
                if (descriptor < 0) throw new IOException("Could not reserve a close-on-exec proof descriptor.");
                reserved.Add(descriptor);
            }
            Exception? creatorFailure = null;
            var creator = new Thread(() =>
            {
                try
                {
                    if (Native.unshare(0x400) != 0) throw new IOException("Required creator CLONE_FILES divergence refused: errno " + Marshal.GetLastPInvokeError());
                    foreach (var descriptor in reserved)
                        if (Native.close(descriptor) != 0) throw new IOException("Could not release a copied proof reservation.");
                    // This is our explicitly owned sentinel's copied descriptor, never its leader-table owner.
                    if (Native.close(sentinelDescriptor) != 0) throw new IOException("Could not release the creator's copied sentinel.");
                    started = Start(request);
                }
                catch (Exception ex) { creatorFailure = ex; }
            });
            creator.Start();
            if (!creator.Join(TimeSpan.FromSeconds(15))) throw new IOException("Private-table owner creator did not drain.");
            if (creatorFailure is not null) throw new IOException("Private-table owner creation failed.", creatorFailure);
            if (started is null || !started.Ready.Succeeded || started.Ready.Observation is null)
                throw new IOException("Private-table native owner initialization failed: " + started?.Ready.Error);
            var task = started.Ready.Observation.OwnerTask;
            var descriptors = CaptureOwnerDescriptors(task, root);
            // Runtime startup may release a formerly occupied low leader slot after the first reservation.
            // F_DUPFD_CLOEXEC can claim that vacant slot atomically; it never replaces an existing descriptor.
            for (var index = 0; index < 128 && descriptors.Keys.Any(descriptor => !reserved.Contains(descriptor) && descriptor != sentinelDescriptor); index++)
            {
                var descriptor = UnixSecureRemovalBackend.DupWithRetry(sentinelDescriptor);
                if (descriptor < 0) throw new IOException("Could not reserve a newly vacant leader descriptor.");
                reserved.Add(descriptor);
                if (descriptor > descriptors.Keys.Max()) break;
            }
            if (descriptors.Count < 5 || descriptors.Keys.Any(descriptor => !reserved.Contains(descriptor) && descriptor != sentinelDescriptor))
                throw new IOException("Actual native owner descriptors are not wholly inside controlled leader reservations: "
                    + string.Join(", ", descriptors.Where(pair => !reserved.Contains(pair.Key) && pair.Key != sentinelDescriptor).Select(pair => pair.Key + "=" + pair.Value + ";leader=" + ReadLink($"/proc/thread-self/fd/{pair.Key}")))
                    + "; leaderTask=" + ReadLink("/proc/thread-self") + "; ownerTask=" + task);
            foreach (var pair in descriptors)
            {
                if (ReadLink($"/proc/{task}/fd/{pair.Key}") != pair.Value
                    || ReadLink($"/proc/thread-self/fd/{pair.Key}") != sentinelPath || pair.Value == sentinelPath)
                    throw new IOException("Actual owner/leader descriptor-table divergence was not observed.");
            }
            VerifySentinels();
            var finalizerWitness = QueueFinalizerWitness(descriptors.Keys.ToImmutableArray(), sentinelPath);
            var weak = Abandon(started.Box);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!started.Completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
            { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(20); }
            var cleanup = started.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            RequireClean(cleanup);
            if (weak.IsAlive || cleanup.Reason != "abandoned")
                throw new IOException("Foreign-table proxy finalization did not independently stop the private owner.");
            GC.Collect(); GC.WaitForPendingFinalizers();
            var finalizer = finalizerWitness.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (finalizer.Task is null || finalizer.Task == task || !finalizer.AddressesMatch || finalizer.Addresses.Length != descriptors.Count)
                throw new IOException("Actual finalizer-thread foreign descriptor table was not witnessed: " + finalizer.Error);
            VerifySentinels();
            foreach (var pair in descriptors)
                if (ReadLink($"/proc/{task}/fd/{pair.Key}") == pair.Value)
                    throw new IOException("An original private-owner native descriptor survived cleanup.");
            return new("foreign-finalizer", true, true, true, true, true, cleanup.Reason,
                cleanup.ResourcesOpened, cleanup.ResourcesReleased, descriptors.Count,
                "reserved-leader-and-observed-finalizer-table-versus-private-owner-native-descriptor-addresses", finalizer.Task, true,
                task, true, finalizer.Addresses.Select(address => new ExposureLifetimeDescriptorWitness(address.Descriptor,
                    descriptors[address.Descriptor], sentinelPath, address.Address)).ToImmutableArray());

            void VerifySentinels()
            {
                foreach (var descriptor in descriptors.Keys)
                {
                    using var borrowed = new SafeFileHandle((IntPtr)descriptor, ownsHandle: false);
                    var bytes = new byte[sentinelBytes.Length];
                    if (ReadLink($"/proc/thread-self/fd/{descriptor}") != sentinelPath
                        || RandomAccess.Read(borrowed, bytes, 0) != bytes.Length || !bytes.AsSpan().SequenceEqual(sentinelBytes))
                        throw new IOException("Foreign finalizer cleanup closed/redirected a leader sentinel.");
                }
            }
        }
        finally
        {
            started?.Box.Proxy?.Dispose();
            foreach (var descriptor in reserved) Native.close(descriptor);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<FinalizerObservation> QueueFinalizerWitness(ImmutableArray<int> descriptors, string sentinelPath)
    {
        var completion = new TaskCompletionSource<FinalizerObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var witness = new FinalizerWitness(descriptors, sentinelPath, completion);
        GC.KeepAlive(witness);
        return completion.Task;
    }
    private sealed record FinalizerAddress(int Descriptor, string? Address);
    private sealed record FinalizerObservation(string? Task, bool AddressesMatch, ImmutableArray<FinalizerAddress> Addresses, string? Error);
    private sealed class FinalizerWitness(ImmutableArray<int> descriptors, string sentinelPath, TaskCompletionSource<FinalizerObservation> completion)
    {
        ~FinalizerWitness()
        {
            try
            {
                var task = ReadLink("/proc/thread-self");
                var addresses = descriptors.Select(descriptor => new FinalizerAddress(descriptor, ReadLink($"/proc/thread-self/fd/{descriptor}"))).ToImmutableArray();
                completion.TrySetResult(new(task, addresses.All(address => address.Address == sentinelPath), addresses, null));
            }
            catch (Exception ex) { completion.TrySetResult(new(null, false, [], ex.ToString())); }
        }
    }
    private static Dictionary<int, string> CaptureOwnerDescriptors(string task, string root)
    {
        var result = new Dictionary<int, string>();
        foreach (var path in Directory.EnumerateFileSystemEntries($"/proc/{task}/fd"))
        {
            var raw = ReadLink(path);
            if (raw is not null && (raw == root || raw.StartsWith(root + "/", StringComparison.Ordinal)
                || raw.StartsWith('/') && root.StartsWith(raw.TrimEnd('/') + "/", StringComparison.Ordinal) || raw.StartsWith("/proc/", StringComparison.Ordinal) || raw == "/proc" || raw.StartsWith("mnt:[", StringComparison.Ordinal)))
                result.Add(int.Parse(Path.GetFileName(path), System.Globalization.CultureInfo.InvariantCulture), raw);
        }
        return result;
    }
    private static string? ReadLink(string path) { try { return new FileInfo(path).LinkTarget; } catch (IOException) { return null; } }
    private static void RequireClean(LinuxExposureOwnerCleanup cleanup)
    {
        if (cleanup.ResourcesOpened != cleanup.ResourcesReleased || cleanup.Diagnostics.Any(x => x.Category == "cleanup"))
            throw new IOException("Owner cleanup failed: " + string.Join(" ", cleanup.Diagnostics.Select(x => x.Detail)));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Started Start(LinuxExposureOwnerRequest request)
    {
        var proxy = LinuxExposureOwnerContext.Start(request);
        return new(new() { Proxy = proxy }, proxy.Ready.GetAwaiter().GetResult(), proxy.Completion);
    }
    private sealed record Started(ProxyBox Box, LinuxExposureOwnerOutcome Ready, Task<LinuxExposureOwnerCleanup> Completion);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(ProxyBox box)
    { var weak = new WeakReference(box.Proxy); box.Proxy = null; return weak; }
    private sealed class ProxyBox { internal LinuxExposureOwnerContext? Proxy; }
    private static void Print(ExposureLifetimeProofReport report) => Console.WriteLine(JsonSerializer.Serialize(report, ExposureLifetimeProofJsonContext.Default.ExposureLifetimeProofReport));
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int unshare(int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int dup2(int oldfd, int newfd);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
    }
}
internal sealed record ExposureLifetimeProofReport(string Scenario, bool OwnerCleanupCompleted, bool ForeignTableDiverged,
    bool ProxyFinalized, bool WriterReleased, bool OwnedDescriptorsClosed, string CloseReason, int ResourcesOpened, int ResourcesReleased, int ConflictingSentinelsPreserved, string DescriptorObservationScope,
    string? FinalizerTask = null, bool FinalizerTableObserved = false, string? OwnerTask = null, bool RuntimeDiagnosticsDisabled = false,
    ImmutableArray<ExposureLifetimeDescriptorWitness>? DescriptorWitnesses = null, LinuxExposureOwnerMoveState? TerminalMoveState = null);
internal sealed record ExposureLifetimeDescriptorWitness(int Descriptor, string OwnerAddress, string LeaderAddress, string? FinalizerAddress);
[JsonSerializable(typeof(ExposureLifetimeProofReport))]
internal partial class ExposureLifetimeProofJsonContext : JsonSerializerContext;
