using System.Collections.Concurrent;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class InventoryProviderOutcomeTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-provider-outcome-");
    public void Dispose() => temporary.Delete(true);

    [Fact]
    public async Task FreshInventoryBypassesBothDefaultAndRootCatalogsWithoutInvalidation()
    {
        var root = CreateSkillRoot("fresh-catalog", "visible");
        var logger = new Logger();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var adapter = new GhSkillListAdapter((executable, arguments, token) =>
        {
            calls.AddOrUpdate(DirectoryArgument(arguments) ?? "default", 1, (_, count) => count + 1);
            return Task.FromResult(new ProcessResult(executable, arguments, 0, "[]", "", TimeSpan.Zero));
        }, logger);
        var service = CreateService(logger, adapter);
        var options = new LocalInventoryService.Options([root], true);
        await service.CaptureAsync("protocol-fixture", options, TestContext.Current.CancellationToken);
        var generation = adapter.Generation;
        await service.CaptureAsync("protocol-fixture", options with { BypassGhCache = true }, TestContext.Current.CancellationToken);
        Assert.Equal(generation, adapter.Generation);
        Assert.Equal(2, calls["default"]);
        Assert.Equal(2, calls[root]);
        await service.CaptureAsync("protocol-fixture", options, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls["default"]);
        Assert.Equal(2, calls[root]);
    }

    [Theory]
    [InlineData("credentials", false, "credentials-unavailable-sentinel")]
    [InlineData("network", false, "network-unavailable-sentinel")]
    [InlineData("malformed", false, "invalid inventory JSON")]
    [InlineData("schema", false, "invalid inventory JSON")]
    [InlineData("record", false, "invalid inventory JSON")]
    [InlineData("truncated", false, "truncated")]
    [InlineData("empty", true, null)]
    public async Task CapturePreservesProviderOutcomeWithoutInventingOrphaning(string outcome, bool succeeded, string? expectedError)
    {
        var root = CreateSkillRoot("loose-payloads", "visible");
        var logger = new Logger();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var adapter = new GhSkillListAdapter((executable, arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            var key = DirectoryArgument(arguments) ?? "default";
            calls.AddOrUpdate(key, 1, (_, count) => count + 1);
            var output = outcome switch
            {
                "malformed" => "{broken",
                "schema" => "{\"skills\":\"not-an-array\"}",
                "record" => "[{\"skillName\":17}]",
                _ => "[]"
            };
            var error = outcome switch
            {
                "credentials" => "credentials-unavailable-sentinel",
                "network" => "network-unavailable-sentinel",
                _ => ""
            };
            return Task.FromResult(new ProcessResult(executable, arguments, error.Length > 0 ? 1 : 0,
                output, error, TimeSpan.Zero)
            { StdOutTruncated = outcome == "truncated" });
        }, logger);
        var service = CreateService(logger, adapter);
        var options = new LocalInventoryService.Options([root], true);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await service.CaptureAsync("protocol-fixture", options, TestContext.Current.CancellationToken);
            Assert.Equal(!succeeded, snapshot.IsPartial);
            Assert.Equal(succeeded, snapshot.UsedGhSkillList);
            Assert.Equal("visible", Assert.Single(snapshot.Skills).Name);
            var providers = snapshot.Providers.Where(provider => provider.Provider.StartsWith("gh", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, providers.Length);
            Assert.All(providers, provider =>
            {
                Assert.Equal(succeeded, provider.Succeeded);
                if (succeeded) Assert.Null(provider.Error);
                else Assert.Contains(expectedError!, provider.Error!, StringComparison.Ordinal);
            });
            Assert.Contains(providers, provider => provider.Provider == "gh" && provider.CoveredRoots.Contains(root));
            using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(snapshot));
            Assert.Equal(!succeeded, json.RootElement.GetProperty("partial").GetBoolean());
            if (!succeeded)
                Assert.Contains(json.RootElement.GetProperty("providers").EnumerateArray(),
                    provider => provider.GetProperty("error").GetString()?.Contains(expectedError!, StringComparison.Ordinal) == true);
            var candidates = CleanupClassifier.Classify(snapshot, []);
            if (!succeeded) Assert.Empty(candidates);
            else
            {
                var candidate = Assert.Single(candidates);
                Assert.Equal(CleanupClassifier.CandidateKind.Untracked, candidate.Kind);
                Assert.True(candidate.ReviewOnly);
            }
            Assert.DoesNotContain(candidates, candidate => candidate.Kind == CleanupClassifier.CandidateKind.SourceOrphaned);
        }
        Assert.Equal(succeeded ? 1 : 2, calls["default"]);
        Assert.Equal(succeeded ? 1 : 2, calls[root]);
    }

    [Fact]
    public async Task SuccessfulDefaultCatalogDoesNotHideFailedCustomRootCoverage()
    {
        var root = CreateSkillRoot("custom", "visible");
        var logger = new Logger();
        var defaultCalls = 0;
        var customCalls = 0;
        var adapter = new GhSkillListAdapter((executable, arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            var directory = DirectoryArgument(arguments);
            if (directory is null) defaultCalls++;
            else { Assert.Equal(root, directory); customCalls++; }
            return Task.FromResult(new ProcessResult(executable, arguments, directory is null ? 0 : 1,
                "[]", directory is null ? "" : "custom-root-unobserved-sentinel", TimeSpan.Zero));
        }, logger);
        var service = CreateService(logger, adapter);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await service.CaptureAsync("protocol-fixture", new([root], true), TestContext.Current.CancellationToken);
            Assert.True(snapshot.UsedGhSkillList);
            Assert.True(snapshot.IsPartial);
            Assert.Equal("visible", Assert.Single(snapshot.Skills).Name);
            Assert.Contains(snapshot.Providers, provider => provider.Provider == "gh-default" && provider.Succeeded && provider.CoveredRoots.IsEmpty);
            Assert.Contains(snapshot.Providers, provider => provider.Provider == "gh" && !provider.Succeeded
                && provider.CoveredRoots.Contains(root) && provider.Error!.Contains("custom-root-unobserved-sentinel"));
            Assert.Empty(CleanupClassifier.Classify(snapshot, []));
        }
        Assert.Equal(1, defaultCalls);
        Assert.Equal(2, customCalls);
    }

    [Fact]
    public async Task InaccessibleRootRetainsReadableControlAndSuppressesAbsenceClassification()
    {
        var root = CreateSkillRoot("readable", "visible");
        var denied = CreateSkillRoot("denied", "inaccessible");
        var logger = new Logger();
        var scanner = new LocalSkillScanner(logger, null, enumerateEntriesForTests: path =>
            PathIdentity.Equals(path, denied) ? throw new UnauthorizedAccessException("enumeration-denied-sentinel")
                : Directory.EnumerateFileSystemEntries(path).GetEnumerator());
        var snapshot = await CreateService(logger, EmptyAdapter(logger), scanner).CaptureAsync("protocol-fixture",
            new([root, denied], true), TestContext.Current.CancellationToken);
        Assert.True(snapshot.IsPartial);
        Assert.Equal("visible", Assert.Single(snapshot.Skills).Name);
        Assert.Contains(denied, snapshot.Diagnostics.InaccessiblePaths);
        Assert.Contains(snapshot.Diagnostics.Locations, location => PathIdentity.Equals(location.Path, denied) && location.State == "inaccessible");
        Assert.All(snapshot.Providers, provider => Assert.True(provider.Succeeded));
        Assert.Empty(CleanupClassifier.Classify(snapshot, []));
        using var json = JsonDocument.Parse(CliDispatcher.RenderListJson(snapshot));
        Assert.True(json.RootElement.GetProperty("partial").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("inaccessiblePaths").EnumerateArray(), path => path.GetString() == denied);
    }

    [Fact]
    public async Task MissingRequiredCustomRootRemainsDiagnosticWithoutClaimedCoverage()
    {
        var root = CreateSkillRoot("readable", "visible");
        var missing = Path.Combine(temporary.FullName, "missing-custom-root");
        var logger = new Logger();
        var snapshot = await CreateService(logger, EmptyAdapter(logger)).CaptureAsync("protocol-fixture",
            new([root, missing], true), TestContext.Current.CancellationToken);
        Assert.True(snapshot.IsPartial);
        Assert.Contains(snapshot.ConfiguredRoots, candidate => PathIdentity.Equals(candidate.Path, missing));
        Assert.DoesNotContain(snapshot.ScannedRoots, candidate => PathIdentity.Equals(candidate.Path, missing));
        Assert.Contains(missing, snapshot.Diagnostics.InaccessiblePaths);
        Assert.DoesNotContain(snapshot.Providers, provider => provider.Succeeded && provider.CoveredRoots.Contains(missing));
        Assert.Contains(snapshot.Providers, provider => provider.Provider == "gh" && provider.Succeeded && provider.CoveredRoots.Contains(root));
        Assert.Equal("visible", Assert.Single(snapshot.Skills).Name);
        Assert.Empty(CleanupClassifier.Classify(snapshot, []));
    }

    [Fact]
    public async Task RootCatalogProbesOverlapWithinBoundAndPreserveConfiguredOrder()
    {
        var roots = Enumerable.Range(0, 8).Select(i => CreateSkillRoot("root-" + i, "skill-" + i)).ToArray();
        var logger = new Logger();
        var active = 0;
        var maximum = 0;
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new GhSkillListAdapter(async (executable, arguments, token) =>
        {
            if (DirectoryArgument(arguments) is not null)
            {
                var current = Interlocked.Increment(ref active);
                int prior;
                do { prior = Volatile.Read(ref maximum); }
                while (current > prior && Interlocked.CompareExchange(ref maximum, current, prior) != prior);
                if (current >= 2) overlap.TrySetResult();
                try { await overlap.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
            }
            return new ProcessResult(executable, arguments, 0, "[]", "", TimeSpan.Zero);
        }, logger);
        var snapshot = await CreateService(logger, adapter).CaptureAsync("protocol-fixture", new(roots, true),
            TestContext.Current.CancellationToken);
        Assert.InRange(maximum, 2, 4);
        Assert.Equal(0, active);
        Assert.Equal(snapshot.ScannedRoots.Where(root => !root.Managed).Select(root => root.Path),
            snapshot.Providers.Where(provider => provider.Provider == "gh").SelectMany(provider => provider.CoveredRoots));
    }

    [Fact]
    public async Task CancelingCaptureDrainsActiveRootProbes()
    {
        var roots = Enumerable.Range(0, 8).Select(i => CreateSkillRoot("cancel-root-" + i, "skill-" + i)).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var logger = new Logger();
        var adapter = new GhSkillListAdapter(async (executable, arguments, token) =>
        {
            if (DirectoryArgument(arguments) is not null)
            {
                Interlocked.Increment(ref active);
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref active); }
            }
            return new ProcessResult(executable, arguments, 0, "[]", "", TimeSpan.Zero);
        }, logger);
        var capture = CreateService(logger, adapter).CaptureAsync("protocol-fixture", new(roots, true), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(0, active);
    }

    private string CreateSkillRoot(string directory, string name)
    {
        var root = Path.Combine(temporary.FullName, directory);
        var payload = Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(payload.FullName, "SKILL.md"), $"---\nname: {name}\ndescription: Provider outcome fixture\n---\nBody\n");
        return root;
    }

    private LocalInventoryService CreateService(Logger logger, GhSkillListAdapter adapter, LocalSkillScanner? scanner = null)
    {
        var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
        var runner = new ProcessRunner(logger, defaultWorkingDirectory: temporary.FullName, environment: new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = "",
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi", "agent")
        });
        return new LocalInventoryService(new(), scanner ?? new(logger), adapter, logger, runner);
    }

    private static GhSkillListAdapter EmptyAdapter(Logger logger) => new((executable, arguments, token) =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ProcessResult(executable, arguments, 0, "[]", "", TimeSpan.Zero));
    }, logger);

    private static string? DirectoryArgument(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
            if (arguments[index] == "--dir") return arguments[index + 1];
        return null;
    }
}
