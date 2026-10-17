using System.Text;
using SkillView.Inventory;
using YamlDotNet.Core;

namespace SkillView.Maintenance;

/// Binds provider SKILL bytes to the independently captured *same selected*
/// source. This never compares an installed old revision with a new revision.
internal static class SelectedSkillSourceComparison
{
    internal static void Require(byte[] source, byte[] output, int? sourceMode, int? outputMode)
    {
        if (sourceMode != outputMode) throw new IOException("Provider authored SKILL.md mode differs from the selected source.");
        try
        {
            var before = Normalize(source);
            var after = Normalize(output);
            if (before == after) return;
            // This existing conservative parser permits only the documented
            // YAML map presentation and body-delimiter serialization effects.
            // Unknown/ambiguous scalar coercion never authorizes a difference.
            if (GhPresentationComparison.Fingerprint(before) is { } expected
                && GhPresentationComparison.Fingerprint(after) == expected) return;
        }
        catch (Exception ex) when (ex is YamlException or DecoderFallbackException)
        { throw new IOException("Selected source/provider SKILL.md has unsupported YAML or UTF-8.", ex); }
        throw new IOException("Provider authored SKILL.md metadata/body differs from the independently selected source, or its presentation is unsupported.");
    }

    private static string Normalize(byte[] bytes)
    {
        if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Selected SKILL.md exceeds its byte limit.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        // Validate the original header first: removing tracking fields must
        // never turn invalid/diagnostic YAML into an admissible authored skill.
        var (_, metadata, valid) = FrontMatterParser.Parse(text);
        if (!valid || !metadata.Diagnostics.IsEmpty)
            throw new IOException("Selected source/provider SKILL.md metadata is invalid before installer normalization.");
        return InstallerMetadataNormalizer.Normalize(text);
    }
}
