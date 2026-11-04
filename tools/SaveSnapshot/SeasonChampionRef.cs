namespace MtgSoloSports.SaveSnapshot;

public sealed record SeasonChampionRef(
    int SeasonNumber,
    int AthleteId,
    string Name,
    int TotalChampionshipPointsThousandths);
