namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record LeaderCareerEntry(
    int AthleteId,
    string AthleteName,
    int? CurrentRank,
    int CurrentPointsThousandths,
    int SuperleagueTitles,
    int FeederTitles,
    int TotalTitles,
    int SeasonsActive);
