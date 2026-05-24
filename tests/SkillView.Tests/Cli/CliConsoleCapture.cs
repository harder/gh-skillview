namespace SkillView.Tests.Cli;

internal static class CliConsoleCapture
{
    internal static SemaphoreSlim Gate { get; } = new(1, 1);
}
