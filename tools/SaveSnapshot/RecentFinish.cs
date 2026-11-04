namespace MtgSoloSports.SaveSnapshot;

public sealed record RecentFinish(
    int SeasonNumber,
    int StageNumber,
    int StageRank,
    int ChampionshipPointsThousandths,
    string ChampionshipPointsDisplay);
