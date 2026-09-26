namespace MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;

/// <summary>
/// One league's result within a bulk global-stage completion.
/// </summary>
public sealed record BulkLeagueStageResult(
    int LeagueId,
    string LeagueName,
    int StageNumber,
    string StageChecksum,
    int? NextStageNumber);
