namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Immutable result for running all remaining qualifiers in canonical order.
/// Lists already-completed events (resumed, not rerun) and events executed now.
/// </summary>
public sealed record RunAllQualifiersResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    int TotalStandings,
    int TotalRounds,
    IReadOnlyList<string> AlreadyCompleted,
    IReadOnlyList<string> ExecutedNow);
