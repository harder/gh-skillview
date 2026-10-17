using System.Collections.Immutable;
using System.Diagnostics;
using SkillView.Maintenance;
using SkillView.Threading;

namespace SkillView.Inventory;

internal sealed record LinuxExposureOwnerRequest(string ExposureRoot, string EntryPath, string TargetRoot, string TargetPath,
    ExposureDirectoryEvidence ExpectedDirectory, SecureFileIdentity ExpectedLink, ImmutableArray<byte> ExpectedRawTarget,
    string ExpectedLinkMetadataFingerprint, SecureFileIdentity ExpectedTargetRoot, SecureFileIdentity ExpectedTargetParent,
    SecureFileIdentity ExpectedTarget, string TargetFingerprint, string TargetObjectFingerprint,
    string StatePath, string SlotName, TimeSpan IdleTimeout, TimeSpan AbsoluteTimeout);
internal sealed record LinuxExposureOwnerDiagnostic(string Category, string Detail, int OriginalCharacters, bool Truncated);
internal enum LinuxExposureLinkPosition { Original, Retained, Restored, Uncertain, Unavailable }
internal enum LinuxExposureOwnerProofCheckpoint { None, CancelCompletedMoveBeforePublication }
internal sealed record LinuxExposureOwnerMoveState(bool MutationAttempted, bool MutationCompleted, bool Uncertain,
    LinuxExposureLinkPosition Position, bool LiveContinuity, LinuxExposureLinkPosition LastKnownPosition);
internal sealed record LinuxExposureOwnerObservation(string OwnerTask, SecureFileIdentity Link, ImmutableArray<byte> RawTarget,
    string TargetFingerprint, string TargetObjectFingerprint, string PreservationCoverage,
    ExposureLinkEvidence Original, ExposureLinkEvidence Current, bool RetainedSlotEmpty);
internal sealed record LinuxExposureOwnerOutcome(bool Succeeded, bool Canceled, LinuxExposureOwnerObservation? Observation, string? Error,
    LinuxExposureOwnerMoveState? MoveState = null);
internal sealed record LinuxExposureOwnerCleanup(string Reason, int ResourcesOpened, int ResourcesReleased,
    ImmutableArray<LinuxExposureOwnerDiagnostic> Diagnostics, long OmittedDiagnostics, LinuxExposureOwnerMoveState? MoveState = null);

/// Bounded native primitive. Placement/owner policy and durable intent remain the caller's contract.
/// No borrowed native objects, caller delegates, or native resources cross this proxy boundary.
internal sealed class LinuxExposureOwnerContext : IDisposable
{
    private readonly Owner state;
    private LinuxExposureOwnerContext(Owner state) => this.state = state;
    internal Task<LinuxExposureOwnerOutcome> Ready => state.Ready.Task;
    internal Task<LinuxExposureOwnerCleanup> Completion => state.Completion.Task;
    internal Task<LinuxExposureOwnerOutcome> InspectAsync(CancellationToken token) => state.Enqueue(CommandKind.Inspect, token);
    internal Task<LinuxExposureOwnerOutcome> RetainAsync(CancellationToken token) => state.Enqueue(CommandKind.Retain, token);
    internal Task<LinuxExposureOwnerOutcome> RestoreAsync(CancellationToken token) => state.Enqueue(CommandKind.Restore, token);
    // Legacy metadata DTOs contain mutable byte arrays. Never share those authority-owned
    // arrays with a returned observation, or with a different returned observation.
    internal static ExposureLinkEvidence SnapshotForCaller(ExposureLinkEvidence evidence) => evidence with
    {
        Metadata = evidence.Metadata with
        {
            Attributes = CopyAttributes(evidence.Metadata.Attributes),
            Windows = evidence.Metadata.Windows is { } windows ? windows with
            {
                SecurityDescriptor = windows.SecurityDescriptor.ToArray(),
                Streams = windows.Streams.Select(stream => stream with { Bytes = stream.Bytes.ToArray() }).ToImmutableArray()
            } : null
        },
        Preservation = evidence.Preservation is { } preservation ? preservation with
        { VisibleAttributes = CopyAttributes(preservation.VisibleAttributes) } : null
    };
    private static ImmutableArray<PayloadExtendedAttribute> CopyAttributes(ImmutableArray<PayloadExtendedAttribute> attributes) =>
        attributes.Select(attribute => attribute with { Value = attribute.Value.ToArray() }).ToImmutableArray();
    private static readonly object AuthorityKey = new();
    internal sealed class NativeAuthority
    {
        private readonly Thread thread;
        private bool closed;
        internal NativeAuthority(object key)
        {
            if (!ReferenceEquals(key, AuthorityKey)) throw new IOException("Only the native owner may issue Linux preservation authority.");
            thread = Thread.CurrentThread;
        }
        internal void Verify()
        {
            if (closed || !ReferenceEquals(thread, Thread.CurrentThread)) throw new IOException("Linux preservation authority belongs to its live native owner thread.");
        }
        internal void Close() { Verify(); closed = true; }
    }
    internal static LinuxExposureOwnerContext Start(LinuxExposureOwnerRequest request)
        => StartCore(request, LinuxExposureOwnerProofCheckpoint.None);
    // Friend tests/proof only: no request, environment, serialization or CLI selects this checkpoint.
    internal static LinuxExposureOwnerContext StartForProof(LinuxExposureOwnerRequest request, LinuxExposureOwnerProofCheckpoint checkpoint)
    {
        if (checkpoint != LinuxExposureOwnerProofCheckpoint.CancelCompletedMoveBeforePublication)
            throw new IOException("A fixed completed-move cancellation proof checkpoint is required.");
        return StartCore(request, checkpoint);
    }
    private static LinuxExposureOwnerContext StartCore(LinuxExposureOwnerRequest request, LinuxExposureOwnerProofCheckpoint checkpoint)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException("A Linux native owner context is required.");
        if (request.IdleTimeout < TimeSpan.FromMilliseconds(50) || request.IdleTimeout > TimeSpan.FromMinutes(5)
            || request.AbsoluteTimeout < request.IdleTimeout || request.AbsoluteTimeout > TimeSpan.FromMinutes(10))
            throw new IOException("Linux owner idle/absolute lifetime is outside its finite bound.");
        foreach (var path in new[] { request.ExposureRoot, request.EntryPath, request.TargetRoot, request.TargetPath, request.StatePath })
            if (!Path.IsPathFullyQualified(path)) throw new IOException("Owner requests require absolute policy/evidence paths.");
        if (Interlocked.Increment(ref Owner.Active) > 4)
        { Interlocked.Decrement(ref Owner.Active); throw new IOException("Four live Linux owner contexts already consume the process bound."); }
        Owner? owner = null;
        try
        {
            owner = new(request, checkpoint);
            var proxy = new LinuxExposureOwnerContext(owner);
            var thread = new Thread(owner.Run) { IsBackground = true, Name = "SkillView Linux exposure owner" };
            if (ExecutionContext.IsFlowSuppressed()) thread.Start();
            else { using (ExecutionContext.SuppressFlow()) thread.Start(); }
            return proxy;
        }
        catch
        {
            Interlocked.Decrement(ref Owner.Active);
            owner?.FailStart();
            throw;
        }
    }
    public void Dispose()
    {
        state.Stop("disposed");
        // No native close/unlock and no ownership lock is held while waiting.
        state.Completion.Task.GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
    ~LinuxExposureOwnerContext() => state.Stop("abandoned", wake: false);

    private enum CommandKind { Inspect, Retain, Restore }
    private sealed class Command(CommandKind kind, CancellationToken token)
    {
        internal readonly CommandKind Kind = kind;
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<LinuxExposureOwnerOutcome> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Owner(LinuxExposureOwnerRequest request, LinuxExposureOwnerProofCheckpoint checkpoint)
    {
        internal static int Active;
        internal readonly TaskCompletionSource<LinuxExposureOwnerOutcome> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<LinuxExposureOwnerCleanup> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private readonly List<LinuxExposureOwnerDiagnostic> diagnostics = [];
        private Command? pending;
        private bool nativeMoveInFlight;
        private CancellationSource? activeCancellation;
        private LinuxExposureOwnerMoveState lastMove = new(false, false, false, LinuxExposureLinkPosition.Unavailable, false, LinuxExposureLinkPosition.Unavailable);
        private int stopping;
        private string reason = "closed";
        private long omitted;
        private int diagnosticCharacters;
        private const int MaxDiagnosticCharacters = 512 * 1024;
        internal void Stop(string why, bool wake = true)
        {
            Interlocked.CompareExchange(ref reason, why, "closed");
            Volatile.Write(ref stopping, 1);
            // A finalizer must not wait for the owner or even its admission lock.
            // The bounded idle poll observes this atomic signal without a finalizer lock/callback.
            if (!wake) return;
            CancellationSource? active;
            lock (gate) { active = activeCancellation; Monitor.PulseAll(gate); }
            active?.TryCancel(); // callbacks run outside the ownership lock; owned reporter retains failures.
        }

        internal void FailStart()
        {
            Ready.TrySetResult(new(false, false, null, "Native owner thread could not start."));
            Completion.TrySetResult(new("start-failed", 0, 0, [], 0));
        }
        internal Task<LinuxExposureOwnerOutcome> Enqueue(CommandKind kind, CancellationToken token)
        {
            lock (gate)
            {
                if (Volatile.Read(ref stopping) != 0) return Task.FromResult(new LinuxExposureOwnerOutcome(false, false, null, "Native owner admission is closed.", RejectedState()));
                if (token.IsCancellationRequested) return Task.FromResult(new LinuxExposureOwnerOutcome(false, true, null, "Command canceled before admission.", RejectedState()));
                if (pending is not null) return Task.FromResult(new LinuxExposureOwnerOutcome(false, false, null, "One waiting native command already consumes the queue bound.", RejectedState()));
                pending = new(kind, token); Monitor.PulseAll(gate); return pending.Reply.Task;
            }
        }
        private LinuxExposureOwnerMoveState RejectedState()
        {
            var known = Volatile.Read(ref lastMove);
            return known with
            {
                MutationAttempted = false,
                MutationCompleted = false,
                Uncertain = known.Uncertain || nativeMoveInFlight,
                Position = nativeMoveInFlight ? LinuxExposureLinkPosition.Uncertain : known.Position
            };
        }
        private void Diagnostic(string category, Exception exception)
        {
            // Same full flattened callback evidence as CancellationCallbackReporter,
            // returned as managed data for logging on the caller's own context.
            var detail = exception is AggregateException aggregate ? aggregate.Flatten().ToString() : exception.ToString();
            lock (gate)
            {
                if (diagnostics.Count >= 128 || diagnosticCharacters >= MaxDiagnosticCharacters) { omitted++; return; }
                var length = Math.Min(detail.Length, Math.Min(65536, MaxDiagnosticCharacters - diagnosticCharacters));
                diagnostics.Add(new(category, detail[..length], detail.Length, length != detail.Length));
                diagnosticCharacters += length;
            }
        }
        internal void Run()
        {
            NativeStateDirectory? state = null; FileStream? writer = null; NativeRetainedLinkSlot? slot = null;
            NativeDirectoryMoveLease? target = null; NativeExposureLinkLease? link = null;
            var opened = 0; var released = 0; CancellationSource? cancellation = null;
            NativeAuthority? authority = null;
            Command? executing = null;
            var startingSequence = 0;
            try
            {
                cancellation = new(request.AbsoluteTimeout, ex => Diagnostic("cancellation", ex));
                var stopAlreadyRequested = Volatile.Read(ref stopping) != 0;
                if (stopAlreadyRequested) cancellation.TryCancel();
                var token = cancellation.Token;
                // The owner creates EVERY native object. Private namespaces and
                // writer precede target/source generation freeze and comparison.
                state = NativeStateDirectory.OpenOrCreate(request.StatePath, [request.TargetRoot, request.ExposureRoot], token); opened++;
                writer = state.OpenFile("writer.lock", true, token); opened++;
                slot = NativeRetainedLinkSlot.Create(state, request.SlotName, token); opened++;
                state.FlushPublication(token);
                target = NativeDirectoryMoveLease.Open(request.TargetRoot, request.TargetPath, token); opened++;
                authority = new(AuthorityKey);
                link = NativeExposureLinkLease.OpenOwnedLinux(authority, request.ExposureRoot, request.EntryPath, target,
                    token, ex => Diagnostic("reconciliation-cancellation", ex)); opened++;
                var observation = Inspect(token);
                PublishState(false, false);
                Ready.TrySetResult(new(true, false, observation, null, lastMove));
                var lastCommand = Stopwatch.GetTimestamp();
                while (true)
                {
                    Command? command;
                    lock (gate)
                    {
                        while (Volatile.Read(ref stopping) == 0 && pending is null)
                        {
                            if (token.IsCancellationRequested) { Stop("absolute-expired", wake: false); break; }
                            var remaining = request.IdleTimeout - Stopwatch.GetElapsedTime(lastCommand);
                            if (remaining <= TimeSpan.Zero) { Stop("idle-expired", wake: false); break; }
                            Monitor.Wait(gate, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                        }
                        if (Volatile.Read(ref stopping) != 0) break;
                        command = pending; pending = null;
                        nativeMoveInFlight = command!.Kind != CommandKind.Inspect;
                    }
                    var before = link.MutationSequence;
                    executing = command; startingSequence = before;
                    LinuxExposureOwnerObservation? actual = null;
                    Exception? error = null;
                    using var operation = new CancellationSource(command!.Token, token, ex => Diagnostic("command-cancellation", ex));
                    lock (gate) activeCancellation = operation;
                    try
                    {
                        CheckStop(operation.Token);
                        if (command.Kind == CommandKind.Inspect) actual = Inspect(operation.Token);
                        else
                        {
                            Inspect(operation.Token); // exact reviewed I/source/target policy before either move.
                            if (command.Kind == CommandKind.Retain) link.Retain(slot, operation.Token);
                            else link.Restore(operation.Token);
                        }
                    }
                    catch (Exception ex) { error = ex; Diagnostic("command", ex); }
                    finally { lock (gate) activeCancellation = null; }
                    var attempted = link.MutationSequence != before;
                    var completed = attempted && link.MutationCompleted;
                    var publicationUncertain = false;
                    if (completed)
                    {
                        if (checkpoint == LinuxExposureOwnerProofCheckpoint.CancelCompletedMoveBeforePublication)
                            operation.TryCancel(); // actual owned token cancellation; real publication below still runs.
                        // The caller/lifetime cancellation cannot interrupt reconciliation or publication of a completed move.
                        using var reconciliation = new CancellationSource(TimeSpan.FromSeconds(30), ex => Diagnostic("publication-cancellation", ex));
                        try
                        {
                            actual = Inspect(reconciliation.Token, allowStopped: true);
                            state.FlushPublication(reconciliation.Token); // writer stays open through owning device/namespace barrier.
                        }
                        catch (Exception ex) { publicationUncertain = true; error = ex; Diagnostic("publication", ex); }
                    }
                    var canceled = error is OperationCanceledException || operation.Token.IsCancellationRequested || Volatile.Read(ref stopping) != 0;
                    PublishState(attempted, completed, publicationUncertain);
                    if (error is not null || canceled || lastMove.Uncertain)
                    {
                        Stop(lastMove.Uncertain ? "uncertain-move" : "command-failed");
                        command.Reply.TrySetResult(new(false, canceled, actual, error?.Message ?? "Command canceled after native observation.", lastMove));
                    }
                    else
                    {
                        command.Reply.TrySetResult(new(true, false, actual, null, lastMove));
                        lastCommand = Stopwatch.GetTimestamp();
                    }
                    executing = null;
                }

                void PublishState(bool attempted, bool completed, bool publicationUncertain = false)
                {
                    var uncertain = link.IsUncertain || publicationUncertain;
                    lock (gate)
                    {
                        Volatile.Write(ref lastMove, new(attempted, completed, uncertain,
                            uncertain ? LinuxExposureLinkPosition.Uncertain : link.Position, true, link.Position));
                        nativeMoveInFlight = false;
                    }
                }

                void CheckStop(CancellationToken workToken)
                {
                    var requested = Volatile.Read(ref stopping) != 0;
                    if (requested) { cancellation.TryCancel(); workToken.ThrowIfCancellationRequested(); }
                    workToken.ThrowIfCancellationRequested();
                }
                LinuxExposureOwnerObservation Inspect(CancellationToken workToken, bool allowStopped = false)
                {
                    if (!allowStopped) CheckStop(workToken);
                    link.Revalidate(workToken);
                    var directory = link.OriginalDirectory;
                    if (directory.Root != request.ExpectedDirectory.Root || directory.Parent != request.ExpectedDirectory.Parent
                        || !directory.Entries.SequenceEqual(request.ExpectedDirectory.Entries)
                        || link.Original.Identity != request.ExpectedLink
                        || !link.Original.RawTarget.AsSpan().SequenceEqual(request.ExpectedRawTarget.AsSpan())
                        || link.Original.MetadataFingerprint != request.ExpectedLinkMetadataFingerprint
                        || target.RootIdentity != request.ExpectedTargetRoot || target.ParentIdentity != request.ExpectedTargetParent
                        || target.TargetIdentity != request.ExpectedTarget)
                        throw new IOException("Reopened owner authority differs from immutable reviewed source/target evidence.");
                    if (!allowStopped) CheckStop(workToken);
                    var payload = target.Capture(workToken);
                    if (!allowStopped) CheckStop(workToken);
                    if (payload.Fingerprint != request.TargetFingerprint || payload.ObjectStateFingerprint != request.TargetObjectFingerprint)
                        throw new IOException("Reopened target payload differs from immutable reviewed fingerprints.");
                    return new(link.Original.Preservation!.Mount.Task, link.Current.Identity, link.Current.RawTarget,
                        payload.Fingerprint, payload.ObjectStateFingerprint, link.Original.Preservation.Coverage,
                        SnapshotForCaller(link.Original), SnapshotForCaller(link.Current), slot.Directory.Capture(workToken).Entries.IsEmpty);
                }
            }
            catch (Exception ex)
            {
                Diagnostic(executing is null ? "initialization" : "owner-command", ex);
                if (executing is not null && link is not null)
                {
                    var attempted = link.MutationSequence != startingSequence;
                    var uncertain = link.IsUncertain || attempted;
                    Volatile.Write(ref lastMove, new(attempted, attempted && link.MutationCompleted, uncertain,
                        uncertain ? LinuxExposureLinkPosition.Uncertain : link.Position, true, link.Position));
                    executing.Reply.TrySetResult(new(false, ex is OperationCanceledException, null, ex.Message, lastMove));
                }
                Ready.TrySetResult(new(false, ex is OperationCanceledException, null, ex.Message));
                Stop("initialization-failed");
            }
            finally
            {
                // State strongly roots all partially initialized resources here;
                // neither proxy abandonment nor a foreign finalizer can own them.
                Volatile.Write(ref lastMove, Volatile.Read(ref lastMove) with { LiveContinuity = false });
                Close(link); Close(target); Close(slot); Close(writer); Close(state);
                try { authority?.Close(); } catch (Exception ex) { Diagnostic("cleanup", ex); }
                try { cancellation?.Dispose(); } catch (Exception ex) { Diagnostic("cleanup", ex); }
                lock (gate)
                {
                    Volatile.Write(ref stopping, 1);
                    nativeMoveInFlight = false;
                    pending?.Reply.TrySetResult(new(false, false, null, "Native owner closed before queued command.", RejectedState())); pending = null;
                }
                Interlocked.Decrement(ref Active);
                lock (gate) Completion.TrySetResult(new(reason, opened, released, diagnostics.ToImmutableArray(), omitted, lastMove));
            }
            void Close(IDisposable? resource)
            {
                if (resource is null) return;
                try { resource.Dispose(); released++; }
                catch (Exception ex) { Diagnostic("cleanup", ex); }
            }
        }
    }
}
