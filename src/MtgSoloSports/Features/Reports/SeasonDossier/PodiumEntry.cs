namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record PodiumEntry(
    int AthleteId,
    string AthleteName,
    int PointsThousandths);
