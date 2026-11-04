namespace MtgSoloSports.SaveSnapshot;

public sealed record EvidenceTarget(
    int SeasonNumber,
    int SeasonId,
    int LeagueId,
    string LeagueName,
    string LeagueKind,
    int StageNumber,
    bool IsFirstStage,
    bool IsFinalStage,
    int CompletedRounds,
    int StageStandingsRows,
    int RoundRows,
    string StageChecksum,
    bool SeasonIsComplete,
    string EvidenceRef,
    string LeagueRef);
