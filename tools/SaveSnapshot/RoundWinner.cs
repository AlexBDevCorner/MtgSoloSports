namespace MtgSoloSports.SaveSnapshot;

public sealed record RoundWinner(
    int RoundNumber,
    int AthleteId,
    string Name,
    int FinalThousandths,
    string FinalDisplay);
