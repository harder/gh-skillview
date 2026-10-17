using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Tests.Ui;
using Xunit;

namespace SkillView.Tests.Subprocess;

[Collection(TestCollections.ResourceStress)]
public sealed class FrozenProcessContextTests
{
    [Fact]
    public async Task AddedAmbientKeyCannotEnterGuardLookupNestedContextOrChild()
    {
        // This isolated test process changes a unique key only. Production
        // context capture never changes its caller's environment.
        var name = "GIT_SKILLVIEW_FROZEN_ABSENCE_" + Guid.NewGuid().ToString("N");
        var runner = new ProcessRunner(new Logger());
        var frozen = runner.WithContext(System.Environment.CurrentDirectory, new Dictionary<string, string>());
        Assert.Null(System.Environment.GetEnvironmentVariable(name));
        try
        {
            System.Environment.SetEnvironmentVariable(name, "added-after-capture");
            Assert.Equal("added-after-capture", runner.GetEffectiveEnvironmentVariable(name));
            Assert.Null(frozen.GetEffectiveEnvironmentVariable(name));
            var nested = frozen.WithContext(System.Environment.CurrentDirectory, new Dictionary<string, string>()).WithCaptureLimit(128);
            Assert.Null(nested.GetEffectiveEnvironmentVariable(name));
            var command = OperatingSystem.IsWindows()
                ? ("pwsh", new[] { "-NoProfile", "-Command", $"[Console]::Write([Environment]::GetEnvironmentVariable('{name}'))" })
                : ("/bin/sh", new[] { "-c", "printenv \"$1\" || true", "frozen-context", name });
            var captured = await nested.RunAsync(command.Item1, command.Item2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(captured.Succeeded, captured.StdErr); Assert.Equal("", captured.StdOut);
            var ordinary = await runner.RunAsync(command.Item1, command.Item2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(ordinary.Succeeded, ordinary.StdErr); Assert.Equal("added-after-capture", ordinary.StdOut.TrimEnd());
        }
        finally { System.Environment.SetEnvironmentVariable(name, null); }
    }
}
