namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record CupPodiumEntry(
    int Rank,
    string TeamKey,
    string TeamName,
    int ScoreThousandths,
    string Medal,
    int? AthleteId = null,
    string? AthleteName = null);
