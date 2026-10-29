namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record FeederTitleEntry(
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    string Level,
    bool IsComplete,
    AlmanacPodium? Champion);
