namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record StageWinnerEntry(
    int StageNumber,
    int? WinnerAthleteId,
    string WinnerName,
    int WinnerPointsThousandths,
    bool IsComplete);
