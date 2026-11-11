namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One season of an athlete's history for presentation.
/// LeagueLevel carries the tier identity ("Superleague", "Feeder1",
/// "Feeder2", "Feeder3") derived from league rows without parsing league
/// names (MSS-060); FeederDivision carries the persisted division (0 for
/// historical v1 single-feeder rows, which display as the original "Feeder").
/// Both are null for pool/inactive seasons.
/// LeagueKey carries the stable cross-season league key (<c>LeagueKey</c>)
/// for the season league; null for pool seasons or when the league row is
/// unavailable. Additive (MSS-138): absent on older payloads.
/// </summary>
public sealed record AthleteSeasonDto(
    int SeasonNumber,
    int SeasonId,
    bool WasActive,
    int? LeagueId,
    string? LeagueName,
    int? LeagueKind,
    int RoundWins,
    int StageWins,
    int StageSeconds,
    int StageThirds,
    int? SeasonRank,
    bool IsChampion,
    int EarnedBonusThousandths,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    string? LeagueLevel = null,
    int? FeederDivision = null,
    string? LeagueKey = null);
