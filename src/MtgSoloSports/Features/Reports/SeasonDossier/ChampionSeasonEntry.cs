namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record ChampionSeasonEntry(
    int SeasonNumber,
    PodiumEntry? Champion,
    bool IsComplete,
    bool HasSuperleague);
