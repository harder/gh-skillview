using System.Collections.Immutable;
using SkillView.Maintenance;

namespace SkillView.Ui;

internal enum LocalUpdateAction { Preserve, Review, Choose, Build }

internal sealed record LocalUpdateRequest(LocalUpdateAction Action, ReplacementResult Plan,
    LocalUpdateReview? Review = null, ImmutableArray<LocalPathChoice> Choices = default,
    ImmutableArray<LocalResolutionInput> Resolutions = default);

internal sealed record LocalUpdateResponse(ReplacementResult? Plan = null, LocalUpdateReview? Review = null);
