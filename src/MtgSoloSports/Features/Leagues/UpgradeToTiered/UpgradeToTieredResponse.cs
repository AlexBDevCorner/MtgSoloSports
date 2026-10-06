namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Immutable inspectable result of the v1-to-tiered sporting upgrade.
/// Built only from persisted rows (plus the committed RNG snapshot) so later
/// UI work can show created divisions, seeded athletes, remaining pools,
/// source/target rules, and checksum/RNG provenance without resimulation.
/// </summary>
public sealed record UpgradeToTieredResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int TargetSeasonNumber,
    int SourceRulesVersion,
    int TargetRulesVersion,
    IReadOnlyList<UpgradeTierLeague> CreatedLeagues,
    IReadOnlyList<UpgradeTierSeed> F2Seeds,
    IReadOnlyList<UpgradeTierSeed> F3Seeds,
    IReadOnlyList<UpgradePoolCount> RemainingPools,
    int PoolCount,
    int MovementCount,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    bool AlreadyApplied);
