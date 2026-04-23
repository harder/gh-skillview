using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Gh.Models;

namespace SkillView.Gh;

/// AOT-safe JSON serializer context. All `gh` JSON types flow through here.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SearchResultSkill))]
[JsonSerializable(typeof(SearchResultSkill[]))]
[JsonSerializable(typeof(List<SearchResultSkill>))]
public partial class GhJsonContext : JsonSerializerContext
{
}
