using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class LinuxExposureOwnerLifetimeTests
{
    [Theory]
    [InlineData("dispose")]
    [InlineData("retained-dispose")]
    [InlineData("abandon")]
    [InlineData("foreign-finalizer")]
    [InlineData("idle")]
    [InlineData("absolute")]
    [InlineData("partial-init")]
    [InlineData("command-failure")]
    [InlineData("evidence-mismatch")]
    [InlineData("canceled-command")]
    public async Task NativeOwnerSubprocessReleasesOnlyItsOwnDescriptors(string scenario)
    {
        if (!OperatingSystem.IsLinux() || System.Environment.GetEnvironmentVariable("SKILLVIEW_LINUX_EXPOSURE_PRESERVATION_PROOF") != "1")
        { Assert.Skip("Opt-in native owner subprocess proof was not requested; no cleanup/table-isolation evidence."); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SkillView.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(repository.FullName, "tests", "SkillView.MaintenanceProof", "bin", configuration, "net10.0", "SkillView.MaintenanceProof");
        Assert.True(File.Exists(host), "Required proof flag needs a prebuilt scoped MaintenanceProof host: " + host);
        var root = Directory.CreateTempSubdirectory("skillview-owner-lifetime-");
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home"));
            var state = Directory.CreateDirectory(Path.Combine(root.FullName, "state"));
            File.SetUnixFileMode(home.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(state.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(root.FullName, "disposable-owner-marker"), "SkillView disposable native owner proof\n");
            var start = new ProcessStartInfo(host) { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--exposure-lifetime-proof"); start.ArgumentList.Add(root.FullName);
            foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("GIT_", StringComparison.Ordinal)
                || x.StartsWith("SKILLVIEW_", StringComparison.Ordinal) || x.StartsWith("GH_", StringComparison.Ordinal) || x.StartsWith("GITHUB_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            start.Environment["HOME"] = home.FullName; start.Environment["USERPROFILE"] = home.FullName;
            start.Environment["TMPDIR"] = root.FullName; start.Environment["SKILLVIEW_STATE_HOME"] = state.FullName;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home.FullName, "config");
            start.Environment["XDG_STATE_HOME"] = Path.Combine(home.FullName, "state");
            start.Environment["SKILLVIEW_EXPOSURE_LIFETIME_SCENARIO"] = scenario;
            // A pending CLR diagnostic FIFO open can reserve an invisible leader FD slot.
            // The all-descriptors-controlled finalizer fixture needs ordinary allocatable slots.
            if (scenario == "foreign-finalizer") start.Environment["DOTNET_EnableDiagnostics"] = "0";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try { await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            var output = await stdout; var error = await stderr;
            TestContext.Current.TestOutputHelper!.WriteLine(output + error);
            Assert.True(process.ExitCode == 0, $"{scenario} exit{process.ExitCode}: {output} {error}");
            using var report = JsonDocument.Parse(output);
            var value = report.RootElement;
            Assert.Equal(scenario, value.GetProperty("Scenario").GetString());
            Assert.True(value.GetProperty("OwnerCleanupCompleted").GetBoolean());
            Assert.True(value.GetProperty("WriterReleased").GetBoolean());
            Assert.True(value.GetProperty("OwnedDescriptorsClosed").GetBoolean());
            Assert.Equal(value.GetProperty("ResourcesOpened").GetInt32(), value.GetProperty("ResourcesReleased").GetInt32());
            if (scenario is not ("partial-init" or "evidence-mismatch"))
            {
                Assert.True(value.GetProperty("ForeignTableDiverged").GetBoolean());
                Assert.True(value.GetProperty("ConflictingSentinelsPreserved").GetInt32() >= 5);
            }
            if (scenario is "abandon" or "foreign-finalizer") Assert.True(value.GetProperty("ProxyFinalized").GetBoolean());
            if (scenario == "retained-dispose")
            {
                Assert.False(value.GetProperty("TerminalMoveState").GetProperty("LiveContinuity").GetBoolean());
                Assert.Equal((int)SkillView.Inventory.LinuxExposureLinkPosition.Retained, value.GetProperty("TerminalMoveState").GetProperty("Position").GetInt32());
            }
            if (scenario == "foreign-finalizer")
            {
                Assert.True(value.GetProperty("FinalizerTableObserved").GetBoolean());
                Assert.True(value.GetProperty("RuntimeDiagnosticsDisabled").GetBoolean());
                Assert.NotEqual(value.GetProperty("OwnerTask").GetString(), value.GetProperty("FinalizerTask").GetString());
                Assert.Equal(value.GetProperty("ConflictingSentinelsPreserved").GetInt32(), value.GetProperty("DescriptorWitnesses").GetArrayLength());
            }
        }
        finally { root.Delete(true); }
    }
}
