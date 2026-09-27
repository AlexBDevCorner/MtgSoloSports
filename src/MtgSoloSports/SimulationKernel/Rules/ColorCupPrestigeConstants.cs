namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Versioned Rules v1 career-prestige constants for Color Cup team selection.
/// Raw prestige points per achievement (integer-only, fixed-point free):
/// feeder title 100, Superleague title 300, Superleague appearance 20,
/// stage win 10, stage second 5, stage third 2, other major honour 150.
/// Other major honours counts official Honour rows beyond feeder/Superleague
/// titles (future Color Cup individual/team and Type Cup team honours); zero
/// for pre-Cup saves, keeping the formula forward compatible.
/// Calibrated so prestige (10% weight) decides close calls without dominating
/// bonus (35%) or season performance (30%).
/// Stored in every save rules snapshot; existing saves never silently change.
/// </summary>
public sealed record ColorCupPrestigeConstants(
    int FeederTitlePoints,
    int SuperleagueTitlePoints,
    int SuperleagueAppearancePoints,
    int StageWinPoints,
    int StageSecondPoints,
    int StageThirdPoints,
    int OtherMajorHonourPoints)
{
    public static ColorCupPrestigeConstants Default { get; } = new(
        RulesV1.DefaultPrestigeFeederTitlePoints,
        RulesV1.DefaultPrestigeSuperleagueTitlePoints,
        RulesV1.DefaultPrestigeSuperleagueAppearancePoints,
        RulesV1.DefaultPrestigeStageWinPoints,
        RulesV1.DefaultPrestigeStageSecondPoints,
        RulesV1.DefaultPrestigeStageThirdPoints,
        RulesV1.DefaultPrestigeOtherMajorHonourPoints);

    /// <summary>
    /// Computes raw prestige from career counts. All arithmetic is checked
    /// integer-only; negative inputs abort (never silently clamped).
    /// </summary>
    public int ComputeRaw(
        int feederTitles,
        int superleagueTitles,
        int superleagueAppearances,
        int stageWins,
        int stageSeconds,
        int stageThirds,
        int otherMajorHonours)
    {
        if (feederTitles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(feederTitles));
        }

        if (superleagueTitles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(superleagueTitles));
        }

        if (superleagueAppearances < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(superleagueAppearances));
        }

        if (stageWins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageWins));
        }

        if (stageSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageSeconds));
        }

        if (stageThirds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageThirds));
        }

        if (otherMajorHonours < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(otherMajorHonours));
        }

        checked
        {
            return (feederTitles * FeederTitlePoints)
                + (superleagueTitles * SuperleagueTitlePoints)
                + (superleagueAppearances * SuperleagueAppearancePoints)
                + (stageWins * StageWinPoints)
                + (stageSeconds * StageSecondPoints)
                + (stageThirds * StageThirdPoints)
                + (otherMajorHonours * OtherMajorHonourPoints);
        }
    }
}
