using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Preferences;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SelectedRevisionPlanTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-selected-native-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string Commit = new('a', 40);
    private const string Repository = "https://github.com/example/selected";
    private const string Before = "---\nname: demo\ndescription: selected\nversion: 1\n---\nold authored bytes\n";
    private const string After = "---\nname: demo\ndescription: selected\nversion: 1\n---\nnew authored bytes\n";
    public void Dispose() => temp.Delete(true);

    [Theory]
    [InlineData("tag", true, false)]
    [InlineData("tag", false, false)]
    [InlineData("commit", true, false)]
    [InlineData("commit", false, false)]
    [InlineData("tag", true, true)]
    public async Task ExactNativeApplyAndUndoUseFrozenProviderCaptureWithoutLaterRequests(string kind, bool pin, bool shared)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required; Windows native provider materialization proof remains required."); return; }
        var fixture = Create(kind, "valid", shared);
        var registryBefore = fixture.Registry is null ? null : File.ReadAllBytes(fixture.Registry);
        var before = PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token);
        ReplacementPreparation prepared;
        try
        {
            prepared = await fixture.Store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory,
                new(kind, kind == "tag" ? "v1" : Commit, pin), Token);
        }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
        {
            Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token).ObjectStateFingerprint);
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "plan.json", SearchOption.AllDirectories));
            Assert.Skip("Native full-metadata publication refused on this Linux host; protocol capture and refusal tests run separately."); return;
        }
        var plan = Assert.IsType<ReplacementResult>(prepared.Plan);
        Assert.NotNull(plan.Selection); Assert.Equal(Commit, plan.Selection.Provider.Selection.Commit);
        Assert.Equal("planned", plan.Status); Assert.Contains("SKILL.md", plan.ChangedPaths);
        Assert.Equal("1", plan.Revision!.DesiredMetadata.Version);
        Assert.Equal(pin, plan.Revision.DesiredMetadata.Pinned);
        Assert.Equal(pin ? kind == "tag" ? "v1" : Commit : null, plan.Revision.DesiredMetadata.PinnedRef);
        if (shared) Assert.NotNull(plan.Revision.Owner);
        Assert.Throws<IOException>(() => fixture.Store.Inspect(plan.Id, fixture.Inventory, Token));
        var calls = File.ReadAllText(fixture.Marker);
        // The provider and even its executable may disappear after capture.
        File.Delete(fixture.Gh);
        var applied = fixture.Store.Apply(plan.Id, plan.PlanFingerprint, fixture.Inventory, Token,
            ReplacementPlanStore.SelectedRevisionDisposition, ReplacementPlanStore.SelectedRevisionPurpose);
        Assert.True(applied.Succeeded); Assert.Equal("installed", applied.Status);
        Assert.Contains("new authored bytes", File.ReadAllText(Path.Combine(fixture.Target.ResolvedPath, "SKILL.md")));
        Assert.Equal(calls, File.ReadAllText(fixture.Marker));
        if (shared)
        {
            using var entry = JsonDocument.Parse(GhRegistryEditor.CaptureEntry(File.ReadAllBytes(fixture.Registry!), "demo", Token).Bytes);
            Assert.Equal("v1", entry.RootElement.GetProperty("pinnedRef").GetString());
            Assert.True(entry.RootElement.GetProperty("selectedExtension").GetProperty("keep").GetBoolean());
        }
        var inspected = fixture.Store.Inspect(plan.Id, fixture.Inventory, Token, ReplacementPlanStore.SelectedRevisionPurpose);
        var undone = fixture.Store.Recover(plan.Id, inspected.ReviewFingerprint, fixture.Inventory, true, Token, ReplacementPlanStore.SelectedRevisionPurpose);
        Assert.True(undone.Succeeded);
        var restored = PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token);
        Assert.Equal(before.RootIdentity, restored.RootIdentity); Assert.Equal(before.ObjectStateFingerprint, restored.ObjectStateFingerprint);
        Assert.Equal(before.Fingerprint, restored.Fingerprint);
        if (shared) Assert.Equal(registryBefore, File.ReadAllBytes(fixture.Registry!));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("commit")]
    public async Task ProviderWitnessCapturesActualMetadataAndImmutableArchivesWithoutClaimingPublication(string kind)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create(kind, "valid"); var logger = new Logger();
        var home = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Target.ResolvedPath))!;
        var process = new ProcessRunner(logger, environment: new Dictionary<string, string> { ["HOME"] = home, ["USERPROFILE"] = home }, defaultWorkingDirectory: home);
        var resolver = new GhRevisionResolver(process, fixture.Gh);
        var selection = await resolver.ResolveSelectedAsync(Repository, "", new(kind, kind == "tag" ? "v1" : Commit, true), Token);
        using var preview = NativeStateDirectory.OpenOrCreate(Path.Combine(home, "protocol-capture"), [fixture.Target.ResolvedPath], Token);
        using var writer = preview.OpenFile("writer.lock", true, Token);
        ImmutableArray<RecordedGitEntry> git;
        using (var raw = preview.CreateStagingDirectory("source", Token))
        {
            git = await new GhRecordedTreeStager(process).CaptureIntoAsync(fixture.Gh, Repository, selection.Tree, raw, null, Token);
            raw.SealStagingDirectory(493, Token);
        }
        using (var raw = NativeDirectoryMoveLease.Open(preview.CanonicalPath, Path.Combine(preview.CanonicalPath, "source"), Token))
            _ = CapturedPayloadArchive.Capture(preview, "source-capture", raw, Token);
        using var sourceState = NativeStateDirectory.OpenOrCreate(Path.Combine(preview.CanonicalPath, "source-capture"), [], Token, allowCreate: false);
        var source = CapturedPayloadArchive.Load(sourceState, null, Token);
        var witness = await new GhPinnedRevisionStager(process, logger).StageAsync(fixture.Gh, preview, source.Archive, source.Archive.OpenReader(sourceState),
            source.Fingerprint, git, selection, "demo", Token);
        await resolver.VerifySelectedAsync(selection, Token);
        Assert.Equal(Commit, witness.Selection.Commit); Assert.Equal(selection.Tree, witness.Selection.Tree);
        Assert.Contains("fixture selected output", witness.StdOut); Assert.Contains("fixture diagnostics", witness.StdErr);
        using var outputState = NativeStateDirectory.OpenOrCreate(Path.Combine(preview.CanonicalPath, "selected-gh-capture"), [], Token, allowCreate: false);
        var output = CapturedPayloadArchive.Load(outputState, witness.OutputArchiveFingerprint, Token).Archive;
        Assert.Equal(kind == "tag" ? "refs/tags/v1" : "provider-observed-commit/" + Commit, output.Snapshot.FrontMatter!.GithubRef);
        Assert.Equal(kind == "tag" ? "v1" : Commit, output.Snapshot.FrontMatter.PinnedRef);
        Assert.Equal(fixture.Target.FrontMatter.Version, output.Snapshot.FrontMatter.Version);
        Assert.NotEqual(fixture.Target.TreeSha, output.Snapshot.FrontMatter.GithubTreeSha);
        using var bytes = new MemoryStream(); output.CopyEntry(outputState, "data.txt", bytes, Token);
        Assert.Equal("immutable data\n", Encoding.UTF8.GetString(bytes.ToArray()));
        using var skill = new MemoryStream(); output.CopyEntry(outputState, "SKILL.md", skill, Token);
        Assert.Contains("new authored bytes", Encoding.UTF8.GetString(skill.ToArray()));
        Assert.Contains("old authored bytes", File.ReadAllText(Path.Combine(fixture.Target.ResolvedPath, "SKILL.md")));
    }

    [Theory]
    [InlineData("wrong-tree")]
    [InlineData("wrong-pin")]
    [InlineData("extra-file")]
    [InlineData("changed-file")]
    [InlineData("moving-tag")]
    [InlineData("wrong-body")]
    [InlineData("wrong-description")]
    [InlineData("wrong-version")]
    [InlineData("wrong-authored-metadata")]
    [InlineData("wrong-skill-mode")]
    public async Task UnboundProviderEvidenceRefusesAndChargesPartialCapture(string failure)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create("tag", failure);
        var before = PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token);
        var refused = await Assert.ThrowsAsync<IOException>(() => fixture.Store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory, new("tag", "v1", true), Token));
        Assert.Contains(failure == "moving-tag" ? "moved during capture" : failure is "wrong-tree" or "wrong-pin" ? "inconsistent source"
            : failure is "extra-file" or "changed-file" ? "immutable source files" : "authored SKILL.md", refused.Message);
        Assert.Contains(File.ReadLines(fixture.Marker), line => JsonDocument.Parse(line).RootElement[0].GetString() == "skill");
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token).ObjectStateFingerprint);
        var children = Directory.EnumerateDirectories(fixture.Root + "-selected-previews").ToArray(); Assert.Single(children);
        var calls = File.ReadAllText(fixture.Marker);
        var second = await Assert.ThrowsAsync<IOException>(() => fixture.Store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory, new("tag", "v1", true), Token));
        Assert.Contains("reserved", second.Message); Assert.Equal(calls, File.ReadAllText(fixture.Marker));
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("invalid")]
    [InlineData("partial")]
    public async Task UnsafeOriginalRefusesBeforeProviderOrPreviewState(string failure)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create("tag", "valid");
        var target = failure == "invalid" ? fixture.Target with { Validity = ValidityState.InvalidMetadata }
            : failure == "pinned" ? fixture.Target with { FrontMatter = fixture.Target.FrontMatter with { Pinned = true, PinnedRef = "old" } } : fixture.Target;
        var inventory = failure == "partial" ? fixture.Inventory with { ConfiguredRootsComplete = false } : fixture.Inventory;
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.PlanSelectedAsync(fixture.Gh, target, inventory, new("tag", "v1", false), Token));
        Assert.False(File.Exists(fixture.Marker)); Assert.False(Directory.Exists(fixture.Root + "-selected-previews"));
    }

    [Fact]
    public async Task BinaryChangeDuringApiCaptureRefusesBeforeProviderMaterialization()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create("tag", "changing-binary");
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory, new("tag", "v1", true), Token));
        Assert.Contains("changed before selected materialization", error.Message);
        Assert.DoesNotContain(File.ReadLines(fixture.Marker), line => JsonDocument.Parse(line).RootElement[0].GetString() == "skill");
        Assert.Contains("old authored bytes", File.ReadAllText(Path.Combine(fixture.Target.ResolvedPath, "SKILL.md")));
    }

    [Fact]
    public async Task EntryContextSurvivesCallerDictionaryChangesAcrossApiAndMaterialization()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create("tag", "valid");
        var initial = fixture.Runner.CaptureContextFingerprint();
        var originalHome = fixture.Environment["HOME"]; var originalAuth = fixture.Environment["GH_CONFIG_DIR"];
        var originalCwd = fixture.Runner.EffectiveWorkingDirectory;
        Assert.True(SecureRemovalBackend.TryCanonicalizePath(originalCwd, out var canonicalCwd, out var canonicalError), canonicalError);
        var store = new ReplacementPlanStore(fixture.Root, new Logger(), point =>
        {
            if (point != "selected-context-captured") return;
            foreach (var key in new[] { "HOME", "USERPROFILE", "GH_CONFIG_DIR", "GH_TOKEN", "HTTPS_PROXY", "GIT_WORK_TREE" })
                fixture.Environment[key] = "changed-after-capture";
            fixture.Environment["SKILLVIEW_CONTEXT_LATE"] = "must-not-enter-child";
        }, fixture.Runner);
        ReplacementResult? saved = null;
        try { saved = (await store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory, new("tag", "v1", true), Token)).Plan; }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal)) { }
        Assert.NotEqual(initial, fixture.Runner.CaptureContextFingerprint());
        var contexts = File.ReadLines(fixture.ContextMarker).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(contexts, item => item.RootElement.GetProperty("args")[0].GetString() == "api");
            Assert.Contains(contexts, item => item.RootElement.GetProperty("args")[0].GetString() == "skill");
            foreach (var context in contexts)
            {
                var item = context.RootElement;
                Assert.Equal(originalAuth, item.GetProperty("auth").GetString()); Assert.Equal("fixture-initial-token", item.GetProperty("token").GetString());
                Assert.Equal("fixture-initial-proxy", item.GetProperty("proxy").GetString()); Assert.Equal("", item.GetProperty("late").GetString());
                if (item.GetProperty("args")[0].GetString() == "api")
                { Assert.Equal(originalHome, item.GetProperty("home").GetString()); Assert.Equal(canonicalCwd, item.GetProperty("cwd").GetString()); }
                else { Assert.NotEqual(originalHome, item.GetProperty("home").GetString()); Assert.Equal(item.GetProperty("home").GetString(), item.GetProperty("cwd").GetString()); }
            }
            if (saved is not null)
            {
                Assert.Equal(initial, saved.Selection!.Provider.ContextFingerprint);
                Assert.DoesNotContain("fixture-initial-token", File.ReadAllText(saved.PlanPath));
                Assert.Throws<IOException>(() => fixture.Store.Apply(saved.Id, saved.PlanFingerprint, fixture.Inventory, Token,
                    ReplacementPlanStore.SelectedRevisionDisposition, ReplacementPlanStore.SelectedRevisionPurpose));
            }
        }
        finally { foreach (var item in contexts) item.Dispose(); }
        Assert.Contains("old authored bytes", File.ReadAllText(Path.Combine(fixture.Target.ResolvedPath, "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSchema7PolicyReceiptRefusesFirstApplyAndFirstRecovery(bool recovery)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { Assert.Skip("POSIX injected Python protocol host required."); return; }
        var fixture = Create("tag", "valid"); var before = PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token);
        ReplacementResult plan;
        try { plan = (await fixture.Store.PlanSelectedAsync(fixture.Gh, fixture.Target, fixture.Inventory, new("tag", "v1", true), Token)).Plan!; }
        catch (IOException ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
        {
            Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token).ObjectStateFingerprint);
            Assert.Skip("Actual schema7 receipt-deletion/native admission proof requires complete metadata authority."); return;
        }
        File.Delete(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "desired-policy.json"));
        var observed = fixture.Store.Inspect(plan.Id, fixture.Inventory, Token, ReplacementPlanStore.SelectedRevisionPurpose);
        var error = recovery
            ? Assert.Throws<IOException>(() => fixture.Store.Recover(plan.Id, observed.ReviewFingerprint, fixture.Inventory, false, Token, ReplacementPlanStore.SelectedRevisionPurpose))
            : Assert.Throws<IOException>(() => fixture.Store.Apply(plan.Id, plan.PlanFingerprint, fixture.Inventory, Token,
                ReplacementPlanStore.SelectedRevisionDisposition, ReplacementPlanStore.SelectedRevisionPurpose));
        Assert.Contains("exact saved desired-policy receipt", error.Message);
        Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(fixture.Target.ResolvedPath, Token).ObjectStateFingerprint);
        Assert.False(Directory.Exists(plan.RetainedPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(plan.PlanPath)!, "step-*.json"));
    }

    [Fact]
    public void PrivateRemoteContextUsesCapturedAuthAndClearsInheritedGitRedirectionWithoutChangingParent()
    {
        var logger = new Logger(); var before = System.Environment.GetEnvironmentVariable("HOME");
        var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
        { ["HOME"] = "captured-home", ["USERPROFILE"] = "captured-home", ["GH_CONFIG_DIR"] = "captured-auth", ["GH_TOKEN"] = "fixture-secret", ["GIT_CONFIG_PARAMETERS"] = "hostile", ["HTTPS_PROXY"] = "captured-proxy" }, defaultWorkingDirectory: temp.FullName);
        var home = Path.Combine(temp.FullName, "private");
        var values = GhPinnedRevisionStager.PrivateEnvironment(runner, home, Path.Combine(temp.FullName, "gh"));
        Assert.Equal(Path.Combine(temp.FullName, "captured-auth"), values["GH_CONFIG_DIR"]);
        Assert.Equal("fixture-secret", values["GH_TOKEN"]); Assert.Equal("captured-proxy", values["HTTPS_PROXY"]);
        Assert.Equal("", values["GIT_CONFIG_PARAMETERS"]); Assert.Equal("0", values["GIT_CONFIG_COUNT"]);
        Assert.Equal(Path.Combine(home, "skillview-state"), values["SKILLVIEW_STATE_HOME"]);
        Assert.Equal(before, System.Environment.GetEnvironmentVariable("HOME"));
    }

    [Fact]
    public void PrivateGitContextUsesTheOperatingSystemsEnvironmentKeyComparison()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = temp.FullName,
            ["USERPROFILE"] = temp.FullName,
            ["git_dir"] = "hostile",
            ["Git_Work_Tree"] = "hostile",
            ["gIt_Common_Dir"] = "hostile",
            ["git_config_count"] = "99"
        };
        var home = Path.Combine(temp.FullName, "private-home");
        var result = GhPinnedRevisionStager.PrivateEnvironment(new(new Logger(), environment: values, defaultWorkingDirectory: temp.FullName), home, Path.Combine(temp.FullName, "gh"));
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_CONFIG_COUNT" })
        {
            var entries = result.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, entries.Length);
            Assert.Equal(key == "GIT_CONFIG_COUNT" ? "0" : key == "GIT_WORK_TREE" ? home : Path.Combine(home, "git-dir"), result[key]);
        }
    }

    [Theory]
    [InlineData("exact-pin", "v1", true, true)]
    [InlineData("exact-pin", "v2", true, false)]
    [InlineData("exact-pin", "v1", false, false)]
    [InlineData("manual-fork", "v1", true, false)]
    [InlineData("approved-immutable", "v1", true, false)]
    [InlineData("native-manager", "v1", true, false)]
    public void SelectedPolicyRequiresExactPinIntentAndCannotOverrideOtherProtectedModes(string mode, string selected, bool pin, bool allowed)
    {
        var fixture = Create("tag", "valid");
        var policy = new DesiredPolicyObservation("active", "fixture", new(mode, "v1"), new('a', 64), 1);
        void Admit() => DesiredRevisionPolicyGate.RequireAllowed(fixture.Target, policy, ReplacementPlanStore.SelectedRevisionPurpose,
            selection: new("tag", selected, pin));
        if (allowed) Admit(); else Assert.Throws<IOException>(Admit);
        Assert.False(File.Exists(fixture.Marker));
    }

    private Fixture Create(string kind, string failure, bool shared = false)
    {
        var home = Directory.CreateDirectory(Path.Combine(temp.FullName, "home")).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(temp.FullName, "cwd")).FullName;
        var skills = Directory.CreateDirectory(shared ? Path.Combine(home, ".agents", "skills") : Path.Combine(home, "skills")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(skills, "demo")).FullName;
        var marker = Path.Combine(temp.FullName, "calls.jsonl");
        var contextMarker = Path.Combine(temp.FullName, "contexts.jsonl");
        var beforeTree = Tree(Before); var afterTree = Tree(After);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), Tracking(Before, "refs/heads/main", beforeTree, null));
        File.WriteAllText(Path.Combine(target, "data.txt"), "immutable data\n");
        var registry = shared ? Path.Combine(home, ".agents", ".skill-lock.json") : null;
        if (registry is not null) File.WriteAllText(registry, JsonSerializer.Serialize(new
        {
            version = 3,
            extension = new { preserve = true },
            skills = new Dictionary<string, object>
            {
                ["demo"] = new
                {
                    source = "example/selected",
                    sourceType = "github",
                    sourceUrl = Repository,
                    skillPath = "SKILL.md",
                    skillFolderHash = beforeTree,
                    installedAt = "2020-01-01T00:00:00Z",
                    updatedAt = "2020-01-01T00:00:00Z",
                    selectedExtension = new { keep = true }
                },
                ["other"] = new { source = "other/repo", sourceType = "github", extension = new[] { 1, 2 } }
            }
        }));
        var pin = kind == "tag" ? "v1" : Commit;
        var output = Tracking(After, kind == "tag" ? "refs/tags/v1" : "provider-observed-commit/" + Commit,
            failure == "wrong-tree" ? new('e', 40) : afterTree, failure == "wrong-pin" ? "wrong" : pin);
        output = failure switch
        {
            "wrong-body" => output.Replace("new authored bytes", "altered provider body"),
            "wrong-description" => output.Replace("description: selected", "description: altered"),
            "wrong-version" => output.Replace("version: 1", "version: 9"),
            "wrong-authored-metadata" => output.Replace("  github-repo:", "  custom-author-policy: injected\n  github-repo:"),
            _ => output
        };
        var data = new
        {
            marker,
            contextMarker,
            failure,
            commit = Commit,
            beforeTree,
            afterTree,
            output,
            blobs = new Dictionary<string, string> { [Blob(Before)] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Before)), [Blob(After)] = Convert.ToBase64String(Encoding.UTF8.GetBytes(After)), [Blob("immutable data\n")] = Convert.ToBase64String(Encoding.UTF8.GetBytes("immutable data\n")) },
            trees = new Dictionary<string, object> { [beforeTree] = Listing(Before, beforeTree), [afterTree] = Listing(After, afterTree) }
        };
        var script = "#!/usr/bin/python3\nimport os,sys,json,pathlib\nd=json.loads(r'''" + JsonSerializer.Serialize(data) + "''')\n" + """
            a=sys.argv[1:]
            with open(d['marker'],'a') as log: log.write(json.dumps(a)+'\n')
            with open(d['contextMarker'],'a') as log:
                log.write(json.dumps(dict(args=a,cwd=os.getcwd(),home=os.environ.get('HOME',''),auth=os.environ.get('GH_CONFIG_DIR',''),token=os.environ.get('GH_TOKEN',''),proxy=os.environ.get('HTTPS_PROXY',''),late=os.environ.get('SKILLVIEW_CONTEXT_LATE','')))+'\n')
            if a==['--version']:
                print('gh version 2.102.0 (fixture)');sys.exit(0)
            if a[0]=='api':
                if d['failure']=='changing-binary':
                    with open(__file__,'a') as changed: changed.write('\n# changed fixture binary\n')
                e=a[-1]
                if '/git/ref/tags/' in e:
                    n=sum('/git/ref/tags/' in json.loads(line)[-1] for line in open(d['marker']))
                    body={'ref':'refs/tags/v1','object':{'sha':('c'*40 if d['failure']=='moving-tag' and n>1 else d['commit']),'type':'commit'}}
                elif '/git/commits/' in e: body={'sha':e.split('/')[-1],'tree':{'sha':d['afterTree']}}
                elif '/git/trees/' in e: body=d['trees'][e.split('/')[-1].split('?')[0]]
                elif '/git/blobs/' in e:
                    print(d['blobs'][e.split('/')[-1]]);sys.exit(0)
                else: raise RuntimeError('unexpected endpoint '+e)
                if '--include' in a: print('HTTP/2.0 200 OK\nContent-Type: application/json\n')
                print(json.dumps(body));sys.exit(0)
            assert a[:3]==['skill','install','--dir'] and a[5]=='--' and a[6]=='example/selected' and a[7]=='.'
            assert a[4].startswith('--pin=') and os.environ['GIT_CONFIG_COUNT']=='0'
            assert os.environ['TMPDIR']=='/var/tmp'
            dst=pathlib.Path(a[3])/'demo';dst.mkdir(mode=0o755)
            (dst/'SKILL.md').write_text(d['output'])
            (dst/'data.txt').write_text('changed\n' if d['failure']=='changed-file' else 'immutable data\n')
            if d['failure']=='extra-file': (dst/'unexpected').write_text('extra')
            for p in dst.iterdir(): p.chmod(0o644)
            if d['failure']=='wrong-skill-mode': (dst/'SKILL.md').chmod(0o755)
            print('fixture selected output');print('fixture diagnostics',file=sys.stderr)
            """;
        var gh = Path.Combine(temp.FullName, "fake-gh"); File.WriteAllText(gh, script);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(gh, (UnixFileMode)448);
        var logger = new Logger();
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "skillview-state"),
            ["TMPDIR"] = "/var/tmp",
            ["GIT_CONFIG_COUNT"] = "0",
            ["GH_CONFIG_DIR"] = Path.Combine(home, "captured-auth"),
            ["GH_TOKEN"] = "fixture-initial-token",
            ["HTTPS_PROXY"] = "fixture-initial-proxy",
            ["SKILLVIEW_CONTEXT_LATE"] = ""
        };
        foreach (System.Collections.DictionaryEntry item in System.Environment.GetEnvironmentVariables())
            if (item.Key is string key && key.StartsWith("GIT_", StringComparison.Ordinal)) environment[key] = "";
        environment["GIT_CONFIG_COUNT"] = "0";
        var runner = new ProcessRunner(logger, environment: environment, defaultWorkingDirectory: cwd);
        ScanRoot[] roots = [new(skills, shared ? Scope.User : Scope.Custom, null)];
        var diagnostics = new List<LockReadDiagnostic>();
        var claims = shared ? new SkillLockFileReader(logger).LoadObservations([skills], Token, new(home, Path.Combine(home, "state")), diagnostics) : [];
        var inventory = InventorySnapshot.Empty with
        {
            Skills = LocalInventoryService.EnrichOwnership(new LocalSkillScanner(logger).Scan(roots).ToImmutableArray(), claims, Token),
            OwnershipRecords = claims.Select(claim => claim.Package).ToImmutableArray(),
            OwnershipReadDiagnostics = diagnostics.ToImmutableArray(),
            ConfiguredRoots = roots.ToImmutableArray(),
            ScannedRoots = roots.ToImmutableArray()
        };
        var root = Path.Combine(home, "private", "plans");
        return new(root, gh, marker, Assert.Single(inventory.Skills), inventory, new(root, logger, runner: runner), registry, runner, environment, contextMarker);
    }

    private static string Tracking(string authored, string reference, string tree, string? pin) => authored.Replace("\n---\n", "\nmetadata:\n  github-repo: " + Repository + "\n  github-path: ''\n  github-ref: " + reference + "\n  github-tree-sha: " + tree + (pin is null ? "" : "\n  github-pinned: " + pin) + "\n---\n", StringComparison.Ordinal);
    private static string Blob(string value) => Git("blob", Encoding.UTF8.GetBytes(value));
    private static string Tree(string skill)
    {
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.UTF8.GetBytes("100644 SKILL.md\0")); bytes.Write(Convert.FromHexString(Blob(skill)));
        bytes.Write(Encoding.UTF8.GetBytes("100644 data.txt\0")); bytes.Write(Convert.FromHexString(Blob("immutable data\n")));
        return Git("tree", bytes.ToArray());
    }
    private static string Git(string kind, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes(kind + " " + content.Length + "\0");
        return Convert.ToHexStringLower(SHA1.HashData(header.Concat(content).ToArray()));
    }
    private static object Listing(string skill, string tree) => new
    {
        sha = tree,
        truncated = false,
        tree = new[]
    {
        new { path = "SKILL.md", type = "blob", mode = "100644", sha = Blob(skill), size = Encoding.UTF8.GetByteCount(skill) },
        new { path = "data.txt", type = "blob", mode = "100644", sha = Blob("immutable data\n"), size = Encoding.UTF8.GetByteCount("immutable data\n") }
    }
    };
    private sealed record Fixture(string Root, string Gh, string Marker, InstalledSkill Target, InventorySnapshot Inventory, ReplacementPlanStore Store, string? Registry,
        ProcessRunner Runner, Dictionary<string, string> Environment, string ContextMarker);
}
