using System.Text.Json;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Gh;

public sealed class UpdateAvailabilityReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNamesUseExactDirectoryAndCapturedChildContextWithoutCapturingPayload(bool aliasedContext)
    {
        if (OperatingSystem.IsWindows()) return; // Inert POSIX executable fixture.
        using var fixture = new Fixture(aliasedContext);
        var first = fixture.Skill("first");
        var second = fixture.Skill("second");
        // Full payload capture refuses this external link; upstream checking
        // needs only bounded tracking metadata, never payload archive authority.
        File.CreateSymbolicLink(Path.Combine(first.ResolvedPath, "external"), "/unobserved-external-payload");
        first = first with { ObservedSubject = SkillIdentityObserver.Capture(first, DateTimeOffset.UtcNow, true, TestContext.Current.CancellationToken) };
        second = second with { ObservedSubject = SkillIdentityObserver.Capture(second, DateTimeOffset.UtcNow, true, TestContext.Current.CancellationToken) };
        var before = File.ReadAllBytes(Path.Combine(first.ResolvedPath, "SKILL.md"));
        var registry = Path.Combine(fixture.Root, "home", ".agents", ".skill-lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, "inert owner bytes");
        // getcwd reports the native physical directory, including macOS /var
        // aliases. HOME remains the exact lexical value captured for the child.
        Assert.True(SecureRemovalBackend.TryCanonicalizePath(fixture.Root, out var expectedWorkingDirectory, out var contextError), contextError);
        if (aliasedContext) Assert.NotEqual(fixture.Root, expectedWorkingDirectory);
        var available = await fixture.Observe(first);
        var current = await fixture.Observe(second);
        Assert.Equal(UpdateAvailabilityStatus.Available, available.Status);
        Assert.Equal(UpdateAvailabilityStatus.NoUpdate, current.Status);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(first.ResolvedPath, "SKILL.md")));
        Assert.Equal("inert owner bytes", File.ReadAllText(registry));
        var commands = File.ReadAllLines(fixture.Log).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(4, commands.Length);
            for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
            {
                var root = commands[commandIndex].RootElement;
                Assert.Equal(Path.Combine(fixture.Root, "home"), root.GetProperty("home").GetString());
                Assert.Equal(expectedWorkingDirectory, root.GetProperty("cwd").GetString());
                var args = root.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal("skill", args[0]);
                Assert.Equal(commandIndex % 2 == 0 ? "list" : "update", args[1]);
                var directoryArgument = Array.IndexOf(args, "--dir");
                Assert.InRange(directoryArgument, 0, args.Length - 2);
                Assert.Equal(Path.GetDirectoryName(commandIndex < 2 ? first.ResolvedPath : second.ResolvedPath), args[directoryArgument + 1]);
                if (args[1] == "update")
                {
                    Assert.Contains("--dry-run", args);
                    Assert.DoesNotContain("--unpin", args);
                    Assert.DoesNotContain("--force", args);
                    Assert.Equal("demo", args[^1]);
                    Assert.Equal("--", args[^2]);
                }
            }
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "plan.json", SearchOption.AllDirectories));
        }
        finally { foreach (var command in commands) command.Dispose(); }
    }

    [Fact]
    public async Task PinsOwnerConflictsAndLocalSourcesNeverLaunchRemoteChecks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var skill = fixture.Skill("first");
        Assert.Equal(UpdateAvailabilityStatus.Pinned, (await fixture.Observe(skill with { FrontMatter = skill.FrontMatter with { Pinned = true, PinnedRef = "v1" } })).Status);
        Assert.Equal(UpdateAvailabilityStatus.Unknown, (await fixture.Observe(skill with { MetadataConflicts = ["conflicting source"] })).Status);
        Assert.Equal(UpdateAvailabilityStatus.Unknown, (await fixture.Observe(skill with { OwnershipDiagnostics = ["unreadable lock"] })).Status);
        Assert.Equal(UpdateAvailabilityStatus.Unknown, (await fixture.Observe(skill with
        { FrontMatter = skill.FrontMatter with { Pinned = true, PinnedRef = "v1", Extra = skill.FrontMatter.Extra.SetItem("pinned", "false") } })).Status);
        Assert.Equal(UpdateAvailabilityStatus.Unsupported, (await fixture.Observe(skill with { FrontMatter = skill.FrontMatter with { LocalPath = "/local/source" } })).Status);
        var owner = new SkillPackage("tool/plugin", "plugin", null, null, null) { ReadOnly = true, PluginName = "plugin", Provider = "owning-tool" };
        var managed = await fixture.Observe(skill with { OwnershipClaims = [owner] });
        Assert.Equal(UpdateAvailabilityStatus.Unsupported, managed.Status);
        Assert.Contains("owning tool", managed.Reason);
        Assert.False(File.Exists(fixture.Log));
    }

    [Fact]
    public async Task ReplacedPhysicalCopyAndChangedTrackingCannotPublishOldAvailability()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var skill = fixture.Skill("first");
        skill = skill with { ObservedSubject = SkillIdentityObserver.Capture(skill, DateTimeOffset.UtcNow, true, TestContext.Current.CancellationToken) };
        var bytes = File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md"));
        Directory.Move(skill.ResolvedPath, skill.ResolvedPath + "-original");
        Directory.CreateDirectory(skill.ResolvedPath);
        File.WriteAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md"), bytes);
        Assert.Equal(UpdateAvailabilityStatus.Stale, (await fixture.Observe(skill)).Status);
        Assert.False(File.Exists(fixture.Log));
        var changing = fixture.Skill("alter");
        Assert.Equal(UpdateAvailabilityStatus.Stale, (await fixture.Observe(changing)).Status);
        Assert.Equal(UpdateAvailabilityStatus.Failed, (await fixture.Observe(fixture.Skill("failed"))).Status);
    }

    [Fact]
    public async Task CancellationStopsChildAndAmbiguousCatalogNeverChecksSibling()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var slow = fixture.Skill("slow");
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Check(slow, cancellation.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(fixture.Started)) await Task.Delay(10, deadline.Token);
        cancellation.Cancel();
        Assert.Equal(UpdateAvailabilityStatus.Canceled, (await pending.WaitAsync(deadline.Token)).Status);
        var ambiguous = fixture.Skill("ambiguous");
        Assert.Equal(UpdateAvailabilityStatus.Unknown, (await fixture.Observe(ambiguous)).Status);
        Assert.DoesNotContain(File.ReadAllLines(fixture.Log), line => line.Contains("update") && line.Contains("ambiguous"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryRoot = Directory.CreateTempSubdirectory("availability-read-").FullName;
        internal string Root { get; }
        internal string Log => Path.Combine(Root, "commands.jsonl");
        internal string Started => Path.Combine(Root, "started");
        private readonly TuiServices _services;
        private readonly string _gh;

        internal Fixture(bool aliasedContext = false)
        {
            Root = _temporaryRoot;
            if (aliasedContext)
            {
                Root = Path.Combine(_temporaryRoot, "context-alias");
                Directory.CreateSymbolicLink(Root, _temporaryRoot);
            }
            var home = Path.Combine(Root, "home");
            Directory.CreateDirectory(home);
            var environment = new Dictionary<string, string>(SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(Root, "context")))
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["GH_CONFIG_DIR"] = Path.Combine(Root, "gh"),
                ["FIXTURE_ROOT"] = Root
            };
            _services = TuiServices.Build(new Logger(), processEnvironment: environment, workingDirectory: Root);
            _gh = Path.Combine(Root, "gh-fixture");
            File.WriteAllText(_gh, """
                #!/usr/bin/python3
                import json, os, pathlib, sys, time
                root = pathlib.Path(os.environ['FIXTURE_ROOT'])
                a = sys.argv[1:]
                with (root/'commands.jsonl').open('a') as f:
                    f.write(json.dumps(dict(args=a, home=os.environ['HOME'], cwd=os.getcwd()))+'\n')
                parent = pathlib.Path(a[a.index('--dir')+1])
                if a[:2] == ['skill','list']:
                    rows=[dict(skillName='demo', path=str(parent/'demo'), sourceURL='https://github.com/owner/repo', pinned=False)]
                    if parent.name == 'ambiguous':
                        rows.append(dict(skillName='demo', path=str(parent/'sibling'), pinned=False))
                    print(json.dumps(rows))
                elif a[:2] == ['skill','update'] and '--dry-run' in a and a[-2:] == ['--','demo']:
                    if parent.name == 'slow':
                        (root/'started').write_text('started'); time.sleep(60)
                    if parent.name == 'failed':
                        print('HTTP 429 rate limit',file=sys.stderr);sys.exit(1)
                    if parent.name == 'alter':
                        md=parent/'demo/SKILL.md'; md.write_text(md.read_text().replace('refs/heads/main','refs/heads/changed'))
                    print('demo (owner/repo) abc > def [main]' if parent.name=='first' else 'All skills are up to date.')
                else:
                    raise Exception('Unexpected or mutating command')
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal InstalledSkill Skill(string directory)
        {
            var parent = Path.Combine(Root, directory);
            var path = Path.Combine(parent, "demo");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "SKILL.md"), """
                ---
                name: demo
                description: Inert fixture
                metadata:
                  github-repo: https://github.com/owner/repo
                  github-path: skills/demo
                  github-ref: refs/heads/main
                  github-tree-sha: abc
                ---
                Local changes do not affect upstream availability.
                """);
            return Assert.Single(_services.Scanner.Scan([new(parent, Scope.Custom, null)]));
        }

        internal Task<UpdateAvailability> Observe(InstalledSkill skill) => Check(skill, TestContext.Current.CancellationToken);
        internal Task<UpdateAvailability> Check(InstalledSkill skill, CancellationToken token) =>
            _services.UpdateAvailability.CheckAvailabilityAsync(_gh, skill, _services.Runner, token);
        public void Dispose() => Directory.Delete(_temporaryRoot, true);
    }
}
