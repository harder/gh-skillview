using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Threading;

namespace SkillView.Evaluation;

/// Private input publication and cleanup retain native authority throughout.
internal sealed class EvaluationWorkspace : IDisposable
{
    private readonly NativeStateDirectory state;
    private readonly OwnedDirectoryLease ownership;
    private readonly FileStream writer;
    private readonly Logger logger;
    private NativePayloadDirectory? input;
    private PayloadSnapshot? inputSnapshot;
    internal string Path => state.CanonicalPath;
    internal string InputPath => System.IO.Path.Combine(Path, "input");
    internal string DockerConfigPath => System.IO.Path.Combine(Path, "docker");
    internal string? CleanupError { get; private set; }

    private EvaluationWorkspace(NativeStateDirectory state, Logger logger, CancellationToken token)
    {
        this.state = state; this.logger = logger;
        writer = state.OpenFile("writer.lock", true, token);
        try { ownership = state.CaptureOwnership(token); }
        catch { writer.Dispose(); throw; }
    }

    internal static EvaluationWorkspace Create(EvaluationReview review, Logger logger, CancellationToken token)
    {
        var path = System.IO.Path.Combine("/var/tmp", "skillview-evaluation-" + Guid.NewGuid().ToString("N"));
        NativeStateDirectory? state = null;
        EvaluationWorkspace? workspace = null;
        try
        {
            state = NativeStateDirectory.OpenOrCreate(path, [review.Payload.Path], token);
            workspace = new(state, logger, token);
            using (state.CreateStagingDirectory("docker", token)) { }
            var input = state.CreatePayloadDirectory("input", token);
            workspace.input = input;
            using (var skill = input.CreateDirectory("skill", token)) Build(skill, "");
            using (var file = input.CreateFile("request.json", token))
            {
                file.Write(review.Request);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file.SafeFileHandle, (UnixFileMode)292);
                else throw new IOException("Evaluation staging requires Linux.");
                file.Flush(true);
            }
            input.Seal(493, token);
            var copied = input.FlushAndCapture(token);
            workspace.inputSnapshot = copied;
            if (copied.Entries.Length != review.Payload.Entries.Length + 2) throw new IOException("Private evaluation path set changed.");
            foreach (var entry in copied.Entries.Where(entry => entry.Kind == "file"))
            {
                var expected = entry.Path == "request.json" ? review.Request : review.Files[entry.Path[6..]];
                if (entry.Bytes != expected.Length || !entry.ContentHash.Equals(EvaluationReview.Hash(expected), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Private evaluation input differs from the frozen capture.");
            }
            state.ValidateLocation(token); return workspace;

            void Build(NativePayloadDirectory directory, string parent)
            {
                foreach (var entry in review.Payload.Entries.Where(entry => Parent(entry.Path) == parent))
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
                    if (entry.Kind == "directory")
                    {
                        using var child = directory.CreateDirectory(name, token); Build(child, entry.Path);
                    }
                    else
                    {
                        using var file = directory.CreateFile(name, token);
                        file.Write(review.Files[entry.Path]);
                        // Preserve executable bits without restoring owner/ACL/xattr authority.
                        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file.SafeFileHandle, (UnixFileMode)(292 | (entry.UnixMode ?? 0) & 73));
                        else throw new IOException("Evaluation staging requires Linux.");
                        file.Flush(true);
                    }
                }
                // Owner write permits native cleanup; the bind is read-only and
                // the evaluator runs as a different UID.
                directory.Seal(493, token);
            }
        }
        catch (Exception exception)
        {
            if (workspace is not null) workspace.Dispose(); else state?.Dispose();
            if (exception is not (IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)) throw;
            if (exception is OperationCanceledException && workspace?.CleanupError is null) throw;
            throw new IOException("Private evaluation staging failed at " + path + ": " + exception.Message
                + (workspace?.CleanupError is { } cleanup ? "\n" + cleanup : workspace is null ? "; cleanup was not verified." : ""), exception);
        }
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";
    internal void Validate(CancellationToken token)
    {
        state.ValidateLocation(token);
        var actual = input?.FlushAndCapture(token) ?? throw new IOException("Held evaluation input unavailable.");
        if (inputSnapshot is null || actual.Fingerprint != inputSnapshot.Fingerprint
            || actual.ObjectStateFingerprint != inputSnapshot.ObjectStateFingerprint || actual.Path != inputSnapshot.Path)
            throw new IOException("Private evaluation bytes or native objects changed after capture.");
    }

    public void Dispose()
    {
        input?.Dispose(); input = null;
        try
        {
            using var deadline = new CancellationSource(TimeSpan.FromSeconds(5), CancellationCallbackReporter.For(logger, "evaluation input cleanup"));
            var snapshot = ownership.Capture(deadline.Token); var removed = false; var errors = 0;
            SecureRemovalBackend.RemoveTree(Path, snapshot.Identity, false, PayloadSnapshot.MaxDepth + 4,
                _ => { }, (_, _) => { }, (path, directory) => { if (directory && PathIdentity.Equals(path, Path)) removed = true; },
                (_, _) => errors++, deadline.Token);
            if (!removed || errors != 0) throw new IOException("Native input cleanup was incomplete.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException)
        { CleanupError = "Private inputs may remain at " + Path + ": " + exception.Message; }
        finally { writer.Dispose(); ownership.Dispose(); state.Dispose(); }
    }
}
