using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

/// Baseline-less replacement from the same observed durable local source.
/// Archive metadata retains real observations separately from desired recipes.
internal sealed record ReplacementLocalImport(string SourcePath, string CanonicalSourcePath,
    SecureFileIdentity SourceRootIdentity, string InstalledArchiveFingerprint, string SourceArchiveFingerprint,
    SkillFrontMatter DesiredMetadata, string MetadataPolicy, ImmutableArray<PayloadMetadataRecipe> Metadata,
    GhLocalImportWitness Gh, string Disposition = "replace-local-retain-original");

internal sealed record GhLocalImportWitness(string BinaryPath, string BinarySha256, string Version,
    string InputPath, string OutputPath, string InputArchiveFingerprint, string OutputArchiveFingerprint,
    ImmutableArray<string> Arguments, string EnvironmentPolicy, string StdOut = "", string StdErr = "");

internal sealed record ReplacementLocalSourceObservation(string Status, string Message);
