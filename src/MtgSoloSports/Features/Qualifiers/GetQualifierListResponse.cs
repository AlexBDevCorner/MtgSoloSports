namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Immutable read model listing all qualifier events for one transition.
/// </summary>
public sealed record GetQualifierListResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<GetQualifierEventResponse> Events);
