namespace SkillView.Maintenance;

/// Display guidance only; saved revisions do not record how a tag was selected.
internal static class VersionUpdateGuidance
{
    internal const string FutureUpdates = "A later ordinary Update follows latest; no pin is created.";
    internal const string SharedConsumers = "All consumers of this physical installation are affected.";
    internal const string ExplicitTagChoice = "One-time tag choice. " + SharedConsumers + " " + FutureUpdates;
    internal const string UnpinnedUpdate = SharedConsumers + " " + FutureUpdates;
}
