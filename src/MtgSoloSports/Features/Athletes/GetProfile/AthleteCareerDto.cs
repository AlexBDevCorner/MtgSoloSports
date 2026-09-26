namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Immutable presentation DTO for one athlete's career. Built from transactional
/// projections (never round payloads). All sporting values are fixed-point
/// thousandths integers.
/// </summary>
public sealed record AthleteCareerDto(
    int SeasonsActive,
    bool IsActive,
    int? CurrentLeagueId,
    string? CurrentLeagueName,
    int? CurrentLeagueKind,
    int RoundWins,
    int StageWins,
    int StageSeconds,
    int StageThirds,
    int StagePodiums,
    int? BestSeasonFinish,
    int? BestSeasonNumber,
    int LifetimeEarnedBonusThousandths,
    int CurrentEffectiveBonusThousandths,
    int LastSeasonNumber,
    int LastStageNumber);
