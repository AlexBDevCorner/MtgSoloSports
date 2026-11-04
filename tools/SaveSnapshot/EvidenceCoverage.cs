namespace MtgSoloSports.SaveSnapshot;

public sealed record EvidenceCoverage(
    int TargetStageNumber,
    int StagesPerSeason,
    int RoundsPerStage,
    int LeagueSize,
    IReadOnlyList<string> PayloadEncodings,
    int SeasonsInSnapshot,
    int StagesCoveredInSeason);
