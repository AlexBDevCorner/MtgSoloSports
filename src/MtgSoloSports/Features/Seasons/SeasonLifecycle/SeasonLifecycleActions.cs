namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Stable legal next-action names exposed by the season lifecycle read model
/// and executed one-per-call by AdvanceToNextEvent. Each name maps to exactly
/// one focused feature operation so callers never need internal ordering.
/// </summary>
public static class SeasonLifecycleActions
{
    public const string CompleteNextGlobalStage = "CompleteNextGlobalStage";
    public const string ResolveInauguralMovement = "ResolveInauguralMovement";
    public const string ResolveAutomaticMovement = "ResolveAutomaticMovement";
    public const string RunQualifier = "RunQualifier";
    public const string RebalanceFeeders = "RebalanceFeeders";
    public const string SelectColorCup = "SelectColorCup";
    public const string RunColorCupIndividual = "RunColorCupIndividual";
    public const string RunColorCupTeam = "RunColorCupTeam";
    public const string SelectTypeCup = "SelectTypeCup";
    public const string RunTypeCupTeam = "RunTypeCupTeam";
    public const string StartNextSeason = "StartNextSeason";
}
