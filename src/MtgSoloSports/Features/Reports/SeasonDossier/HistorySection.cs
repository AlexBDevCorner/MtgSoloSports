namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record HistorySection(
    int? PreviousSeasonNumber,
    PodiumEntry? PreviousChampion,
    PodiumEntry? DefendingChampion,
    IReadOnlyList<ChampionSeasonEntry> ChampionsLastFive,
    IReadOnlyList<LeaderCareerEntry> LeaderCareers,
    IReadOnlyList<LeaderCareerEntry> CupPerformerCareers);
