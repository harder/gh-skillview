using System.Collections.Immutable;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class SkillIdentityTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-identities-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Logger logger = new();
    public void Dispose() => temporary.Delete(true);
    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(temporary.FullName, relative)).FullName;
    private InstalledSkill Skill(string relative, SkillFrontMatter? metadata = null, params AgentMembership[] agents)
    {
        var path = Folder(relative);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\ndescription: Fixture\n---\nInstructions\n");
        return new()
        {
            Name = "demo",
            ResolvedPath = path,
            ScanRoot = Path.GetDirectoryName(path)!,
            Scope = Scope.User,
            FrontMatter = metadata ?? SkillFrontMatter.Empty,
            Agents = agents.ToImmutableArray(),
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            OwnershipRunner = Runner()
        };
    }
    private ProcessRunner Runner() => new(logger, environment: new Dictionary<string, string>
    {
        ["HOME"] = temporary.FullName,
        ["USERPROFILE"] = temporary.FullName,
        ["CODEX_HOME"] = Path.Combine(temporary.FullName, ".codex"),
        ["CLAUDE_CONFIG_DIR"] = Path.Combine(temporary.FullName, ".claude"),
        ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(temporary.FullName, ".claude", "plugins"),
        ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
        ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
        ["PI_CODING_AGENT_DIR"] = Path.Combine(temporary.FullName, ".pi", "agent")
    }, defaultWorkingDirectory: temporary.FullName);
    private InstalledSkill Observe(InstalledSkill skill) => skill with
    { ObservedSubject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, Token) };
    private InventorySnapshot Snapshot(params InstalledSkill[] skills) => InventorySnapshot.Empty with
    { Skills = skills.ToImmutableArray(), ScannedRoots = [new(temporary.FullName, Scope.Custom, null)], CapturedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void FourSameNameCopiesRetainIndependentSourceRevisionOwnersAndAddress()
    {
        var metadata = new SkillFrontMatter { Upstream = "https://github.com/fixture/skills", GithubPath = "skills/demo", GithubRef = "main", GithubTreeSha = "tree" };
        var shared = Skill("user/demo", metadata);
        shared = Observe(shared with { Agents = [new("universal", shared.ResolvedPath, false, Scope.User, Profile: "shared")] });
        var pinned = Observe(Skill("project/demo", metadata with { Pinned = true, PinnedRef = "v1", GithubRef = "v1" }) with { Scope = Scope.Project });
        var fork = Observe(Skill("fork/demo", new() { LocalPath = Folder("fork-source") }));
        File.WriteAllText(Path.Combine(fork.ResolvedPath, "local-edit"), "local edits stay local");
        var plugin = Skill("plugin/v1/skills/demo");
        var owner = new SkillPackage("demo@market", "unknown", null, null, null)
        { Provider = "codex-plugin", PluginName = "demo@market", ReadOnly = true, PackageVersion = "1", InstallationRoot = Path.GetDirectoryName(plugin.ResolvedPath) };
        plugin = Observe(plugin with { Package = owner });
        var subjects = new[] { shared, pinned, fork, plugin }.Select(OperationSubject.From).ToArray();
        Assert.Equal(4, subjects.Select(subject => subject.InstallationId).Distinct().Count());
        Assert.Equal(subjects[0].Source.Key, subjects[1].Source.Key);
        Assert.Equal("v1", subjects[1].Revision.PinnedRef);
        Assert.Equal("local", subjects[2].Source.Provider);
        Assert.Null(subjects[3].Source.Key);
        Assert.Equal("demo@market", Assert.Single(subjects[3].Distributors).Locator);
        Assert.True(Assert.Single(subjects[3].Owners).ReadOnly);
        Assert.Equal("shared", Assert.Single(subjects[0].Exposures).Membership.Profile);
        Assert.All(subjects, subject => Assert.Null(subject.Revision.PayloadFingerprint));
        Assert.Single(plugin.PhysicalInstallation.OwnerRecords); // Legacy Package fallback survives.
        using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(Snapshot(shared, pinned, fork, plugin)));
        Assert.Equal(4, json.RootElement.GetProperty("skills").GetArrayLength());
        Assert.All(json.RootElement.GetProperty("skills").EnumerateArray(), row =>
            Assert.Equal(JsonValueKind.Null, row.GetProperty("subject").GetProperty("revision").GetProperty("resolvedCommit").ValueKind));
    }

    [Fact]
    public void VersionsShareLogicalDistributorButNotDeliveryAndDifferentMarketsStayDistinct()
    {
        SkillPackage Owner(string market, string version) => new("demo@" + market, "unknown", null, null, null)
        {
            Provider = "codex-plugin",
            PluginName = "demo@" + market,
            ReadOnly = true,
            PackageVersion = version,
            InstallationRoot = Path.Combine(temporary.FullName, market, version, "skills")
        };
        var first = SkillDistributorIdentity.From(Owner("one", "1"));
        var second = SkillDistributorIdentity.From(Owner("one", "2"));
        var other = SkillDistributorIdentity.From(Owner("two", "1"));
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.DeliveryId, second.DeliveryId);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal("observed-distributor-label", first.Evidence);
    }

    [Fact]
    public void SamePluginLabelFromDifferentRepositoriesDoesNotJoinPackageRemoval()
    {
        InstalledSkill Owned(string path, string source)
        {
            var skill = Skill(path);
            var owner = new SkillPackage(source, "github", null, null, null)
            { Provider = "codex-plugin", PluginName = "tools", ReadOnly = true, InstallationRoot = skill.ScanRoot };
            return skill with { Package = owner };
        }
        var first = Owned("repositories/first", "vendor-one/skills");
        var sibling = Owned("repositories/sibling", "Vendor-One/Skills");
        var other = Owned("repositories/other", "vendor-two/skills");
        Assert.Equal(SkillDistributorIdentity.From(first.Package!).Id, SkillDistributorIdentity.From(sibling.Package!).Id);
        Assert.NotEqual(SkillDistributorIdentity.From(first.Package!).Id, SkillDistributorIdentity.From(other.Package!).Id);
        var group = Assert.Single(RemoveTargetResolver.BuildTargets(first, Snapshot(first, sibling, other)), target => target.Kind == RemoveTargetKind.PackageGroup);
        Assert.Equal(2, group.Skills.Length); Assert.DoesNotContain(other, group.Skills);
        var changedUrl = first.Package! with { SourceUrl = "https://github.com/vendor-two/skills" };
        Assert.NotEqual(SkillDistributorIdentity.From(first.Package!).Id, SkillDistributorIdentity.From(changedUrl).Id);
    }

    [Fact]
    public void WindowsReadableNonDeletableDirectoryRetainsNativeObservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var skill = Skill("acl/demo");
        var payload = new DirectoryInfo(skill.ResolvedPath);
        var parent = payload.Parent!;
        var originalPayload = payload.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var originalParent = parent.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var sourceFile = new FileInfo(Path.Combine(payload.FullName, "SKILL.md"));
        var originalFile = sourceFile.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var originalBytes = File.ReadAllBytes(sourceFile.FullName);
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User!;
        var payloadAcl = new DirectorySecurity();
        payloadAcl.SetAccessRuleProtection(true, false);
        payloadAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        payloadAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Delete, AccessControlType.Deny));
        var parentAcl = parent.GetAccessControl();
        parentAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
        try
        {
            parent.SetAccessControl(parentAcl); payload.SetAccessControl(payloadAcl);
            var reviewedAcl = payload.GetAccessControl().GetSecurityDescriptorBinaryForm();
            Assert.False(SecureRemovalBackend.TryCaptureIdentity(payload.FullName, out _, out _));
            Assert.True(SecureRemovalBackend.TryObserveIdentity(payload.FullName, out var native, out var error), error);
            var subject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, Token);
            Assert.Equal("observed", subject.NativeObject.Status);
            Assert.Equal(native.FileIdHigh.ToString("x16") + native.FileIdLow.ToString("x16"), subject.NativeObject.ObjectId);
            Assert.Equal(native.WindowsCreationTime.ToString(System.Globalization.CultureInfo.InvariantCulture), subject.NativeObject.WindowsCreationTime);
            Assert.Equal(native.WindowsChangeTime.ToString(System.Globalization.CultureInfo.InvariantCulture), subject.NativeObject.WindowsChangeTime);
            Assert.Equal(reviewedAcl, payload.GetAccessControl().GetSecurityDescriptorBinaryForm());
            Assert.Equal(originalBytes, File.ReadAllBytes(sourceFile.FullName));
        }
        finally
        {
            var restoreParent = new DirectorySecurity();
            restoreParent.SetSecurityDescriptorBinaryForm(originalParent, AccessControlSections.Access);
            parent.SetAccessControl(restoreParent);
            var restorePayload = new DirectorySecurity();
            restorePayload.SetSecurityDescriptorBinaryForm(originalPayload, AccessControlSections.Access);
            payload.SetAccessControl(restorePayload);
            var restoreFile = new FileSecurity();
            restoreFile.SetSecurityDescriptorBinaryForm(originalFile, AccessControlSections.Access);
            sourceFile.SetAccessControl(restoreFile);
        }
    }

    [Fact]
    public void DeliveryIdentityLengthPrefixesEveryCoveredRoot()
    {
        var owner = new SkillPackage("fixture/package", "unknown", null, null, null)
        { CoveredRoots = ["/a\n/b", "/c"] };
        var other = owner with { CoveredRoots = ["/a", "/b\n/c"] };
        Assert.Equal(SkillDistributorIdentity.From(owner).Id, SkillDistributorIdentity.From(other).Id);
        Assert.NotEqual(SkillDistributorIdentity.From(owner).DeliveryId, SkillDistributorIdentity.From(other).DeliveryId);
    }

    [Fact]
    public void LiveProjectionPreservesConflictedSourceEvidence()
    {
        var skill = Skill("conflict/demo", new() { Upstream = "https://github.com/fixture/skills", GithubPath = "demo" })
            with
        { MetadataConflicts = ["Conflicting source observations"] };
        Assert.True(OperationSubject.From(skill).Source.Conflicted);
        var observed = Observe(skill);
        Assert.Equal(skill.SourceIdentity, observed.ObservedSubject!.Source);
        Assert.Null(observed.ObservedSubject.Source.Key);
    }

    [Fact]
    public void ConflictingOwnersDoNotChooseAnArbitrarySourceOrRevision()
    {
        var skill = Skill("owners/demo");
        var one = new SkillPackage("one/repo", "github", null, null, null) { PackageVersion = "1", RepositoryRef = "one" };
        var two = one with { Source = "two/repo", PackageVersion = "2", RepositoryRef = "two" };
        skill = Observe(skill with { Package = one, OwnershipClaims = [one, two] });
        var subject = OperationSubject.From(skill);
        Assert.True(subject.Source.Conflicted); Assert.Null(subject.Source.Key);
        Assert.Null(subject.Revision.DistributorVersion); Assert.Null(subject.Revision.RepositoryRef);
        Assert.Equal(2, subject.Distributors.Length); Assert.Equal(2, subject.Owners.Length);
    }

    [Fact]
    public void SameDistributorVersionRetainsIndependentUnregisteredContexts()
    {
        var skill = Skill("managed/demo");
        var first = new ManagedSkillRegion(skill.ScanRoot, "codex-plugin", "codex", "plugin", "demo@market", "1", "context-one");
        var second = first with { Context = "context-two" };
        var discovery = new ManagedSkillDiscovery.Observation([first, second], [], [], [], ImmutableDictionary<string, string>.Empty, false);
        var claims = discovery.Claims(skill).ToImmutableArray();
        Assert.Equal(2, claims.Length);
        skill = Observe(skill with { Package = claims[0], OwnershipClaims = claims });
        var subject = OperationSubject.From(skill);
        Assert.Equal(2, subject.Distributors.Length);
        Assert.Single(subject.Distributors.Select(distributor => distributor.Id).Distinct());
        Assert.Equal(2, subject.Distributors.Select(distributor => distributor.DeliveryId).Distinct().Count());
        Assert.Equal(["context-one", "context-two"], subject.Distributors.Select(distributor => distributor.Context));
    }

    [Fact]
    public async Task UpdatePinnedAndRefusedOutcomesKeepEveryStructuredExposure()
    {
        var skill = Skill("updates/demo", new() { Pinned = true });
        skill = Observe(skill with
        {
            Agents = [new("codex", skill.ResolvedPath, false, Scope.User, Context: "context-one", Profile: "one"),
            new("codex", skill.ResolvedPath, false, Scope.Project, Context: "context-two", Profile: "two")]
        });
        var runner = Runner();
        var install = new GhSkillInstallService(runner, logger);
        var update = new GhSkillUpdateService(runner, logger);
        var adapter = new GhSkillListAdapter(runner, logger);
        var coordinator = new SkillUpdateCoordinator(install, update, adapter, logger, runner);
        var result = await coordinator.UpdateAsync("unused-no-process", new(Targets: [skill], DryRun: true), Token);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("pinned", outcome.Status); Assert.Same(skill.ObservedSubject, outcome.Impact!.Subject);
        Assert.Equal(2, outcome.Impact.AffectedExposures.Length);
        var saved = new SavedUpdateCoordinator(new(Path.Combine(temporary.FullName, "state"), logger, runner: runner), coordinator, adapter, logger);
        var refusal = await saved.PrepareAsync("unused-no-process", new(Targets: [skill]),
            Snapshot(skill) with { ConfiguredRootsComplete = false }, Token);
        Assert.Equal("refused", Assert.Single(refusal.Outcomes).Status);
        Assert.Same(skill.ObservedSubject, refusal.Outcomes[0].Impact!.Subject);
        using var json = JsonDocument.Parse(CliDispatcher.RenderUpdateJson(result, new([], false, true, false, false, true), [], []));
        var affected = json.RootElement.GetProperty("outcomes")[0].GetProperty("impact").GetProperty("affectedExposures");
        Assert.Equal("context-two", affected[1].GetProperty("context").GetString());
        Assert.Equal("two", affected[1].GetProperty("profile").GetString());
    }

    [Fact]
    public void SharedLinkImpactIncludesEveryContextOfOneEntryAndRetainsOtherLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var skill = Skill("payload/demo");
        var links = Folder("links");
        var alias = Path.Combine(temporary.FullName, "links-alias");
        Directory.CreateSymbolicLink(alias, links);
        var first = Path.Combine(links, "first");
        var second = Path.Combine(links, "second");
        Directory.CreateSymbolicLink(first, skill.ResolvedPath);
        Directory.CreateSymbolicLink(second, skill.ResolvedPath);
        skill = Observe(skill with
        {
            Agents = [new("codex", first, true, Scope.User, Profile: "one", Context: "workspace-one"),
            new("codex", Path.Combine(alias, "first"), true, Scope.Project, Profile: "two", Context: "workspace-two"),
            new("claude", second, true, Scope.User), new("universal", skill.ResolvedPath, false, Scope.User)]
        });
        var subject = OperationSubject.From(skill);
        var impact = OperationImpact.Link(subject, skill.Agents[0]);
        Assert.True(impact.PayloadRetained); Assert.True(impact.ExposureCoverageComplete);
        Assert.Equal(2, impact.AffectedExposures.Length); Assert.Equal(2, impact.RetainedExposures.Length);
        Assert.Equal(["one", "two"], impact.AffectedExposures.Select(exposure => exposure.Membership.Profile));
        Assert.Equal(4, OperationImpact.Payload(subject, "remove").AffectedExposures.Length);
        var targets = RemoveTargetResolver.BuildTargets(skill, Snapshot(skill));
        var evaluation = RemoveTargetResolver.Evaluate(targets.First(target => target.Kind == RemoveTargetKind.AgentSymlink), Snapshot(skill));
        Assert.Equal(2, evaluation.Items[0].Validation.Impact!.AffectedExposures.Length);
        var review = RemoveWizardContent.BuildReviewMarkdown(evaluation);
        Assert.Contains("workspace-one", review); Assert.Contains("workspace-two", review); Assert.Contains("Retained payload", review);
    }

    [Fact]
    public void RecreatedDirectoryKeepsAddressButChangesNativeEvidenceAndRefusesStaleRemoval()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var skill = Observe(Skill("replace/demo"));
        var validation = RemoveValidator.Validate(skill, Snapshot(skill).ScannedRoots, [skill], Token);
        Assert.True(validation.Allowed, string.Join(";", validation.Errors));
        Directory.Move(skill.ResolvedPath, skill.ResolvedPath + "-retained");
        var replacement = Observe(Skill("replace/demo"));
        Assert.Equal(skill.InstallationId, replacement.InstallationId);
        Assert.Equal("observed", skill.ObservedSubject!.NativeObject.Status);
        Assert.NotEqual(skill.ObservedSubject.NativeObject.ObjectId, replacement.ObservedSubject!.NativeObject.ObjectId);
        var result = new RemoveService(logger).Remove(validation, cancellationToken: Token);
        Assert.False(result.Succeeded); Assert.True(File.Exists(Path.Combine(replacement.ResolvedPath, "SKILL.md")));
        Assert.Same(validation.Impact, result.Impact);
        Assert.Equal(skill.ObservedSubject.NativeObject, result.Impact!.Subject.NativeObject);
    }

    [Fact]
    public async Task CanceledRemoveReturnsFrozenSubjectWithoutOpeningReplacement()
    {
        var skill = Observe(Skill("canceled/demo"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Directory.Move(skill.ResolvedPath, skill.ResolvedPath + "-retained");
        var (validation, report) = await CliDispatcher.RemoveTargetAsync(skill, Snapshot(skill), true, new(logger), canceled.Token);
        Assert.True(report.IsCanceled); Assert.Same(skill.ObservedSubject, report.Impact!.Subject);
        Assert.Equal(0, report.FilesDeleted); Assert.True(validation.Allowed);
        using var json = JsonDocument.Parse(CliDispatcher.RenderRemoveJson(report, skill, new(null, null, true, true), validation));
        Assert.True(json.RootElement.GetProperty("canceled").GetBoolean());
        Assert.Equal(skill.InstallationId, json.RootElement.GetProperty("impact").GetProperty("subject").GetProperty("installationId").GetString());
    }

    [Fact]
    public async Task PartialCancellationPreservesNativeTotalsAndStructuredSubject()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var skill = Observe(Skill("partial/demo"));
        for (var index = 0; index < 5; index++) File.WriteAllText(Path.Combine(skill.ResolvedPath, index.ToString()), "fixture");
        // Re-observe and validate after populating the directory.
        skill = Observe(skill);
        var validation = RemoveValidator.Validate(skill, Snapshot(skill).ScannedRoots, [skill], Token);
        using var cancel = new CancellationTokenSource();
        var deleting = 0;
        var service = new RemoveService(logger, null, (_, _) => { if (++deleting == 2) cancel.Cancel(); });
        var report = await service.RemoveWithOutcomeAsync(validation, cancellationToken: cancel.Token);
        Assert.True(report.IsCanceled); Assert.Equal(1, report.FilesDeleted);
        Assert.Same(skill.ObservedSubject, report.Impact!.Subject);
        Assert.Equal(0, report.ErrorCount); Assert.True(Directory.Exists(skill.ResolvedPath));
    }

    [Fact]
    public async Task CleanupCancellationReturnsCompletedInterruptedAndNotStartedSubjects()
    {
        if (!SecureRemovalBackend.IsSupported) return;
        var first = Observe(Skill("cleanup/first"));
        var second = Observe(Skill("cleanup/second"));
        var third = Observe(Skill("cleanup/third"));
        CleanupClassifier.Candidate Candidate(InstalledSkill skill) => new(CleanupClassifier.CandidateKind.Malformed, skill.ResolvedPath, "fixture", skill)
        { Impact = OperationImpact.Payload(OperationSubject.From(skill), "cleanup") };
        using var cancel = new CancellationTokenSource();
        var canonicalSecond = OwnershipPath.CanonicalCandidate(second.ResolvedPath)!;
        var service = new RemoveService(logger, null, (path, _) => { if (PathIdentity.IsInside(path, canonicalSecond)) cancel.Cancel(); });
        var result = await CliDispatcher.ApplyCleanupAsync([Candidate(first), Candidate(second), Candidate(third)], Snapshot(first, second, third), service, Runner(), cancel.Token);
        Assert.Equal(3, result.Applied.Count);
        Assert.True(result.Applied[0].R.Succeeded, string.Join("; ", result.Applied[0].R.Errors));
        Assert.True(result.Applied[1].R.IsCanceled, string.Join("; ", result.Applied[1].R.Errors));
        Assert.True(result.Applied[2].R.NotAttempted);
        Assert.All(result.Applied, item => Assert.NotNull(item.R.Impact));
        Assert.Same(first.ObservedSubject, result.Applied[0].R.Impact!.Subject);
        Assert.False(Directory.Exists(first.ResolvedPath)); Assert.True(Directory.Exists(third.ResolvedPath));
    }

    [Fact]
    public void ExactRemoveSelectorsDisambiguateSameAgentAndNormalizeAliases()
    {
        var first = Skill("select/one"); first = first with { Agents = [new("claude-code", first.ResolvedPath, false)] };
        var second = Skill("select/two"); second = second with { Agents = [new("claude-code", second.ResolvedPath, false)] };
        var snapshot = Snapshot(first, second);
        Assert.Equal(2, CliDispatcher.SelectRemoveTargets(snapshot, new("demo", "claude", false, true)).Count);
        Assert.Same(second, Assert.Single(CliDispatcher.SelectRemoveTargets(snapshot,
            CliDispatcher.ParseRemoveArgs(["--install-id", second.InstallationId, "--agent", "claude"]))));
        Assert.Same(first, Assert.Single(CliDispatcher.SelectRemoveTargets(snapshot, CliDispatcher.ParseRemoveArgs(["--path=" + first.ResolvedPath]))));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--path")]
    [InlineData("--agent=")]
    [InlineData("--path=one", "--install-id=two")]
    [InlineData("demo", "other")]
    public void RemoveRejectsAmbiguousOrIncompleteArguments(params string[] args) => Assert.NotNull(CliDispatcher.ParseRemoveArgs(args).Error);

    [Fact]
    public void PackageGroupingDoesNotMixSameLabelFromDifferentDistributors()
    {
        InstalledSkill Owned(string path, string provider)
        {
            var skill = Skill(path);
            var owner = new SkillPackage("same-label", "unknown", null, null, null) { Provider = provider, InstallationRoot = skill.ScanRoot };
            return skill with { Package = owner };
        }
        var first = Owned("group/one", "codex-plugin"); var sibling = Owned("group/two", "codex-plugin");
        var other = Owned("group/other", "claude-plugin");
        var group = Assert.Single(RemoveTargetResolver.BuildTargets(first, Snapshot(first, sibling, other)), target => target.Kind == RemoveTargetKind.PackageGroup);
        Assert.Equal(2, group.Skills.Length); Assert.DoesNotContain(other, group.Skills);
    }
}
