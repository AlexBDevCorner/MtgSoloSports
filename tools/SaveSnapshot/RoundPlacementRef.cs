namespace MtgSoloSports.SaveSnapshot;

public sealed record RoundPlacementRef(
    int RoundNumber,
    int Position,
    int BaseThousandths,
    string BaseDisplay,
    int FinalThousandths,
    string FinalDisplay);
