namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record ChaosChampionEntry(
    int AthleteId,
    string AthleteName,
    int FinalPlace,
    int FinalScoreThousandths,
    int RoundWins);
