namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

/// <summary>
/// Lightweight progress snapshot for an explicit multi-season simulation,
/// suitable for a later UI progress indicator.
/// </summary>
public sealed record SimulateSeasonsProgress(
    int SeasonsRequested,
    int SeasonsCompleted,
    int StagesCompleted,
    int PostseasonStepsCompleted,
    int StartSeasonNumber,
    int EndSeasonNumber);
