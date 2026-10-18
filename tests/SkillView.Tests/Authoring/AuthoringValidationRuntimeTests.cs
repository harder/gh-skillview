using System.Security.Cryptography;
using SkillView.Authoring;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Authoring;

/// Opt-in local proof only. No remote repository and no real credentials.
public sealed class AuthoringValidationRuntimeTests
{
    [Fact]
    public async Task ExplicitOfflineProofUsesBothFixedGhVersionsAndPreservesAuthoredBytes()
    {
        var tools = System.Environment.GetEnvironmentVariable("SKILLVIEW_AUTHORING_PROOF_TOOLS");
        var evidence = System.Environment.GetEnvironmentVariable("SKILLVIEW_AUTHORING_PROOF_EVIDENCE");
        if (tools is null || evidence is null) Assert.Skip("Opt in with private authoring proof tools/evidence paths; normal tests never launch publish dry-run.");
        Assert.StartsWith("/var/tmp/", System.Environment.GetEnvironmentVariable("HOME")!, StringComparison.Ordinal);
        Assert.StartsWith("/var/tmp/", System.Environment.GetEnvironmentVariable("TMPDIR")!, StringComparison.Ordinal);
        Assert.Equal("skillview-authoring-offline-dummy-not-a-credential", System.Environment.GetEnvironmentVariable("GH_TOKEN"));
        Assert.Null(System.Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        Assert.Null(System.Environment.GetEnvironmentVariable("GIT_DIR"));
        Assert.Null(System.Environment.GetEnvironmentVariable("GIT_WORK_TREE"));
        Assert.Null(System.Environment.GetEnvironmentVariable("GIT_COMMON_DIR"));
        Directory.CreateDirectory(evidence);
        var fixture = Path.Combine(System.Environment.GetEnvironmentVariable("TMPDIR")!, "authoring-runtime");
        Directory.CreateDirectory(fixture);
        var template = Directory.CreateDirectory(Path.Combine(fixture, "empty-template")).FullName;
        var logger = new Logger(LogLevel.Debug);
        var commands = new List<string>();
        using var subscription = logger.Subscribe(entry => { if (entry.Category == "subprocess") commands.Add(entry.Message); });
        var runner = new ProcessRunner(logger, defaultWorkingDirectory: fixture);
        foreach (var version in new[] { "2.97.0", "2.102.0" })
        {
            var gh = Path.Combine(tools, version, "gh");
            Assert.True(File.Exists(gh));
            foreach (var malformed in new[] { false, true })
            {
                var name = version + (malformed ? "-malformed" : "-valid");
                var repo = Directory.CreateDirectory(Path.Combine(fixture, name)).FullName;
                var payload = Directory.CreateDirectory(Path.Combine(repo, "skills", "demo")).FullName;
                var metadata = Path.Combine(payload, "SKILL.md");
                File.WriteAllText(metadata, "---\nname: demo\n" + (malformed ? "" : "description: Offline authoring proof\n") + "---\n# Authored source\nExact bytes.\n");
                var marker = Path.Combine(fixture, name + "-script-ran");
                File.WriteAllText(Path.Combine(payload, "never-run.sh"), "#!/bin/sh\nprintf ran > '" + marker + "'\nexit 88\n");
                var init = await runner.RunAsync("git", ["init", "--quiet", "--template=" + template, "--", repo], cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(init.Succeeded, init.StdErr);
                var remotes = await runner.RunAsync("git", ["-C", repo, "remote", "-v"], cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(remotes.Succeeded, remotes.StdErr); Assert.Empty(remotes.StdOut);
                var before = Files(repo);
                File.WriteAllText(Path.Combine(evidence, name + "-before.txt"), string.Join('\n', before));
                var report = await new AuthoringValidationService(runner, logger).ValidateAsync(repo, gh, TestContext.Current.CancellationToken);
                File.WriteAllText(Path.Combine(evidence, name + "-report.json"), CliDispatcher.RenderAuthoringJson(report));
                File.WriteAllLines(Path.Combine(evidence, name + "-commands.txt"), commands);
                commands.Clear();
                File.WriteAllBytes(Path.Combine(evidence, name + "-SKILL.md"), File.ReadAllBytes(metadata));
                var after = Files(repo);
                File.WriteAllText(Path.Combine(evidence, name + "-after.txt"), string.Join('\n', after));
                Assert.Equal(before, after); Assert.False(File.Exists(marker));
                Assert.Equal(version, report.GhVersion);
                Assert.Equal(malformed ? "failed" : "completed", report.Status);
                Assert.Equal(malformed ? 1 : 0, report.CommandExitCode);
                Assert.Equal("unchanged-observed-candidates", report.AuthoredBytes);
                Assert.Contains("demo", report.StdOut + report.StdErr);
                if (malformed) Assert.Contains("missing required field: description", report.StdOut + report.StdErr);
                else Assert.Contains("Dry run complete", report.StdOut + report.StdErr);
                Assert.DoesNotContain("api.github.com", report.StdOut + report.StdErr);
            }
        }

        static string[] Files(string repo) => Directory.GetFiles(repo, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(repo, file) + " " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))
            .Order(StringComparer.Ordinal).ToArray();
    }
}
