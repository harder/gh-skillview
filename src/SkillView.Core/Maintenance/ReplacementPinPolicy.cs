using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

/// No remote resolution or invented pristine baseline: the archive is observed I.
internal sealed record ReplacementPinPolicy(SkillFrontMatter DesiredMetadata, string ArchiveFingerprint,
    string MetadataPolicy, ImmutableArray<PayloadMetadataRecipe> Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementOwnerTransition? Owner = null);
