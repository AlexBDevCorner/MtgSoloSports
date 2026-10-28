namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacCupEditionEntry(
    string Cup,
    int SourceSeasonNumber,
    string State,
    int TeamCount,
    IReadOnlyList<AlmanacCupPodium> Podium,
    string? IndividualChampionName,
    int? IndividualChampionAthleteId);
