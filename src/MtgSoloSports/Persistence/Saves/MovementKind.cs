namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Kind of postseason league movement. MSS-014 persists only
/// <see cref="InauguralPromotion"/>; MSS-015 adds automatic Superleague
/// movement plus qualifier-candidate markers without rewriting existing rows.
/// MSS-017 adds feeder-rebalancing transfers between feeder leagues and their
/// color's common pool without rewriting existing rows.
/// Stored as an integer so future kinds extend the enum.
/// </summary>
public enum MovementKind
{
    InauguralPromotion = 0,

    /// <summary>
    /// Feeder-league champion (rank 1) automatically promoted to the next
    /// Superleague. Exactly eight per normal transition.
    /// </summary>
    AutomaticPromotion = 1,

    /// <summary>
    /// Superleague rank 25-32 automatically relegated to the returning
    /// sporting-color feeder. Exactly eight per normal transition.
    /// </summary>
    AutomaticRelegation = 2,

    /// <summary>
    /// Superleague rank 17-24 entering the qualifier as an incumbent.
    /// Exactly eight per normal transition; winners decided by MSS-016.
    /// </summary>
    QualifierIncumbent = 3,

    /// <summary>
    /// Feeder rank 2-4 entering the qualifier as a challenger.
    /// Exactly 24 per normal transition (3 per feeder); winners decided by MSS-016.
    /// </summary>
    QualifierChallenger = 4,

    /// <summary>
    /// Common-pool athlete drawn into its sporting-color feeder to restore 32.
    /// Equal-probability draw with the versioned simulation RNG (MSS-017).
    /// Former pool athletes are eligible immediately; no cooldown or weighting.
    /// </summary>
    RebalanceDraw = 5,

    /// <summary>
    /// Lowest-ranked retained feeder athlete displaced to its color's common
    /// pool to restore 32 when returning Superleague athletes overflow (MSS-017).
    /// Deterministic by previous feeder-season rank; consumes no RNG.
    /// </summary>
    RebalanceDisplacement = 6,

    /// <summary>
    /// Common-pool athlete seeded into a new F2/F3 division when a v1 save
    /// enters the tiered rules at a safe season boundary (MSS-057).
    /// Equal-probability deterministic draw with the versioned simulation RNG
    /// from the target season's pool; F1 is never demoted to populate new
    /// divisions. Exactly 512 per upgrade (32 F2 + 32 F3 per color).
    /// </summary>
    TierUpgradeSeed = 7,

    /// <summary>
    /// Feeder athlete automatically promoted across a feeder boundary (MSS-058):
    /// F2 ranks 1-8 → F1 or F3 ranks 1-8 → F2, 8 per color per boundary.
    /// Source/destination tiers are adjacent; sporting color never changes.
    /// </summary>
    FeederAutomaticPromotion = 8,

    /// <summary>
    /// Feeder athlete automatically relegated across a feeder boundary (MSS-058):
    /// F1 ranks 25-32 → F2 or F2 ranks 25-32 → F3, 8 per color per boundary.
    /// Adjacent tiers only; no division skipping.
    /// </summary>
    FeederAutomaticRelegation = 9,

    /// <summary>
    /// Feeder athlete entering a feeder qualifier as an incumbent (MSS-058):
    /// F1 ranks 17-24 or F2 ranks 17-24 defend, 8 per color per boundary.
    /// Winners decided by the 16-athlete qualifier.
    /// </summary>
    FeederQualifierIncumbent = 10,

    /// <summary>
    /// Feeder athlete entering a feeder qualifier as a challenger (MSS-058):
    /// F2 ranks 9-16 or F3 ranks 9-16 challenge, 8 per color per boundary.
    /// Winners decided by the 16-athlete qualifier.
    /// </summary>
    FeederQualifierChallenger = 11,

    /// <summary>
    /// Structural rebalance move upward between feeder tiers (MSS-059):
    /// F2 → F1 or F3 → F2 to repair a shortage cascade after Superleague
    /// color imbalance. Adjacent tiers only, same sporting color, never
    /// involving the common pool. Deterministic by source-season rank
    /// (best retained first, protected last); consumes no RNG.
    /// Distinct from competitive <see cref="FeederAutomaticPromotion"/>.
    /// </summary>
    RebalanceUp = 12,

    /// <summary>
    /// Structural rebalance move downward between feeder tiers (MSS-059):
    /// F1 → F2 or F2 → F3 to repair an overflow cascade. Adjacent tiers only,
    /// same sporting color, never involving the common pool. Deterministic by
    /// source-season rank (worst retained first, protected last); no RNG.
    /// Distinct from competitive <see cref="FeederAutomaticRelegation"/>.
    /// </summary>
    RebalanceDown = 13,
}
