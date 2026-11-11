namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Immutable presentation DTO for one athlete's career. Built from transactional
/// projections (never round payloads). All sporting values are fixed-point
/// thousandths integers.
/// <c>CurrentLeagueKey</c> is the stable cross-season league key
/// (<c>LeagueKey</c>) for the current league; null for pool/retired athletes
/// or when the league row is unavailable. Additive: absent on older payloads.
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
    int LastStageNumber,
    string? CurrentLeagueKey = null);
