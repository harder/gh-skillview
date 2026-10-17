using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Preferences;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Preferences;

public sealed class DesiredPolicyInstallTests
{
    [Theory]
    [InlineData(false, false, "demo", false)]
    [InlineData(false, true, "demo", false)]
    [InlineData(true, false, "demo", false)]
    [InlineData(true, true, "demo", false)]
    [InlineData(false, false, "namespace/demo@v1", false)]
    [InlineData(true, false, "namespace/demo@v1", false)]
    [InlineData(false, false, "group/demo/SKILL.md", false)]
    [InlineData(true, false, "group/demo/SKILL.md", false)]
    [InlineData(false, false, "demo", true)]
    [InlineData(true, false, "demo", true)]
    public async Task ExactAndAllForceInstallsRetainActualPinsAndExplicitForks(bool pinned, bool all, string selector, bool upstream)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX fake-gh child marker; portable gate and CLI tests remain separate."); return; }
        var token = TestContext.Current.CancellationToken;
        var temporary = Directory.CreateTempSubdirectory("skillview-policy-install-");
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
            var skills = Directory.CreateDirectory(Path.Combine(temporary.FullName, "skills")).FullName;
            var payload = Directory.CreateDirectory(selector.StartsWith("group/", StringComparison.Ordinal)
                ? Path.Combine(skills, "group", "demo") : Path.Combine(skills, "demo")).FullName;
            var markdown = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/repository\n"
                + (pinned ? "  github-pinned: v1\n" : "") + "---\nOriginal instructions\n";
            File.WriteAllText(Path.Combine(payload, "SKILL.md"), markdown);
            var marker = Path.Combine(temporary.FullName, "mutation");
            var discovery = Path.Combine(temporary.FullName, "discovery");
            var fake = Path.Combine(temporary.FullName, "fake-gh");
            // This fixture is an adapter recorder, never a skill payload.
            File.WriteAllText(fake, "#!/bin/sh\nfor arg do\n case \"$arg\" in --all|demo) touch \"$SV_MUTATION\"; exit 0;; esac\ndone\ntouch \"$SV_DISCOVERY\"\nprintf '[root] demo\\tdescription\\n'\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
                ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
                ["CODEX_HOME"] = Path.Combine(home, "codex"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
                ["SV_MUTATION"] = marker,
                ["SV_DISCOVERY"] = discovery,
                ["TMPDIR"] = "/var/tmp",
                ["GIT_DIR"] = Path.Combine(temporary.FullName, ".git"),
                ["GIT_WORK_TREE"] = temporary.FullName,
                ["GIT_COMMON_DIR"] = Path.Combine(temporary.FullName, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            }, defaultWorkingDirectory: temporary.FullName);
            if (!pinned)
            {
                var target = Assert.Single(new LocalSkillScanner(logger).ScanWithCancellation([new(skills, Scope.Custom, null)], null, token));
                var store = DesiredRevisionPolicyStore.For(runner);
                var before = store.Observe(target, [skills], token);
                using var writer = store.Acquire([skills], token);
                writer.Set(target, new("manual-fork"), before.Fingerprint!, token);
            }
            var result = await new GhSkillInstallService(runner, logger).InstallAsync(fake,
                upstream ? "https://github.com/fixture/source/tree/main/group/demo" : "fixture/source", all ? null : selector,
                new(Path: skills, FromLocal: !upstream, Upstream: upstream, Overwrite: true, All: all), token);
            Assert.False(result.Succeeded);
            Assert.Contains(pinned ? "pinned installation" : "explicit desired-state", result.ErrorMessage);
            Assert.False(File.Exists(discovery));
            Assert.False(File.Exists(marker));
            Assert.Equal(markdown, File.ReadAllText(Path.Combine(payload, "SKILL.md")));
            var captured = Assert.Single(new LocalSkillScanner(logger).ScanWithCancellation([new(skills, Scope.Custom, null)], null, token));
            var raw = await new GhSkillUpdateService(runner, logger).UpdateAsync(fake,
                new(Skills: ["demo"], Directory: Path.GetDirectoryName(payload), Targets: [captured], Force: true,
                    ProcessEnvironment: new Dictionary<string, string> { ["HOME"] = Path.Combine(home, "incidental-registry-home") }), token);
            Assert.False(raw.Succeeded); Assert.False(raw.MutationAttempted);
            Assert.Contains(pinned ? "Exact pin retained" : "manual-fork", raw.ErrorMessage);
            Assert.False(File.Exists(marker));
        }
        finally { temporary.Delete(true); }
    }
}
