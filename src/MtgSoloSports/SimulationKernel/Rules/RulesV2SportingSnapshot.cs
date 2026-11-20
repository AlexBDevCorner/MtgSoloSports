namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Named carrier for the v2 tiered sporting extras (exact per-tier rational
/// bonus scales plus Cup selection competition-strength factors, MSS-152).
/// Pairing each numerator with its denominator in one record makes the four
/// adjacent same-typed scale pairs transposition-proof at their single
/// construction site. Carried by <see cref="RulesV2"/> and inherited by
/// <see cref="RulesV3"/>; not persisted.
/// </summary>
public sealed record RulesV2SportingSnapshot(
    int SuperleagueBonusNumerator,
    int SuperleagueBonusDenominator,
    int Feeder1BonusNumerator,
    int Feeder1BonusDenominator,
    int Feeder2BonusNumerator,
    int Feeder2BonusDenominator,
    int Feeder3BonusNumerator,
    int Feeder3BonusDenominator,
    int CupSuperleagueStrengthPermille,
    int CupFeeder1StrengthPermille,
    int CupFeeder2StrengthPermille,
    int CupFeeder3StrengthPermille);
