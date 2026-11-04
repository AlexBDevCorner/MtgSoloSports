namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacChaosEditionEntry(
    int SourceSeasonNumber,
    string State,
    bool IsComplete,
    int FieldSize,
    string? ChampionName,
    int? ChampionAthleteId,
    IReadOnlyList<AlmanacCupPodium> FinalTopThree);
