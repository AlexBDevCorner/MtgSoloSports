namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacSeasonEntry(
    int SeasonNumber,
    bool IsComplete,
    bool HasSuperleague,
    string? SuperleagueChampionName,
    int? SuperleagueChampionAthleteId,
    string ColorCupState,
    string TypeCupState);
