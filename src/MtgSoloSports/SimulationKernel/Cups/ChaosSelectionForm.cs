namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure Chaos form component for Color/Type squad selection (MSS-105).
/// One outcome per athlete from the relevant completed source-season Chaos Cup
/// edition: the reached stage and, for finalists, the official Final rank.
/// Eliminated athletes score by exit stage only (no 16-round group-score sums,
/// no global 33..1024 ranks); finalists refine by official place 1..32.
/// Non-entrants and legacy seasons with no completed Chaos edition score zero,
/// so the tied component normalizes to 1000 and cannot distort ranking.
/// Pool invitation counts identically here, but pool athletes are never selection
/// candidates elsewhere, so pool inclusion grants no selection rights.
/// Integer-only, deterministic, no RNG/clock.
/// </summary>
public static class ChaosSelectionForm
{
    public const int NoChaosRaw = 0;
    public const int EliminatedStage0Raw = 10;
    public const int EliminatedStage1Raw = 20;
    public const int EliminatedStage2Raw = 30;
    public const int EliminatedStage3Raw = 40;
    public const int EliminatedStage4Raw = 50;
    public const int FinalBaseRaw = 60;

    public static int ForMissing() => NoChaosRaw;

    public static int ForElimination(int stageIndex) => stageIndex switch
    {
        0 => EliminatedStage0Raw,
        1 => EliminatedStage1Raw,
        2 => EliminatedStage2Raw,
        3 => EliminatedStage3Raw,
        4 => EliminatedStage4Raw,
        _ => throw new InvalidOperationException($"Chaos exit stage {stageIndex} is corrupt; only stages 0..4 eliminate athletes."),
    };

    public static int ForFinal(int finalRank)
    {
        if (finalRank < 1 || finalRank > ChaosCupFormat.FinalFieldSize)
        {
            throw new InvalidOperationException($"Chaos Final rank {finalRank} is corrupt; must be 1..{ChaosCupFormat.FinalFieldSize}.");
        }

        checked
        {
            return FinalBaseRaw + (ChaosCupFormat.FinalFieldSize - finalRank);
        }
    }
}
