namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Search-result DTO tailored to the athlete browse page. Historical counts
/// are computed in the backend/query layer from authoritative persisted
/// history; the browser never loads detailed histories to calculate filters.
/// All sporting counts are plain integer counts (no bonus math).
/// </summary>
public sealed record AthleteSearchResultDto(
    int AthleteId,
    string Name,
    string? ImageUrl,
    string TypeLine,
    int SportingColor,
    string SportingColorName,
    IReadOnlyList<string> CreatureTypes,
    bool IsActive,
    string? CurrentLeagueName,
    int? CurrentLeagueKind,
    int NonPoolSeasons,
    int HonoursCount,
    int TitlesCount,
    int? BestSeasonFinish,
    int? BestSeasonNumber,
    bool EverSuperleague,
    int SuperSeasons,
    int CupAppearances,
    int CupPodiums,
    int CupTitles,
    string? CurrentLeagueLevel = null,
    int? CurrentLeagueDivision = null);
