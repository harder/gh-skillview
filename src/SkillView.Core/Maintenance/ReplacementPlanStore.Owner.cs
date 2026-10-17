using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class ReplacementPlanStore
{
    private ReplacementOwnerTransition? PrepareOwner(InstalledSkill target, ResolvedSkillRevision? source, InventorySnapshot inventory, CancellationToken token,
        bool unpinOnly = false, bool initializeState = true, SelectedRevisionRequest? selection = null,
        string? selectedTree = null)
    {
        var claims = target.AllOwnershipClaims.ToArray();
        if (claims.Length == 0) return null;
        if (claims.Any(claim => claim.Provider != "gh-vercel-global" || claim.ReadOnly || claim.IsExclusiveOwner
            || claim.LockFilePath is null || claim.Selector is null || claim.EntryFingerprint is null)
            || claims.Select(claim => (OwnershipPath.EntryAddress(claim.LockFilePath!), claim.Selector, claim.EntryFingerprint)).Distinct().Count() != 1)
            throw new IOException("Update requires one verified shared gh owner; native/exclusive or ambiguous owners require their own transition.");
        if (inventory.IsPartial || !inventory.ConfiguredRootsComplete)
            throw new IOException("Shared update planning requires complete inventory before initializing private state.");
        // A configured state home may be beneath .agents (outside skills).
        // Create its private ancestors before capturing the registry namespace
        // witness; never refresh that witness after staging begins.
        CheckPlacement(inventory, token);
        if (initializeState)
        {
            using var state = NativeStateDirectory.OpenOrCreate(Root, Exclusions(inventory), token);
            using var writer = state.OpenFile("writer.lock", true, token);
            CheckWriter(writer);
        }
        var claim = claims[0];
        using var held = new GhRegistryWriteSession(claim.LockFilePath!, claim.Selector!, logger, token);
        if (!PathIdentity.Equals(held.CanonicalPath, GlobalRegistryPath()))
            throw new IOException("The captured shared owner is outside the effective gh global registry context.");
        var snapshot = held.Read(token);
        var updated = DateTimeOffset.UtcNow;
        var patch = unpinOnly ? GhRegistryEditor.PrepareUnpin(snapshot.Bytes, claim.Selector!, claim.EntryFingerprint!, token)
            : GhRegistryEditor.Prepare(snapshot.Bytes, claim.Selector!, claim.EntryFingerprint!, OwnerTree(source, selection, selectedTree), selection is { Pin: true } ? selection.Selector : null, updated, token);
        var before = GhRegistryEditor.CaptureEntry(patch.BeforeBytes, claim.Selector!, token);
        var desired = GhRegistryEditor.CaptureEntry(patch.DesiredBytes, claim.Selector!, token);
        var transition = new ReplacementOwnerTransition(held.CanonicalPath, claim.Selector!, snapshot.Identity,
            before.Bytes, desired.Bytes, before.Fingerprint, desired.Fingerprint, updated, held.ParentIdentity);
        ValidateOwnerTransition(target.FrontMatter, claims.ToImmutableArray(), source, transition, token, unpinOnly, selection, selectedTree);
        return transition;
    }

    private static string OwnerTree(ResolvedSkillRevision? source, SelectedRevisionRequest? selection, string? selectedTree)
    {
        if (source is not null) return source.Tree;
        // Read-only selected-owner preflight binds the actual installed tree;
        // it does not invent a commit/ref or claim a completed desired plan.
        if (selection is null || selectedTree is not { Length: 40 } || !selectedTree.All(char.IsAsciiHexDigit))
            throw new IOException("Selected owner preflight requires an observed full installed tree.");
        selection.Validate(); return selectedTree;
    }

    private string GlobalRegistryPath()
    {
        var home = runner?.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Cannot establish the gh registry context.");
        return OwnershipPath.EntryAddress(Path.Combine(Path.GetFullPath(home, runner?.EffectiveWorkingDirectory ?? Environment.CurrentDirectory), ".agents", SkillLockFileReader.FileName))
            ?? throw new IOException("Cannot establish the canonical gh registry parent.");
    }

    private OwnerContext? OpenOwner(ReplacementOwnerTransition? transition, CancellationToken token, bool requireBefore = false,
        Session? recoverySession = null, InventorySnapshot? inventory = null, bool exactEntryBytes = false)
    {
        if (transition is null) return null;
        if (!PathIdentity.Equals(transition.RegistryPath, GlobalRegistryPath()))
            throw new IOException("The saved registry context changed; use the original HOME context.");
        var held = new GhRegistryWriteSession(transition.RegistryPath, transition.Selector, logger, token, checkpoint, rawRecovery: recoverySession is not null);
        try
        {
            var owner = new OwnerContext(transition, held, exactEntryBytes);
            var observed = owner.Observe(token);
            if (observed.State == "torn" && recoverySession is not null)
            {
                using var journal = OpenOwnerJournal(recoverySession, inventory!, token, create: false);
                var snapshot = held.Read(token);
                var repair = journal?.FindRepair(recoverySession, snapshot, token)
                    ?? throw new IOException("Malformed registry has no applicable unfinished write from this plan; retained evidence cannot authorize repair.");
                owner.SetRepair(snapshot, repair.Review, repair.Image);
            }
            if (observed.State == "foreign" || requireBefore && observed.State != "before")
                throw new IOException("The selected owner record changed outside the saved transition; refresh and review.");
            return owner;
        }
        catch { held.Dispose(); throw; }
    }

    private static void ValidateOwnerTransition(SkillFrontMatter metadata, ImmutableArray<SkillPackage> claims,
        ResolvedSkillRevision? source, ReplacementOwnerTransition transition, CancellationToken token, bool unpinOnly = false,
        SelectedRevisionRequest? selection = null, string? selectedTree = null)
    {
        if (string.IsNullOrWhiteSpace(transition.Selector) || !Path.IsPathFullyQualified(transition.RegistryPath)
            || Path.GetFileName(transition.RegistryPath) != SkillLockFileReader.FileName
            || transition.Identity.IsDirectory || transition.Identity.IsReparsePoint
            || transition.Identity.CanonicalPath != transition.RegistryPath || claims.IsEmpty
            || !transition.ParentIdentity.IsDirectory || transition.ParentIdentity.IsReparsePoint
            || transition.ParentIdentity.CanonicalPath != Path.GetDirectoryName(transition.RegistryPath)
            || transition.BeforeEntry is null || transition.DesiredEntry is null
            || !QuarantineStore.ValidHash(transition.BeforeFingerprint) || !QuarantineStore.ValidHash(transition.DesiredFingerprint)
            || claims.Any(claim => claim.Provider != "gh-vercel-global" || claim.ReadOnly || claim.IsExclusiveOwner
                || claim.LockFilePath is null || !OwnershipPath.Same(claim.LockFilePath, transition.RegistryPath)
                || claim.Selector != transition.Selector || claim.EntryFingerprint != transition.BeforeFingerprint))
            throw new IOException("Saved gh owner authority or captured claim is inconsistent.");
        var before = EntryRegistry(transition.Selector, transition.BeforeEntry);
        var patch = unpinOnly ? GhRegistryEditor.PrepareUnpin(before, transition.Selector, transition.BeforeFingerprint, token)
            : GhRegistryEditor.Prepare(before, transition.Selector, transition.BeforeFingerprint, OwnerTree(source, selection, selectedTree), selection is { Pin: true } ? selection.Selector : null, transition.UpdatedAt, token);
        var desired = GhRegistryEditor.CaptureEntry(EntryRegistry(transition.Selector, transition.DesiredEntry), transition.Selector, token);
        if (!unpinOnly && selection is null && !Matches(patch))
            patch = GhRegistryEditor.RecreateSavedTransition(before, transition.Selector, transition.BeforeFingerprint, source!.Tree, null, transition.UpdatedAt, token);
        if (!Matches(patch))
            throw new IOException("Saved owner transition changes fields outside the approved tree/pin/timestamp policy.");
        using var document = JsonDocument.Parse(transition.BeforeEntry);
        string? Text(string name) => document.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : null;
        var record = new GhSharedLockGuard.Record(Text("sourceUrl"), Text("source"), Text("sourceType"), Text("skillPath"), Text("skillFolderHash"), Text("pinnedRef"));
        var target = new InstalledSkill
        {
            Name = metadata.Name ?? transition.Selector,
            ResolvedPath = Path.Combine(Path.GetDirectoryName(transition.RegistryPath)!, "skills", transition.Selector),
            ScanRoot = Path.GetDirectoryName(transition.RegistryPath)!,
            Scope = Scope.User,
            Agents = [],
            FrontMatter = metadata,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            OwnershipClaims = claims
        };
        GhSharedLockGuard.ValidateUpdateRecords(ImmutableDictionary<string, GhSharedLockGuard.Record>.Empty.Add(transition.Selector, record), [target]);

        bool Matches(GhRegistryPatch expected) => desired.Fingerprint == transition.DesiredFingerprint
            && expected.DesiredEntryFingerprint == transition.DesiredFingerprint
            && GhRegistryEditor.CaptureEntry(expected.DesiredBytes, transition.Selector, token).Bytes.AsSpan().SequenceEqual(transition.DesiredEntry);
    }

    private static byte[] EntryRegistry(string selector, byte[] entry)
    {
        if (entry.Length > SkillLockFileReader.MaxFileBytes) throw new IOException("Saved owner entry exceeds its limit.");
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 3); writer.WriteStartObject("skills");
            writer.WritePropertyName(selector); writer.WriteRawValue(entry); writer.WriteEndObject(); writer.WriteEndObject();
        }
        return bytes.ToArray();
    }

    private static void ValidateOwners(InstalledSkill target, CancellationToken token, OwnerContext? owner)
    {
        if (owner is null) { GhSharedLockGuard.ValidateCapturedOwners([target], token); return; }
        var observed = owner.Observe(token);
        if (observed.State is not ("before" or "desired") && !(observed.State == "torn" && observed.Repair is not null))
            throw new IOException("Owner bytes are outside the saved transition; review is required.");
        if (target.AllOwnershipClaims.Any(claim => owner.Matches(claim.LockFilePath) && !owner.Accepts(claim)))
            throw new IOException("Captured shared claim does not match this owner transition.");
        GhSharedLockGuard.ValidateCapturedOwners([target with
        { Package = null, OwnershipClaims = target.AllOwnershipClaims.Where(claim => !owner.Matches(claim.LockFilePath)).ToImmutableArray() }], token);
    }

    private static bool OwnerSatisfied(ReplacementObservation observation, OwnerContext owner, bool undo) =>
        observation.Owner?.State == (undo ? "before" : "desired")
        || owner.Transition.BeforeFingerprint == owner.Transition.DesiredFingerprint && observation.Owner?.State == "before";

    private sealed class OwnerContext(ReplacementOwnerTransition transition, GhRegistryWriteSession held, bool exactEntryBytes) : IDisposable
    {
        internal ReplacementOwnerTransition Transition { get; } = transition;
        internal GhRegistryWriteSession Held { get; } = held;
        internal byte[]? RepairBytes { get; private set; }
        private RegistryFileSnapshot? tornSnapshot;
        private ReplacementOwnerRepair? repairReview;
        internal void SetRepair(RegistryFileSnapshot snapshot, ReplacementOwnerRepair review, byte[] image)
        { tornSnapshot = snapshot; repairReview = review; RepairBytes = image; }
        internal bool Matches(string? path) => path is not null && OwnershipPath.EntryAddress(path) is { } canonical
            && PathIdentity.Equals(canonical, Transition.RegistryPath);
        internal bool Accepts(SkillPackage claim) => Matches(claim.LockFilePath) && claim.Selector == Transition.Selector
            && claim.Provider == "gh-vercel-global" && !claim.ReadOnly && !claim.IsExclusiveOwner
            && (claim.EntryFingerprint == Transition.BeforeFingerprint || claim.EntryFingerprint == Transition.DesiredFingerprint);
        internal ReplacementOwnerObservation Observe(CancellationToken token)
        {
            var snapshot = Held.Read(token);
            if (Held.ParentIdentity != Transition.ParentIdentity)
                throw new IOException("The saved registry parent identity or generation changed; no owner mutation was admitted.");
            if (!NativeDirectoryMoveLease.SameObject(Transition.Identity, snapshot.Identity))
                throw new IOException("The saved registry file object was replaced; no owner mutation was admitted.");
            if (GhRegistryWriteSession.MalformedJson(snapshot.Bytes))
            {
                if (tornSnapshot is not null && (snapshot.Identity != tornSnapshot.Identity || snapshot.ContentHash != tornSnapshot.ContentHash))
                    throw new IOException("Torn registry bytes changed during inspection; inspect again.");
                return new("torn", Transition.RegistryPath, Transition.Selector, snapshot.ContentHash, snapshot.Bytes.Length, snapshot.Identity, repairReview);
            }
            var entry = GhRegistryEditor.CaptureEntry(snapshot.Bytes, Transition.Selector, token);
            var state = entry.Fingerprint == Transition.BeforeFingerprint
                && (!exactEntryBytes || entry.Bytes.AsSpan().SequenceEqual(Transition.BeforeEntry)) ? "before"
                : entry.Fingerprint == Transition.DesiredFingerprint
                    && (!exactEntryBytes || entry.Bytes.AsSpan().SequenceEqual(Transition.DesiredEntry)) ? "desired" : "foreign";
            return new(state, Transition.RegistryPath, Transition.Selector, snapshot.ContentHash, snapshot.Bytes.Length, snapshot.Identity);
        }
        internal byte[]? ReadFor(string path, CancellationToken token)
        {
            if (!Matches(path)) return null;
            var observation = Observe(token);
            return observation.State == "torn" ? RepairBytes ?? throw new IOException("No reviewed owner repair image is available.") : Held.Read(token).Bytes;
        }
        internal InventorySnapshot RecoveryInventory(InventorySnapshot inventory, CancellationToken token)
        {
            if (tornSnapshot is null) return inventory;
            _ = Observe(token);
            if (RepairBytes is null) throw new IOException("Unrecognized malformed registry cannot bypass inventory diagnostics.");
            // A failed aggregate provider is repairable only when every
            // structured failure belongs to this exact held malformed file.
            var attributable = !inventory.OwnershipReadDiagnostics.IsEmpty
                && inventory.OwnershipReadDiagnostics.All(diagnostic => Matches(diagnostic.Path));
            return inventory with
            {
                Providers = attributable ? inventory.Providers.Where(provider => provider.Provider != "lockfiles").ToImmutableArray() : inventory.Providers,
                OwnershipReadDiagnostics = inventory.OwnershipReadDiagnostics.Where(diagnostic => !Matches(diagnostic.Path)).ToImmutableArray()
            };
        }
        internal SkillPackage Normalize(SkillPackage claim)
        {
            if (!Accepts(claim)) return claim;
            using var document = JsonDocument.Parse(Transition.BeforeEntry);
            var entry = document.RootElement;
            return claim with
            {
                EntryFingerprint = Transition.BeforeFingerprint,
                ContentHash = entry.GetProperty("skillFolderHash").GetString(),
                PinnedRef = entry.TryGetProperty("pinnedRef", out var pin) ? pin.GetString() : null
            };
        }
        public void Dispose() => Held.Dispose();
    }
}
