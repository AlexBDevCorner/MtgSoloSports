namespace MtgSoloSports.SaveSnapshot;

public sealed record RoundPlacement(
    int AthleteId,
    string Name,
    int Position,
    int BaseThousandths,
    string BaseDisplay,
    int ActiveBonusThousandths,
    string ActiveBonusDisplay,
    int FinalThousandths,
    string FinalDisplay,
    int CumulativeAfterThousandths,
    string CumulativeAfterDisplay);
