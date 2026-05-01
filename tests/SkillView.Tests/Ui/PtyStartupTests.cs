using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace SkillView.Tests.Ui;

[Trait("Category", "PTY")]
public sealed class PtyStartupTests
{
    private static bool ShouldRun =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("SKILLVIEW_PTY_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void BuiltBinary_StartsInsidePty_AndRendersSearchView()
    {
        if (!ShouldRun)
        {
            return;
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var scriptPath = "/usr/bin/script";
        Assert.True(File.Exists(scriptPath), "PTY tests require /usr/bin/script.");

        var binaryPath = ResolveBuiltBinaryPath();
        Assert.True(File.Exists(binaryPath), $"Built binary not found: {binaryPath}");

        var root = Directory.CreateTempSubdirectory("skillview-pty-");
        var homeDir = Path.Combine(root.FullName, "home");
        Directory.CreateDirectory(homeDir);
        var transcriptPath = Path.Combine(root.FullName, "typescript.out");

        var startInfo = new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add(transcriptPath);
        startInfo.ArgumentList.Add(binaryPath);
        startInfo.ArgumentList.Add("--debug");

        var ghToken = ResolveGhToken();
        startInfo.Environment["GH_TOKEN"] = ghToken;
        startInfo.Environment["HOME"] = homeDir;
        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLUMNS"] = "120";
        startInfo.Environment["LINES"] = "40";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["SKILLVIEW_LOG"] = "debug";

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        string stderr = string.Empty;

        try
        {
            var logDir = Path.Combine(homeDir, "Library", "Caches", "SkillView", "logs");
            var ready = WaitFor(
                timeout: TimeSpan.FromSeconds(15),
                condition: () => HasStartupLog(logDir) && TranscriptShowsSearchView(transcriptPath),
                process: process);

            if (!ready)
            {
                stderr = process.StandardError.ReadToEnd();
            }

            Assert.True(ready, $"SkillView did not reach PTY startup readiness. stderr: {stderr}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            try
            {
                root.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup for opt-in test sandboxes.
            }
        }
    }

    private static bool WaitFor(TimeSpan timeout, Func<bool> condition, Process process)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static bool HasStartupLog(string logDir)
    {
        if (!Directory.Exists(logDir))
        {
            return false;
        }

        var latest = Directory.GetFiles(logDir, "skillview-*.log")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
        {
            return false;
        }

        var text = File.ReadAllText(latest);
        return text.Contains("filesystem scan found", StringComparison.Ordinal)
            || text.Contains("scan roots resolved", StringComparison.Ordinal);
    }

    private static bool TranscriptShowsSearchView(string transcriptPath)
    {
        if (!File.Exists(transcriptPath))
        {
            return false;
        }

        var text = File.ReadAllText(transcriptPath);
        return text.Contains("SkillView", StringComparison.Ordinal)
            && text.Contains("Query:", StringComparison.Ordinal);
    }

    private static string ResolveBuiltBinaryPath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (var rid in CandidateRids())
        {
            var path = Path.Combine(repoRoot, "src", "SkillView.App", "bin", "Debug", "net10.0", rid, "skillview");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(repoRoot, "src", "SkillView.App", "bin", "Debug", "net10.0", CandidateRids().First(), "skillview");
    }

    private static IEnumerable<string> CandidateRids()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            yield return "osx-arm64";
            yield return "osx-x64";
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            yield return "linux-x64";
            yield return "linux-arm64";
        }
    }

    private static string ResolveGhToken()
    {
        var token = System.Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            },
        };
        process.StartInfo.ArgumentList.Add("auth");
        process.StartInfo.ArgumentList.Add("token");
        process.Start();
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && output.Length > 0, "PTY tests require GH_TOKEN or a working `gh auth token`.");
        return output;
    }
}
