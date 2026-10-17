using System.Collections.Immutable;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ApprovedPayloadStoreTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-approved-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Logger logger = new();
    private string Home => Path.Combine(temp.FullName, "home");
    private string Cwd => Path.Combine(temp.FullName, "cwd");
    private string Skills => Path.Combine(Home, "skills");
    private string Payload => Path.Combine(Skills, "demo");
    private string Source => Path.Combine(Home, "upstream-source");
    private ProcessRunner runner = null!;
    private ApprovedPayloadStore Store => ApprovedPayloadStore.For(runner, logger);
    private string ReplacementRoot => Path.Combine(Path.GetDirectoryName(MaintenanceBackupStore.ResolveRoot(runner))!, "replacements");
    private ReplacementPlanStore Plans => new(ReplacementRoot, logger, runner: runner);

    public ApprovedPayloadStoreTests()
    {
        Directory.CreateDirectory(Payload); Directory.CreateDirectory(Source); Directory.CreateDirectory(Cwd);
        Directory.CreateDirectory(Path.Combine(Cwd, ".git"));
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), "---\nname: demo\ndescription: Custom approved fixture\nversion: unchanged\nmetadata:\n  local-path: " + Source.Replace("\\", "/") + "\n  author-policy: preserve\n---\nIntentionally customized instructions.\n");
        File.WriteAllText(Path.Combine(Payload, "extra.sh"), "captured authored extra\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Payload, "extra.sh"), (UnixFileMode)493);
        File.WriteAllText(Path.Combine(Source, "SKILL.md"), "---\nname: demo\n---\nA different source; never a fabricated baseline.\n");
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = Home,
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(Home, "private-state"),
            ["XDG_STATE_HOME"] = Path.Combine(Home, "state"),
            ["PATH"] = "",
            ["GH_PATH"] = "",
            ["GH_EXTENSION"] = "0",
            ["TMPDIR"] = "/var/tmp"
        };
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) values[key] = "";
        values["GIT_DIR"] = Path.Combine(Cwd, ".git"); values["GIT_WORK_TREE"] = Cwd; values["GIT_COMMON_DIR"] = Path.Combine(Cwd, ".git"); values["GIT_CONFIG_COUNT"] = "0";
        runner = new(logger, environment: values, defaultWorkingDirectory: Cwd);
    }

    private InventorySnapshot Inventory() => InventorySnapshot.Empty with
    {
        Skills = new LocalSkillScanner(logger).Scan([new(Skills, Scope.Custom, null)]).ToImmutableArray(),
        ConfiguredRoots = [new(Skills, Scope.Custom, null)],
        ScannedRoots = [new(Skills, Scope.Custom, null)]
    };
    private InstalledSkill Target => Assert.Single(Inventory().Skills);
    private string Fingerprint => PayloadSnapshot.Capture(Payload, Token).Fingerprint;
    private ApprovedPayloadReference CaptureOrSkip()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        try { return Store.CaptureCustom(Target, Inventory(), before.Fingerprint, Token); }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
        {
            Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
            Assert.Empty(Directory.EnumerateFiles(Store.Root, "approved.json", SearchOption.AllDirectories));
            Assert.Skip("Completed approval/native restore proof requires the existing complete metadata authority; no captured-I archive is promoted to pristine B/C.");
            throw;
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("diagnostics")]
    [InlineData("partial")]
    public void InvalidCapturedSelectionRefusesBeforeApprovalState(string failure)
    {
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills);
        if (failure == "invalid") target = target with { Validity = ValidityState.InvalidMetadata };
        if (failure == "diagnostics") target = target with { FrontMatter = target.FrontMatter with { Diagnostics = ["unrecognized pin metadata"] } };
        if (failure == "partial") inventory = inventory with { ConfiguredRootsComplete = false };
        Assert.Throws<IOException>(() => Store.CaptureCustom(target, inventory, Fingerprint, Token));
        Assert.False(Directory.Exists(Store.Root));
    }

    [Fact]
    public void NullHeaderProjectionIsUnknownAndNeverAnApprovalAuthority()
    {
        var id = Guid.NewGuid().ToString("N");
        using (var state = NativeStateDirectory.OpenOrCreate(Store.Root, [Skills], Token))
        {
            using var writer = state.OpenFile("writer.lock", true, Token);
            using var capture = state.CreateStagingDirectory(id, Token);
            using var file = capture.OpenFile("approved.json", false, Token);
            file.Write(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"id\":\"" + id + "\",\"createdAt\":\"2026-10-17T00:00:00Z\",\"kind\":\"captured-custom\",\"contextFingerprint\":null,\"binding\":null,\"subject\":null,\"captured\":null,\"archiveFingerprint\":null,\"metadataPolicy\":null,\"metadata\":[],\"sourceReviewContract\":null,\"fingerprint\":null}"));
            file.Flush(true);
        }
        var observed = Store.Inventory([Skills], Token);
        Assert.Empty(observed.Captures); Assert.NotNull(observed.Diagnostic);
        Assert.Throws<IOException>(() => Store.Open(new(Store.Root, id, new('a', 64)), [Skills], Token));
    }

    [Fact]
    public void ValidCaptureNameCannotTurnARegularFileIntoNativeDirectoryAuthority()
    {
        using (var state = NativeStateDirectory.OpenOrCreate(Store.Root, [Skills], Token))
        {
            using var writer = state.OpenFile("writer.lock", true, Token);
            using var wrong = state.OpenFile(Guid.NewGuid().ToString("N"), false, Token);
            wrong.WriteByte(1); wrong.Flush(true);
        }
        Assert.ThrowsAny<IOException>(() => Store.CaptureCustom(Target, Inventory(), Fingerprint, Token));
        Assert.Empty(Directory.EnumerateDirectories(Store.Root));
        Assert.NotNull(Store.Inventory([Skills], Token).Diagnostic);
    }

    [Fact]
    public void WrongReviewedPayloadStaysPartialAndCannotAuthorizeIntent()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        var error = Assert.Throws<IOException>(() => Store.CaptureCustom(Target, Inventory(), new('f', 64), Token));
        Assert.Contains("independently reviewed", error.Message);
        Assert.Single(Directory.EnumerateDirectories(Store.Root));
        Assert.Empty(Directory.EnumerateFiles(Store.Root, "approved.json", SearchOption.AllDirectories));
        var projection = Store.Inventory([Skills], Token);
        Assert.Empty(projection.Captures); Assert.Contains("Incomplete", projection.Diagnostic!);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Fact]
    public void EveryPartialOrUnreferencedAttemptReservesTheFullIndependentQuota()
    {
        var before = PayloadSnapshot.Capture(Payload, Token);
        for (var index = 0; index < ApprovedPayloadStore.MaxCaptures; index++)
            Assert.Throws<IOException>(() => Store.CaptureCustom(Target, Inventory(), new('f', 64), Token));
        var error = Assert.Throws<IOException>(() => Store.CaptureCustom(Target, Inventory(), before.Fingerprint, Token));
        Assert.Contains("quota is reserved", error.Message);
        Assert.Equal(ApprovedPayloadStore.MaxCaptures, Directory.EnumerateDirectories(Store.Root).Count());
        Assert.False(Directory.Exists(ReplacementRoot));
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Fact]
    public void ReplacementPlanQuotaDoesNotBecomeApprovalRetentionQuota()
    {
        using (var state = NativeStateDirectory.OpenOrCreate(ReplacementRoot, [Skills], Token))
        {
            using var writer = state.OpenFile("writer.lock", true, Token);
            for (var index = 0; index < 16; index++)
            { using var retained = state.CreateStagingDirectory(Guid.NewGuid().ToString("N"), Token); retained.FlushPublication(Token); }
        }
        var before = Directory.EnumerateDirectories(ReplacementRoot).Order(StringComparer.Ordinal).ToArray();
        try { _ = Store.CaptureCustom(Target, Inventory(), Fingerprint, Token); }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal)) { }
        Assert.Single(Directory.EnumerateDirectories(Store.Root));
        Assert.Equal(before, Directory.EnumerateDirectories(ReplacementRoot).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void OwnedObservationRetainsActualRegistryObjectParentAndRawEntryWithoutClaimingApproval()
    {
        var skills = Directory.CreateDirectory(Path.Combine(Home, ".agents", "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "demo")).FullName;
        var tree = new string('a', 40); var repository = "https://github.com/fixture/approved";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: owned capture\nmetadata:\n  github-repo: " + repository
            + "\n  github-ref: refs/heads/main\n  github-path: ''\n  github-tree-sha: " + tree + "\n---\nCustom installed I, not pristine B/C.\n");
        var registry = Path.Combine(Home, ".agents", ".skill-lock.json");
        File.WriteAllText(registry, System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 3,
            skills = new Dictionary<string, object>
            {
                ["demo"] = new
                {
                    source = "fixture/approved",
                    sourceType = "github",
                    sourceUrl = repository,
                    skillPath = "SKILL.md",
                    skillFolderHash = tree,
                    installedAt = "2020-01-01T00:00:00Z",
                    updatedAt = "2020-01-01T00:00:00Z",
                    retainedExtension = true
                }
            }
        }));
        var beforeRegistry = File.ReadAllBytes(registry);
        var diagnostics = new List<LockReadDiagnostic>();
        var claims = new SkillLockFileReader(logger).LoadObservations([skills], Token, new(Home, Path.Combine(Home, "state")), diagnostics);
        var inventory = InventorySnapshot.Empty with
        {
            Skills = LocalInventoryService.EnrichOwnership(new LocalSkillScanner(logger).Scan([new(skills, Scope.User, "universal")]).ToImmutableArray(), claims, Token),
            OwnershipRecords = claims.Select(claim => claim.Package).ToImmutableArray(),
            OwnershipReadDiagnostics = diagnostics.ToImmutableArray(),
            ConfiguredRoots = [new(skills, Scope.User, "universal")],
            ScannedRoots = [new(skills, Scope.User, "universal")]
        };
        var target = Assert.Single(inventory.Skills); Assert.NotEmpty(target.AllOwnershipClaims);
        var statePath = Path.Combine(Home, "observation-only");
        ApprovedCaptureSubject observed;
        using (var state = NativeStateDirectory.OpenOrCreate(statePath, [skills], Token))
        {
            using var writer = state.OpenFile("writer.lock", true, Token);
            observed = Plans.CaptureApprovedPayload(target, inventory, state, PayloadSnapshot.Capture(payload, Token).Fingerprint, null, null, Token);
        }
        Assert.Null(observed.Selected); var owner = Assert.IsType<ApprovedOwnerCapture>(observed.Owner);
        using (var held = new GhRegistryWriteSession(registry, "demo", logger, Token))
        {
            var actual = held.Read(Token);
            Assert.Equal(actual.Identity, owner.Identity); Assert.Equal(held.ParentIdentity, owner.ParentIdentity);
            Assert.Equal(GhRegistryEditor.CaptureEntry(actual.Bytes, "demo", Token).Bytes, owner.Entry);
        }
        Assert.Equal(beforeRegistry, File.ReadAllBytes(registry));
        Assert.Empty(Directory.EnumerateFiles(statePath, "approved.json", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Store.Root));
    }

    [Fact]
    public void MutableApprovalStringsCannotPublishPolicyOrClearActualPins()
    {
        var target = Target; var policies = DesiredRevisionPolicyStore.For(runner);
        var before = policies.Observe(target, [Skills], Token);
        using var writer = policies.Acquire([Skills], Token);
        var error = Assert.Throws<IOException>(() => writer.Set(target, new("approved-immutable", ApprovalId: Guid.NewGuid().ToString("N"), ApprovalFingerprint: new('a', 64)), before.Fingerprint!, Token));
        Assert.Contains("held verified completed approval", error.Message);
        Assert.Equal("absent", writer.Observe(target, Token).Status);
        Assert.False(Directory.Exists(Store.Root));
    }

    [Fact]
    public void CustomCaptureDoesNotSetIntentOrInventIndependentSourceEvidence()
    {
        var reference = CaptureOrSkip();
        using (var held = Store.Open(reference, [Skills], Token))
        {
            Assert.Equal("captured-custom", held.Header.Kind); Assert.Null(held.Header.Selected);
            Assert.Equal(Fingerprint, held.Header.Captured.Payload);
        }
        Assert.Equal("absent", DesiredRevisionPolicyStore.For(runner).Observe(Target, [Skills], Token).Status);
        var projection = Store.Inventory([Skills], Token);
        Assert.Single(projection.Captures); Assert.Contains("unproved", projection.Captures[0].Provenance);
        Assert.False(Directory.Exists(ReplacementRoot));
    }

    [Fact]
    public void SourceCanDisappearBeforeCustomRestoreAndExactNativeUndo()
    {
        var reference = CaptureOrSkip(); var approved = PayloadSnapshot.Capture(Payload, Token);
        Directory.Delete(Source, true);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), File.ReadAllText(Path.Combine(Payload, "SKILL.md")).Replace("Intentionally customized", "Later installed"));
        File.Delete(Path.Combine(Payload, "extra.sh")); File.WriteAllText(Path.Combine(Payload, "not-approved"), "must disappear from active payload\n");
        var inventory = Inventory(); var target = Assert.Single(inventory.Skills); var before = PayloadSnapshot.Capture(Payload, Token);
        var policy = DesiredRevisionPolicyStore.For(runner).Observe(target, [Skills], Token);
        var active = Store.SetPolicy(reference, target, inventory, policy.Fingerprint!, true, Token);
        Assert.Equal("approved-immutable", active.Policy!.Mode);
        var plan = Plans.PlanApproved(reference, target, inventory, Token);
        Assert.Null(plan.Revision); Assert.NotNull(plan.Approval); Assert.Null(plan.Approval.Header.Selected);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "recorded-base")));
        Assert.Throws<IOException>(() => Plans.Apply(plan.Id, plan.PlanFingerprint, inventory, Token));
        Assert.True(Plans.Apply(plan.Id, plan.PlanFingerprint, inventory, Token, ReplacementPlanStore.ApprovedRestoreDisposition, ReplacementPlanStore.ApprovedRestorePurpose).Succeeded);
        Assert.Equal(approved.Fingerprint, PayloadSnapshot.Capture(Payload, Token).Fingerprint);
        Assert.True(File.Exists(Path.Combine(Payload, "extra.sh"))); Assert.False(File.Exists(Path.Combine(Payload, "not-approved")));
        Directory.Move(Store.Root, Store.Root + "-unavailable-after-start");
        var observed = Plans.Inspect(plan.Id, Inventory(), Token, ReplacementPlanStore.ApprovedRestorePurpose);
        Assert.True(Plans.Recover(plan.Id, observed.ReviewFingerprint, Inventory(), true, Token, ReplacementPlanStore.ApprovedRestorePurpose).Succeeded);
        var restored = PayloadSnapshot.Capture(Payload, Token);
        Assert.Equal(before.RootIdentity, restored.RootIdentity); Assert.Equal(before.ObjectStateFingerprint, restored.ObjectStateFingerprint);
        Assert.Equal(before.Fingerprint, restored.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualSchema8RequiresItsPolicyReceiptAtFirstApplyOrRecovery(bool recover)
    {
        var reference = CaptureOrSkip(); var inventory = Inventory();
        var plan = Plans.PlanApproved(reference, Assert.Single(inventory.Skills), inventory, Token);
        var before = PayloadSnapshot.Capture(Payload, Token);
        File.Delete(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "desired-policy.json"));
        var inspected = Plans.Inspect(plan.Id, inventory, Token, ReplacementPlanStore.ApprovedRestorePurpose);
        var error = recover
            ? Assert.Throws<IOException>(() => Plans.Recover(plan.Id, inspected.ReviewFingerprint, inventory, false, Token, ReplacementPlanStore.ApprovedRestorePurpose))
            : Assert.Throws<IOException>(() => Plans.Apply(plan.Id, plan.PlanFingerprint, inventory, Token, ReplacementPlanStore.ApprovedRestoreDisposition, ReplacementPlanStore.ApprovedRestorePurpose));
        Assert.Contains("exact saved desired-policy receipt", error.Message);
        Assert.False(Directory.Exists(plan.RetainedPath)); Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(Payload, Token).ObjectStateFingerprint);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("blob")]
    public void CorruptRetainedEvidenceCannotPublishPolicy(string corrupt)
    {
        var reference = CaptureOrSkip(); var path = Path.Combine(Store.Root, reference.Id, "payload");
        if (corrupt == "blob") File.WriteAllText(Directory.EnumerateFiles(path, "entry-*").First(), "corrupted");
        else reference = reference with { Fingerprint = new('f', 64) };
        Assert.Throws<IOException>(() => Store.Open(reference, [Skills], Token));
        Assert.Equal("absent", DesiredRevisionPolicyStore.For(runner).Observe(Target, [Skills], Token).Status);
    }

    public void Dispose() => temp.Delete(true);
}
