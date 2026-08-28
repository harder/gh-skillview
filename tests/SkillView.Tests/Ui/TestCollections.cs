using Xunit;

namespace SkillView.Tests.Ui;

internal static class TestCollections
{
    internal const string TerminalGuiStaticState = "Terminal.Gui static state";
    internal const string ResourceStress = "Resource stress";
}

[CollectionDefinition(TestCollections.TerminalGuiStaticState, DisableParallelization = true)]
public sealed class TerminalGuiStaticStateCollection;

[CollectionDefinition(TestCollections.ResourceStress, DisableParallelization = true)]
public sealed class ResourceStressCollection;
