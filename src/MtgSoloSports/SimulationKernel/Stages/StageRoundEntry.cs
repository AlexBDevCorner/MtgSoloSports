namespace MtgSoloSports.SimulationKernel.Stages;

/// <summary>
/// One athlete's finishing position within one round, as input to stage accumulation.
/// Pure kernel input: no persistence, HTTP or EF dependencies.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed record StageRoundEntry(
    int AthleteId,
    string Name,
    int Position,
    int BaseThousandths,
    int FinalThousandths);
