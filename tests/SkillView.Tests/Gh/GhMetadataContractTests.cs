using System.Text;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Gh;

[Trait("Category", "Contract")]
public sealed class GhMetadataContractTests
{
    [Theory]
    [InlineData("unpinned")]
    [InlineData("pinned")]
    [InlineData("local-literal")]
    [InlineData("local-folded")]
    public async Task ActualGhTrackingSurvivesRawScanInventoryAndDetail(string kind)
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("SKILLVIEW_CONTRACT_TESTS"), "true", StringComparison.OrdinalIgnoreCase)) return;
        var logger = new Logger();
        var parentRunner = new ProcessRunner(logger);
        var gh = new GhBinaryLocator(parentRunner, logger).FindOnPath(TestContext.Current.CancellationToken);
        Assert.NotNull(gh);
        var token = System.Environment.GetEnvironmentVariable("GH_TOKEN") ?? System.Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            var credentials = await parentRunner.RunAsync(gh!, ["auth", "token", "--hostname", "github.com"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(credentials.Succeeded && !credentials.StdOutTruncated, "Configured GitHub credentials are required for this opt-in contract.");
            token = credentials.StdOut.Trim();
        }
        Assert.False(string.IsNullOrWhiteSpace(token));
        var temporary = Directory.CreateTempSubdirectory("skillview-tracking-contract-");
        try
        {
            var environment = new Dictionary<string, string>(SkillUpdateCoordinator.CreateStagingEnvironment(
                Path.Combine(temporary.FullName, "fixture")));
            environment["GH_TOKEN"] = token;
            environment["GH_CONFIG_DIR"] = Path.Combine(temporary.FullName, "gh-config");
            environment["SKILLVIEW_STATE_HOME"] = Path.Combine(temporary.FullName, "state");
            environment["CLAUDE_CONFIG_DIR"] = Path.Combine(environment["HOME"], ".claude");
            environment["PI_CODING_AGENT_DIR"] = Path.Combine(environment["HOME"], ".pi", "agent");
            foreach (var key in System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)))
                environment[key] = null!;
            environment["GIT_CONFIG_COUNT"] = "0";
            var runner = new ProcessRunner(logger, defaultWorkingDirectory: temporary.FullName, environment: environment);
            var destination = Path.Combine(temporary.FullName, "destination");
            var local = kind.StartsWith("local-", StringComparison.Ordinal);
            var name = local ? kind : "gh-skill";
            var source = Path.Combine(temporary.FullName, "author-source");
            var sourceSkill = Path.Combine(source, name);
            var arguments = new List<string> { "skill", "install", "--dir", destination };
            if (local)
            {
                Directory.CreateDirectory(sourceSkill);
                var style = kind == "local-literal" ? "|-" : ">-";
                File.WriteAllBytes(Path.Combine(sourceSkill, "SKILL.md"), Encoding.UTF8.GetBytes(
                    $"---\nname: {name}\ndescription: {style}\n  First line\n  second line\nallowed-tools: Read Bash(git log:*)\nmetadata:\n  version: \"1.0\"\n  vendor/example: keep\n---\n# Private fixture\n"));
                arguments.AddRange(["--from-local", "--", source, name]);
            }
            else
            {
                if (kind == "pinned") arguments.Add("--pin=v2.102.0");
                arguments.AddRange(["--", "cli/cli", "skills/gh-skill"]);
            }
            var installed = await runner.RunAsync(gh!, arguments, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(installed.Succeeded && !installed.StdOutTruncated, installed.StdErr);
            var markdownPath = Path.Combine(destination, name, "SKILL.md");
            var metadataBefore = File.ReadAllBytes(markdownPath);
            var raw = Assert.Single(new LocalSkillScanner(logger).Scan([new(destination, Scope.Custom, null)]));
            Assert.Equal(ValidityState.Valid, raw.Validity);
            Assert.Empty(raw.FrontMatter.Diagnostics);
            var adapter = new GhSkillListAdapter(runner, logger);
            var catalog = await adapter.CaptureAsync(gh!, directory: destination, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(catalog.Succeeded, catalog.Error);
            var record = Assert.Single(catalog.Records);
            Assert.Equal(name, record.Name);
            Assert.True(PathIdentity.Equals(raw.ResolvedPath, record.Path!));
            Assert.Equal(kind == "pinned", raw.Pinned);
            Assert.Equal(raw.Pinned, record.Pinned);
            var snapshot = await new LocalInventoryService(new(), new(logger), adapter, logger, runner).CaptureAsync(gh!,
                new([destination], true), TestContext.Current.CancellationToken);
            Assert.False(snapshot.IsPartial);
            var skill = Assert.Single(snapshot.Skills);
            Assert.Empty(skill.MetadataConflicts);
            using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(snapshot));
            var row = json.RootElement.GetProperty("skills")[0];
            var detail = InstalledScreen.RenderDetail(skill, showDetails: true);
            Assert.Contains($"- **Pinned:** {(record.Pinned ? "Yes" : "No")}", detail);
            if (local)
            {
                Assert.True(Path.IsPathFullyQualified(raw.FrontMatter.LocalPath!));
                Assert.True(PathIdentity.Equals(sourceSkill, raw.FrontMatter.LocalPath!));
                Assert.True(PathIdentity.Equals(sourceSkill, record.SourceUrl!));
                Assert.Null(raw.FrontMatter.Upstream);
                Assert.Null(raw.FrontMatter.GithubRef);
                Assert.Null(raw.FrontMatter.GithubPath);
                Assert.Null(raw.TreeSha);
                Assert.Null(raw.FrontMatter.PinnedRef);
                Assert.DoesNotContain(raw.FrontMatter.Metadata.Keys, key => key.StartsWith("github-", StringComparison.Ordinal));
                var description = kind == "local-literal" ? "First line\nsecond line" : "First line second line";
                Assert.Equal(description, raw.FrontMatter.Description);
                Assert.Equal(["Read", "Bash(git log:*)"], raw.FrontMatter.AllowedTools);
                Assert.Equal("1.0", raw.FrontMatter.Version);
                Assert.Equal("keep", raw.FrontMatter.Metadata["vendor/example"]);
                Assert.Equal(raw.FrontMatter.Version, skill.FrontMatter.Version);
                Assert.Equal(raw.FrontMatter.Description, row.GetProperty("description").GetString());
                Assert.Equal("1.0", row.GetProperty("version").GetString());
                Assert.True(PathIdentity.Equals(sourceSkill, row.GetProperty("localSourcePath").GetString()!));
                Assert.True(string.IsNullOrEmpty(record.Version));
                Assert.Equal("local", skill.SourceIdentity.Provider);
                Assert.Contains($"- **Local source:** `{raw.FrontMatter.LocalPath}`", detail);
                Assert.Contains("- **Version:** 1.0", detail);
                Assert.Contains(description, detail);
                Assert.DoesNotContain("- **Repository ref:**", detail);
            }
            else
            {
                Assert.Equal("https://github.com/cli/cli", raw.FrontMatter.Upstream);
                Assert.Equal(raw.FrontMatter.Upstream, record.SourceUrl);
                Assert.Equal("skills/gh-skill", raw.FrontMatter.GithubPath);
                Assert.Equal(ShortRef(raw.FrontMatter.GithubRef), record.Version);
                Assert.Matches("^[0-9a-f]{40}$", raw.TreeSha!);
                Assert.Null(raw.FrontMatter.LocalPath);
                Assert.Equal(kind == "pinned" ? "v2.102.0" : null, raw.FrontMatter.PinnedRef);
                Assert.Equal(raw.FrontMatter.GithubRef, row.GetProperty("repositoryRef").GetString());
                Assert.Equal(raw.FrontMatter.Upstream, row.GetProperty("source").GetString());
                Assert.Equal(raw.FrontMatter.PinnedRef, row.GetProperty("pinnedRef").GetString());
                Assert.Contains("- **Upstream:** [https://github.com/cli/cli](https://github.com/cli/cli)", detail);
                Assert.Contains($"- **Repository ref:** `{raw.FrontMatter.GithubRef}`", detail);
                Assert.Contains("- **Source path:** `skills/gh-skill`", detail);
            }
            if (kind == "pinned") Assert.Contains("- **Pinned ref:** `v2.102.0`", detail);
            else Assert.DoesNotContain("- **Pinned ref:**", detail);
            Assert.Equal(metadataBefore, File.ReadAllBytes(markdownPath));
        }
        finally { temporary.Delete(true); }
    }

    private static string? ShortRef(string? value)
    {
        foreach (var prefix in new[] { "refs/heads/", "refs/tags/" })
            if (value?.StartsWith(prefix, StringComparison.Ordinal) == true) return value[prefix.Length..];
        return value;
    }
}
