namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record IndividualCupSummary(
    string? ChampionName,
    int? ChampionAthleteId,
    IReadOnlyList<CupPodiumEntry> TopThree);
