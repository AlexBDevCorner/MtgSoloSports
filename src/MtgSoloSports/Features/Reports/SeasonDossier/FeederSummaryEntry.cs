namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record FeederSummaryEntry(
    int LeagueId,
    string LeagueName,
    string Level,
    string Status,
    int CompletedStages,
    PodiumEntry? Champion,
    PodiumEntry? RunnerUp,
    PodiumEntry? Third);
