namespace MtgSoloSports.SaveSnapshot;

public sealed record StageWinnerRef(
    int StageNumber,
    int AthleteId,
    string Name,
    int ChampionshipPointsThousandths,
    int SeasonNumber = 0);
