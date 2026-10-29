using MtgSoloSports.Features.Reports.StandingsReport;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record SeasonDossierData(
    int SeasonNumber,
    int SeasonId,
    bool IsComplete,
    string Status,
    bool IsFinal,
    int CompletedStages,
    int TotalStages,
    int GlobalStage,
    string SeasonChecksum,
    int SeasonCount,
    SuperleagueSection Superleague,
    IReadOnlyList<StageWinnerEntry> StageProgression,
    IReadOnlyList<FeederSummaryEntry> FeederSummaries,
    PostseasonSection Postseason,
    CupSummaryEntry? ColorCup,
    CupSummaryEntry? TypeCup,
    HistorySection History,
    IReadOnlyList<CareerRecordEntry> CareerRecords);
