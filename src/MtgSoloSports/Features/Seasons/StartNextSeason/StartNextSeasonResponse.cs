namespace MtgSoloSports.Features.Seasons.StartNextSeason;

/// <summary>
/// Immutable presentation DTO for starting the next season. Built only from
/// persisted rows after the current-season pointer advances; replay never
/// resimulates. Bonus aging is finalized: Stage 32 bonus enters the new season
/// at 80% decay with 80/60/40/20/0 weights from the save rules snapshot.
/// </summary>
public sealed record StartNextSeasonResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    int PreviousCurrentSeason,
    int NewCurrentSeason,
    string Phase,
    bool IsInauguralTransition,
    string ExpectedCup,
    int SuperleagueLeagueId,
    string SuperleagueLeagueName,
    IReadOnlyList<NextSeasonRosterEntry> Leagues,
    int ActiveAthletes,
    int PoolAthletes,
    int MovementCount,
    IReadOnlyList<int> BonusDecayWeightsThousandths);
