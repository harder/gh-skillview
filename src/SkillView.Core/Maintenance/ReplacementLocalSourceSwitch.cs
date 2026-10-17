using System.Text.Json.Serialization;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record ReplacementLocalSourceSwitch(string PreviousRecordedPath,
    NativePlacementEvidence PreviousPlacement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CapturedSourceRecordedPath = null,
    string SourceRelationship = "selected-existing-local-source",
    string FutureUpdatePolicy = "explicit-local-reimport");
