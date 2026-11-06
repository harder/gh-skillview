using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ForceInstallDispositionTests : IDisposable
{
    private readonly DirectoryInfo root = CreateRoot();
    private static DirectoryInfo CreateRoot()
    {
        var path = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/var/tmp",
            "skillview-force-admission-" + Guid.NewGuid().ToString("N"));
        return OperatingSystem.IsWindows() ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => root.Delete(true);
    private string Destination => Path.Combine(root.FullName, "skills");
    private string Marker => Path.Combine(root.FullName, "install-dispatched");

    private Dictionary<string, string> EnvironmentMap()
    {
        var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
        return new(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = bin,
            ["GH_EXTENSION"] = "0",
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
            ["CODEX_HOME"] = Path.Combine(home, "codex"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "localappdata"),
            ["GIT_DIR"] = Path.Combine(root.FullName, "private.git"),
            ["GIT_WORK_TREE"] = root.FullName,
            ["GIT_COMMON_DIR"] = Path.Combine(root.FullName, "private.git"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["TMPDIR"] = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/var/tmp",
            ["GH_TOKEN"] = "",
            ["GITHUB_TOKEN"] = "",
            ["GH_ENTERPRISE_TOKEN"] = "",
            ["GITHUB_ENTERPRISE_TOKEN"] = "",
            ["SKILLVIEW_TEST_MARKER"] = Marker
        };
    }

    private string FakeGh(Dictionary<string, string> environment)
    {
        var path = Path.Combine(environment["PATH"], "gh");
        if (OperatingSystem.IsWindows()) return path; // Refusals must precede even a nonexistent executable.
        File.WriteAllText(path, """
            #!/bin/sh
            case "$1 $2" in
              "--version ") printf 'gh version 2.102.0 (fixture)\n';;
              "auth status") exit 0;;
              "skill --help") printf 'fixture skills\n';;
              "skill list") printf '[]\n';;
              "skill install") printf 'dispatched' > "$SKILLVIEW_TEST_MARKER"; printf '%s\n' "$@";;
              *) exit 71;;
            esac
            """ + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private GhSkillInstallService Service(Dictionary<string, string> environment)
    {
        var logger = new Logger();
        return new(new ProcessRunner(logger).WithIsolatedContext(root.FullName, environment), logger);
    }

    private TuiServices CliServices(Dictionary<string, string> environment)
    {
        var logger = new Logger();
        var runner = new ProcessRunner(logger).WithIsolatedContext(root.FullName, environment);
        // The normal composition root discovers the host's gh. Bind this probe
        // explicitly to the inert fixture without changing the parent PATH.
        var locator = new GhBinaryLocator(runner, logger, () => environment["PATH"], File.Exists);
        var auth = new GhAuthService(runner, logger);
        var list = new GhSkillListAdapter(runner, logger);
        var resolver = new ScanRootResolver();
        var scanner = new LocalSkillScanner(logger);
        var logs = Path.Combine(root.FullName, "logs");
        return new()
        {
            Logger = logger,
            Runner = runner,
            GhLocator = locator,
            AuthService = auth,
            EnvironmentProbe = new EnvironmentProbe(locator, auth, runner, logger, logs),
            SearchService = new(runner, logger),
            PreviewService = new(runner, logger),
            InstallService = new(runner, logger),
            UpdateService = new(runner, logger),
            ListAdapter = list,
            ScanRootResolver = resolver,
            Scanner = scanner,
            InventoryService = new(resolver, scanner, list, logger, runner),
            RemoveService = new(logger),
            FileLogSink = null,
            LogDirectory = logs
        };
    }

    private void Payload(string delta)
    {
        var payload = Directory.CreateDirectory(Path.Combine(Destination, "demo")).FullName;
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Private fixture\nmetadata:\n  local-path: /unavailable/original\n---\nInstalled instructions\n");
        File.WriteAllText(Path.Combine(payload, "tracked.txt"), delta == "tracked" ? "local edit" : "recorded bytes");
        if (delta != "deleted") File.WriteAllText(Path.Combine(payload, "deleted-upstream.txt"), "recorded member");
        if (delta == "extra") File.WriteAllBytes(Path.Combine(payload, "extra.bin"), [0, 255, 10]);
        if (delta == "ignored") File.WriteAllText(Path.Combine(payload, ".skillview-ignore"), "Keep this exclusion\n");
    }

    private SortedDictionary<string, string> Bytes() => new(Directory.GetFiles(Destination, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Destination, path), path => Convert.ToBase64String(File.ReadAllBytes(path))), StringComparer.Ordinal);

    [Theory]
    [InlineData("tracked")]
    [InlineData("extra")]
    [InlineData("deleted")]
    [InlineData("ignored")]
    public async Task ExistingOwnerlessLocalPayloadCannotReachForceChildOrLoseAnyBytes(string delta)
    {
        Payload(delta);
        var before = Bytes();
        var environment = EnvironmentMap();
        // Unrelated manager bytes lie outside the admitted custom destination.
        var unrelated = Path.Combine(Directory.CreateDirectory(Path.Combine(environment["HOME"], ".agents")).FullName, ".skill-lock.json");
        const string unrelatedBytes = "{\"version\":3,\"skills\":{\"unrelated\":{\"source\":\"other/repo\",\"sourceType\":\"github\",\"fixtureExtension\":true}}}\n";
        File.WriteAllText(unrelated, unrelatedBytes);
        var result = await Service(environment).InstallAsync(FakeGh(environment), "/unavailable/source", "demo",
            new(Path: Destination, Overwrite: true, FromLocal: true), Token);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Force install refused", result.ErrorMessage);
        Assert.Contains(Destination, result.ErrorMessage);
        Assert.Contains("Unknown provenance", result.ErrorMessage);
        Assert.False(File.Exists(Marker));
        Assert.Equal(before, Bytes());
        Assert.Equal(unrelatedBytes, File.ReadAllText(unrelated));
        Assert.Equal(delta == "deleted", !File.Exists(Path.Combine(Destination, "demo", "deleted-upstream.txt")));
    }

    [Theory]
    [InlineData("team/demo", false)]
    [InlineData("custom/demo/SKILL.md", false)]
    [InlineData("demo@v1", false)]
    [InlineData(null, true)]
    public async Task AmbiguousOrAllSelectorsCannotTurnOccupiedRootsIntoVacancy(string? selector, bool all)
    {
        Directory.CreateDirectory(Path.Combine(Destination, "unrelated-name"));
        var environment = EnvironmentMap();
        var result = await Service(environment).InstallAsync(FakeGh(environment), "owner/repo", selector,
            new(Path: Destination, Overwrite: true, All: all), Token);
        Assert.False(result.Succeeded);
        Assert.Contains("No selector-to-native target was inferred", result.ErrorMessage);
        Assert.False(File.Exists(Marker));
        Assert.True(Directory.Exists(Path.Combine(Destination, "unrelated-name")));
    }

    [Fact]
    public async Task ManagedAllRefusalRetainsSpecificDiagnosisBeforeForceVacancyChecks()
    {
        var managed = Directory.CreateDirectory(Path.Combine(Destination, "managed-child")).FullName;
        File.WriteAllText(Path.Combine(managed, "plugin.json"), "{\"name\":\"fixture\"}");
        var before = Bytes();
        var environment = EnvironmentMap();
        var result = await Service(environment).InstallAsync(FakeGh(environment), "/unavailable/source", null,
            new(Path: Destination, All: true, FromLocal: true, Overwrite: true), Token);
        Assert.False(result.Succeeded);
        Assert.Contains("immutable staged maintenance plan", result.ErrorMessage);
        Assert.Equal(before, Bytes());
        Assert.False(File.Exists(Marker));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task VacantDestinationStillDispatchesTrustedFreshInstall(bool exists, bool force, bool all)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Actual shell child marker requires POSIX; native guard refusals are portable."); return; }
        if (force && !SecureRemovalBackend.IsSupported) { Assert.Skip("Native vacancy unavailable: " + SecureRemovalBackend.UnsupportedReason); return; }
        if (exists) Directory.CreateDirectory(Destination);
        var environment = EnvironmentMap();
        var result = await Service(environment).InstallAsync(FakeGh(environment), "owner/repo", all ? null : "demo",
            new(Path: Destination, Overwrite: force, All: all), Token);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(File.Exists(Marker));
        Assert.Contains(all ? "--\nowner/repo\n" : "--\nowner/repo\ndemo\n", result.StdOut);
        Assert.Equal(all, result.CommandLine.Contains("--all"));
        Assert.DoesNotContain("--force", result.CommandLine);
        Assert.DoesNotContain("--force\n", result.StdOut);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("user")]
    public async Task ForceUsesActualCatalogScopeRootsAndProtectsEveryCodexPlacement(string scope)
    {
        var environment = EnvironmentMap();
        var destination = scope == "project" ? Path.Combine(root.FullName, ".agents", "skills")
            : Path.Combine(environment["HOME"], ".codex", "skills");
        var payload = Directory.CreateDirectory(Path.Combine(destination, "demo")).FullName;
        const string instructions = "---\nname: demo\ndescription: Catalog fixture\n---\nKeep exact local instructions\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), instructions);
        var result = await Service(environment).InstallAsync(FakeGh(environment), "/unavailable/source", "demo",
            new(Agents: ["codex"], Scope: scope, FromLocal: true, Overwrite: true), Token);
        Assert.False(result.Succeeded);
        Assert.Contains(destination, result.ErrorMessage);
        Assert.Contains("Force install refused", result.ErrorMessage);
        Assert.Equal(instructions, File.ReadAllText(Path.Combine(payload, "SKILL.md")));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void FinalVacancyCaptureRefusesPayloadCreatedAfterInitialCapture()
    {
        if (!SecureRemovalBackend.IsSupported) { Assert.Skip("Native vacancy unavailable: " + SecureRemovalBackend.UnsupportedReason); return; }
        var options = new GhSkillInstallService.Options(Path: Destination, Overwrite: true);
        GhInstallOwnershipGuard.RequireVacantForceDestinations([Destination], options, Token);
        Payload("extra");
        var before = Bytes();
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.RequireVacantForceDestinations([Destination], options, Token));
        Assert.Equal(before, Bytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedOrNondirectoryDestinationCannotProveVacancy(bool link)
    {
        if (link && OperatingSystem.IsWindows()) { Assert.Skip("Unprivileged symbolic-link fixture requires POSIX."); return; }
        if (link) Directory.CreateSymbolicLink(Destination, Directory.CreateDirectory(Path.Combine(root.FullName, "empty-target")).FullName);
        else File.WriteAllText(Destination, "ordinary file");
        Assert.Throws<IOException>(() => GhInstallOwnershipGuard.RequireVacantForceDestinations([Destination],
            new(Path: Destination, Overwrite: true), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCliSeparatesRequestedForceFromEffectiveFreshInstallAndExactRefusal(bool occupied)
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Real CLI probe shell fixture requires POSIX."); return; }
        if (!occupied && !SecureRemovalBackend.IsSupported) { Assert.Skip("Native vacancy unavailable: " + SecureRemovalBackend.UnsupportedReason); return; }
        if (occupied) Payload("ignored"); else Directory.CreateDirectory(Destination);
        var before = Bytes(); var environment = EnvironmentMap(); _ = FakeGh(environment);
        var services = CliServices(environment);
        await SkillView.Tests.Cli.CliConsoleCapture.Gate.WaitAsync(Token);
        var originalOut = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try
        {
            var options = ArgParser.Parse("skillview", ["--scan-root", Destination, "install", "owner/repo", "demo",
                "--path", Destination, "--force", "--from-local", "--json"]);
            var exitCode = await CliDispatcher.RunAsync(options, services, Token);
            Assert.True(exitCode == (occupied ? ExitCodes.EnvironmentError : ExitCodes.Success),
                $"CLI exit {exitCode}; stdout: {output}; stderr: {error}");
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(!occupied, json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.True(json.RootElement.GetProperty("force").GetBoolean()); // Requested intent, not delegated authority.
            if (occupied)
            {
                Assert.Contains(Destination, json.RootElement.GetProperty("errorMessage").GetString());
                Assert.Contains("reviewed local-change disposition", json.RootElement.GetProperty("errorMessage").GetString());
            }
            else Assert.DoesNotContain(json.RootElement.GetProperty("commandLine").EnumerateArray(), argument => argument.GetString() == "--force");
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("added").ValueKind);
            Assert.False(json.RootElement.GetProperty("addedKnown").GetBoolean());
            Assert.Equal(!occupied, File.Exists(Marker)); Assert.Equal(before, Bytes());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); SkillView.Tests.Cli.CliConsoleCapture.Gate.Release(); }
    }
}
