using System.Text.Json;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    // A payload topology cannot establish that this operation published its
    // owner transition: payload moves precede the first owner-write intent.
    private bool RetirementOwnerCompleted(Session session, string topology, OwnerJournal? journal, CancellationToken token)
    {
        if (journal is null || topology is not ("installed" or "restored")) return false;
        ReplacementStep? payload = null, intent = null, complete = null;
        for (var index = 0; index < session.StepCount; index++)
        {
            var step = Read(session.Operation, $"step-{index:D4}.json", ReplacementJsonContext.Default.ReplacementStep, token);
            if (step.Sequence != index || step.PlanFingerprint != session.Plan.ReviewFingerprint || !ValidOwnerStep(step, session.Plan))
                throw new IOException("Owner completion steps changed during retirement inspection.");
            if (!step.Status.StartsWith("owner-", StringComparison.Ordinal)) { payload = step; intent = null; complete = null; }
            else if (step.Status == (topology == "installed" ? "owner-forward-intent" : "owner-undo-intent")) { intent = step; complete = null; }
            else if (step.Status == (topology == "installed" ? "owner-forward-complete" : "owner-undo-complete")) complete = step;
            else { intent = null; complete = null; }
        }
        return payload?.Status == (topology == "installed" ? "install-complete" : "restore-complete")
            && payload.Observation.Topology == topology && intent is not null && complete is not null
            && payload.Sequence < intent.Sequence && intent.Sequence < complete.Sequence
            && journal.ProvesRetirementCompletion(intent, complete, topology == "restored");
    }

    private string RequireRetirementOwner(Session session, string topology, OwnerJournal? journal, CancellationToken token,
        bool completedBeforeJournalRemoval = false)
    {
        var transition = EffectiveOwner(session.Plan);
        if (topology == "ready" || transition is null || transition.BeforeFingerprint == transition.DesiredFingerprint) return "no-owner-write";
        if (RetirementOwnerCompleted(session, topology, journal, token)) return "local-owner-complete";
        // This invocation may have removed a journal which it actually verified.
        // A new session/marker never supplies this fact. Use it only after every
        // payload slot is gone and the unchanged main steps were revalidated.
        if (completedBeforeJournalRemoval && journal is null
            && !RetirementEntries(session.Operation, token).Any(entry => RetirementPayloads.Contains(entry.Name)))
            return "local-owner-complete";
        var observed = ObserveRetirementOwner(transition, token);
        if (observed.State == (topology == "installed" ? "before" : "desired"))
            throw new IOException("This operation's owner transition is still pending. Preserve its payloads, plan and journal; use ordinary Inspect and Recover before retirement.");
        return JsonSerializer.Serialize(observed, ReplacementJsonContext.Default.ReplacementOwnerObservation);
    }

    private ReplacementOwnerObservation ObserveRetirementOwner(ReplacementOwnerTransition transition, CancellationToken token)
    {
        if (!PathIdentity.Equals(transition.RegistryPath, GlobalRegistryPath()))
            throw new IOException("Cannot verify this operation's owner transition in the current registry context; use its original HOME for inspection.");
        var parentPath = Path.GetDirectoryName(transition.RegistryPath)!;
        using var parent = OwnedDirectoryLease.Open(parentPath);
        var before = parent.Capture(token);
        ReplacementOwnerObservation observed;
        if (!RetirementPathPresent(transition.RegistryPath))
            observed = new("absent", transition.RegistryPath, transition.Selector, "", 0, before.Identity);
        else
        {
            // Existing bounded native data-file lease, lock, path and byte
            // validation only. No owner context, client or registry write.
            using var held = NativeRegistryFileLease.Open(transition.RegistryPath, token);
            var snapshot = held.Read(token);
            if (held.ParentIdentity != before.Identity) throw new IOException("Registry parent changed during retirement inspection.");
            var state = RetirementRegistryState(snapshot.Bytes, transition, token);
            var after = held.Read(token);
            if (after.Identity != snapshot.Identity || after.ContentHash != snapshot.ContentHash)
                throw new IOException("Registry changed during retirement inspection.");
            observed = new(state, transition.RegistryPath, transition.Selector, snapshot.ContentHash, snapshot.Bytes.Length, snapshot.Identity);
        }
        if (parent.Capture(token).Identity != before.Identity)
            throw new IOException("Registry parent changed during retirement inspection.");
        return observed;
    }

    private static string RetirementRegistryState(byte[] bytes, ReplacementOwnerTransition transition, CancellationToken token)
    {
        // Absence is meaningful only in a complete, supported, unambiguous
        // registry. Do not mistake malformed JSON or an unsupported entry for a
        // foreign manager change. Keep ordinary GhRegistryEditor unchanged.
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            var properties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!))
                    throw new IOException("Ambiguous owner registry remains protected.");
            }
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var number) || number != 3
                || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object
                || skills.EnumerateObject().Take(SkillLockFileReader.MaxEntries + 1).Count() > SkillLockFileReader.MaxEntries)
                throw new IOException("Unsupported owner registry remains protected.");
            if (!skills.TryGetProperty(transition.Selector, out _)) return "absent";
            var selected = GhRegistryEditor.CaptureEntry(bytes, transition.Selector, token);
            return selected.Fingerprint == transition.BeforeFingerprint ? "before"
                : selected.Fingerprint == transition.DesiredFingerprint ? "desired" : "foreign";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        { throw new IOException("Unreadable or torn owner registry remains protected; inspect ordinary recovery.", ex); }
    }
}
