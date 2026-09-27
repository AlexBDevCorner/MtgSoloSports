namespace MtgSoloSports.Features.Simulation.CompleteSeason;

/// <summary>
/// Lightweight progress snapshot for a fast season completion.
/// Built only from counts and stage cursors so a later UI progress indicator
/// can render without expensive animation payloads.
/// </summary>
public sealed record CompleteSeasonProgress(
    int StagesCompleted,
    int TotalStagesInSeason,
    int GlobalStageBefore,
    int GlobalStageAfter);
