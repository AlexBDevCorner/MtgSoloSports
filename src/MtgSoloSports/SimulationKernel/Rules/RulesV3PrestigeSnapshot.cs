namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Named carrier for the v3 tiered prestige model in quarter-prestige-points
/// (scale 4, MSS-152). The eighteen adjacent same-typed integers become
/// named record fields so a transposed value no longer compiles past review
/// as a plausible neighbour. Carried by <see cref="RulesV3"/> and inherited by
/// future RulesV4+ subclasses; not persisted.
/// </summary>
public sealed record RulesV3PrestigeSnapshot(
    int SuperTitlePoints,
    int Feeder1TitlePoints,
    int Feeder2TitlePoints,
    int Feeder3TitlePoints,
    int SuperAppearancePoints,
    int MajorCupTitlePoints,
    int SuperStageWinPoints,
    int SuperStageSecondPoints,
    int SuperStageThirdPoints,
    int Feeder1StageWinPoints,
    int Feeder1StageSecondPoints,
    int Feeder1StageThirdPoints,
    int Feeder2StageWinPoints,
    int Feeder2StageSecondPoints,
    int Feeder2StageThirdPoints,
    int Feeder3StageWinPoints,
    int Feeder3StageSecondPoints,
    int Feeder3StageThirdPoints)
{
    /// <summary>
    /// Canonical tiered prestige values applied when upgrading a v2 snapshot
    /// to v3 and used by V4-style subclasses that keep the tiered model.
    /// </summary>
    public static RulesV3PrestigeSnapshot Defaults { get; } = new(
        RulesV3.PrestigeSuperTitlePointsDefault,
        RulesV3.PrestigeFeeder1TitlePointsDefault,
        RulesV3.PrestigeFeeder2TitlePointsDefault,
        RulesV3.PrestigeFeeder3TitlePointsDefault,
        RulesV3.PrestigeSuperAppearancePointsDefault,
        RulesV3.PrestigeMajorCupTitlePointsDefault,
        RulesV3.PrestigeSuperStageWinPointsDefault,
        RulesV3.PrestigeSuperStageSecondPointsDefault,
        RulesV3.PrestigeSuperStageThirdPointsDefault,
        RulesV3.PrestigeFeeder1StageWinPointsDefault,
        RulesV3.PrestigeFeeder1StageSecondPointsDefault,
        RulesV3.PrestigeFeeder1StageThirdPointsDefault,
        RulesV3.PrestigeFeeder2StageWinPointsDefault,
        RulesV3.PrestigeFeeder2StageSecondPointsDefault,
        RulesV3.PrestigeFeeder2StageThirdPointsDefault,
        RulesV3.PrestigeFeeder3StageWinPointsDefault,
        RulesV3.PrestigeFeeder3StageSecondPointsDefault,
        RulesV3.PrestigeFeeder3StageThirdPointsDefault);
}
