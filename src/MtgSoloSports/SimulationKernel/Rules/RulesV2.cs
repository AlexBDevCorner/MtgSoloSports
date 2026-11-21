using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Immutable versioned snapshot of Game Rules v2: the tiered feeder model.
/// Same sporting core as v1 (tables, counts, decay, cups, movement) but the
/// single integer <c>SuperleagueBonusMultiplier</c> is superseded by an exact
/// per-tier rational scale: Superleague 2/1, Feeder 1 1/1, Feeder 2 1/2,
/// Feeder 3 1/4. New saves use v2; v1 snapshots stay readable through the v1
/// compatibility path and are never reinterpreted as tiered.
/// All bonus arithmetic stays fixed-point integer-only with truncation
/// (multiply first, then integer-divide).
/// </summary>
public class RulesV2 : RulesV1
{
    public new const int RulesVersion = 2;

    public const int SuperleagueBonusNumeratorDefault = 2;
    public const int SuperleagueBonusDenominatorDefault = 1;
    public const int Feeder1BonusNumeratorDefault = 1;
    public const int Feeder1BonusDenominatorDefault = 1;
    public const int Feeder2BonusNumeratorDefault = 1;
    public const int Feeder2BonusDenominatorDefault = 2;
    public const int Feeder3BonusNumeratorDefault = 1;
    public const int Feeder3BonusDenominatorDefault = 4;

    public const int CupSuperleagueStrengthPermilleDefault = 1000;

    public const int CupFeeder1StrengthPermilleDefault = 800;

    public const int CupFeeder2StrengthPermilleDefault = 600;

    public const int CupFeeder3StrengthPermilleDefault = 400;

    protected RulesV2(
        RulesCoreSnapshot core,
        RulesV2SportingSnapshot sporting)
        : base(core)
    {
        ArgumentNullException.ThrowIfNull(sporting);
        SuperleagueBonusNumerator = sporting.SuperleagueBonusNumerator;
        SuperleagueBonusDenominator = sporting.SuperleagueBonusDenominator;
        Feeder1BonusNumerator = sporting.Feeder1BonusNumerator;
        Feeder1BonusDenominator = sporting.Feeder1BonusDenominator;
        Feeder2BonusNumerator = sporting.Feeder2BonusNumerator;
        Feeder2BonusDenominator = sporting.Feeder2BonusDenominator;
        Feeder3BonusNumerator = sporting.Feeder3BonusNumerator;
        Feeder3BonusDenominator = sporting.Feeder3BonusDenominator;
        CupSuperleagueStrengthPermille = sporting.CupSuperleagueStrengthPermille;
        CupFeeder1StrengthPermille = sporting.CupFeeder1StrengthPermille;
        CupFeeder2StrengthPermille = sporting.CupFeeder2StrengthPermille;
        CupFeeder3StrengthPermille = sporting.CupFeeder3StrengthPermille;
    }

    public override int Version => RulesVersion;

    public int SuperleagueBonusNumerator { get; }

    public int SuperleagueBonusDenominator { get; }

    public int Feeder1BonusNumerator { get; }

    public int Feeder1BonusDenominator { get; }

    public int Feeder2BonusNumerator { get; }

    public int Feeder2BonusDenominator { get; }

    public int Feeder3BonusNumerator { get; }

    public int Feeder3BonusDenominator { get; }

    /// <summary>
    /// Versioned Cup selection competition-strength factors in permille (MSS-064).
    /// Stored in every tiered save snapshot: Superleague 1000, F1 800, F2 600, F3 400.
    /// Softer than the bonus 2×/1×/1/2×/1/4× scale by design so tier strength is
    /// not counted three times at the same severity. Selection-rating only.
    /// </summary>
    public int CupSuperleagueStrengthPermille { get; }

    public int CupFeeder1StrengthPermille { get; }

    public int CupFeeder2StrengthPermille { get; }

    public int CupFeeder3StrengthPermille { get; }

    public override bool SupportsLevel(LeagueLevel level) => true;

    public override TierBonusScale GetBonusScale(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => new TierBonusScale(SuperleagueBonusNumerator, SuperleagueBonusDenominator),
        LeagueLevel.Feeder1 => new TierBonusScale(Feeder1BonusNumerator, Feeder1BonusDenominator),
        LeagueLevel.Feeder2 => new TierBonusScale(Feeder2BonusNumerator, Feeder2BonusDenominator),
        LeagueLevel.Feeder3 => new TierBonusScale(Feeder3BonusNumerator, Feeder3BonusDenominator),
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    public override int GetCupStrengthFactor(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => CupSuperleagueStrengthPermille,
        LeagueLevel.Feeder1 => CupFeeder1StrengthPermille,
        LeagueLevel.Feeder2 => CupFeeder2StrengthPermille,
        LeagueLevel.Feeder3 => CupFeeder3StrengthPermille,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    public static new RulesV2 CreateDefault() => Create(null);

    /// <summary>
    /// Upgrades a validated v1 snapshot to v2, preserving every sporting core
    /// value (tables, counts, decay, cups, prestige) and applying the canonical
    /// tier scales (Superleague 2/1, F1 1/1, F2 1/2, F3 1/4). Historical v1
    /// results keep the amount earned at their original tier and are never
    /// rescaled; only newly earned bonus uses the tiered scales.
    /// </summary>
    public static RulesV2 FromV1(RulesV1 v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.Validate();
        if (v1 is RulesV2 already)
        {
            return already;
        }

        RulesV2SportingSnapshot sporting = new(
            SuperleagueBonusNumerator: SuperleagueBonusNumeratorDefault,
            SuperleagueBonusDenominator: SuperleagueBonusDenominatorDefault,
            Feeder1BonusNumerator: Feeder1BonusNumeratorDefault,
            Feeder1BonusDenominator: Feeder1BonusDenominatorDefault,
            Feeder2BonusNumerator: Feeder2BonusNumeratorDefault,
            Feeder2BonusDenominator: Feeder2BonusDenominatorDefault,
            Feeder3BonusNumerator: Feeder3BonusNumeratorDefault,
            Feeder3BonusDenominator: Feeder3BonusDenominatorDefault,
            CupSuperleagueStrengthPermille: CupSuperleagueStrengthPermilleDefault,
            CupFeeder1StrengthPermille: CupFeeder1StrengthPermilleDefault,
            CupFeeder2StrengthPermille: CupFeeder2StrengthPermilleDefault,
            CupFeeder3StrengthPermille: CupFeeder3StrengthPermilleDefault);
        var candidate = new RulesV2(v1.ExtractCore(), sporting);
        candidate.Validate();
        return candidate;
    }

    public static RulesV2 Create(
        RulesV1Overrides? overrides,
        int superleagueNumerator = SuperleagueBonusNumeratorDefault,
        int superleagueDenominator = SuperleagueBonusDenominatorDefault,
        int feeder1Numerator = Feeder1BonusNumeratorDefault,
        int feeder1Denominator = Feeder1BonusDenominatorDefault,
        int feeder2Numerator = Feeder2BonusNumeratorDefault,
        int feeder2Denominator = Feeder2BonusDenominatorDefault,
        int feeder3Numerator = Feeder3BonusNumeratorDefault,
        int feeder3Denominator = Feeder3BonusDenominatorDefault,
        int cupSuperleagueStrengthPermille = CupSuperleagueStrengthPermilleDefault,
        int cupFeeder1StrengthPermille = CupFeeder1StrengthPermilleDefault,
        int cupFeeder2StrengthPermille = CupFeeder2StrengthPermilleDefault,
        int cupFeeder3StrengthPermille = CupFeeder3StrengthPermilleDefault)
    {
        RulesV1Overrides active = overrides ?? new RulesV1Overrides();
        RulesV1 core = RulesV1.Create(active);
        RulesV2SportingSnapshot sporting = new(
            SuperleagueBonusNumerator: superleagueNumerator,
            SuperleagueBonusDenominator: superleagueDenominator,
            Feeder1BonusNumerator: feeder1Numerator,
            Feeder1BonusDenominator: feeder1Denominator,
            Feeder2BonusNumerator: feeder2Numerator,
            Feeder2BonusDenominator: feeder2Denominator,
            Feeder3BonusNumerator: feeder3Numerator,
            Feeder3BonusDenominator: feeder3Denominator,
            CupSuperleagueStrengthPermille: cupSuperleagueStrengthPermille,
            CupFeeder1StrengthPermille: cupFeeder1StrengthPermille,
            CupFeeder2StrengthPermille: cupFeeder2StrengthPermille,
            CupFeeder3StrengthPermille: cupFeeder3StrengthPermille);
        var candidate = new RulesV2(core.ExtractCore(), sporting);
        candidate.Validate();
        return candidate;
    }

    /// <summary>
    /// Builds the named v2 sporting-extras carrier from this snapshot's
    /// properties (MSS-152); the single hand-checked copy path shared by
    /// every RulesV3 construction.
    /// </summary>
    internal RulesV2SportingSnapshot ExtractSportingSnapshot() => new(
        SuperleagueBonusNumerator: SuperleagueBonusNumerator,
        SuperleagueBonusDenominator: SuperleagueBonusDenominator,
        Feeder1BonusNumerator: Feeder1BonusNumerator,
        Feeder1BonusDenominator: Feeder1BonusDenominator,
        Feeder2BonusNumerator: Feeder2BonusNumerator,
        Feeder2BonusDenominator: Feeder2BonusDenominator,
        Feeder3BonusNumerator: Feeder3BonusNumerator,
        Feeder3BonusDenominator: Feeder3BonusDenominator,
        CupSuperleagueStrengthPermille: CupSuperleagueStrengthPermille,
        CupFeeder1StrengthPermille: CupFeeder1StrengthPermille,
        CupFeeder2StrengthPermille: CupFeeder2StrengthPermille,
        CupFeeder3StrengthPermille: CupFeeder3StrengthPermille);

    public override void Validate()
    {
        base.Validate();
        ValidateTierScale(
            nameof(SuperleagueBonusNumerator),
            SuperleagueBonusNumerator,
            SuperleagueBonusDenominator,
            SuperleagueBonusNumeratorDefault,
            SuperleagueBonusDenominatorDefault,
            LeagueLevel.Superleague);
        ValidateTierScale(
            nameof(Feeder1BonusNumerator),
            Feeder1BonusNumerator,
            Feeder1BonusDenominator,
            Feeder1BonusNumeratorDefault,
            Feeder1BonusDenominatorDefault,
            LeagueLevel.Feeder1);
        ValidateTierScale(
            nameof(Feeder2BonusNumerator),
            Feeder2BonusNumerator,
            Feeder2BonusDenominator,
            Feeder2BonusNumeratorDefault,
            Feeder2BonusDenominatorDefault,
            LeagueLevel.Feeder2);
        ValidateTierScale(
            nameof(Feeder3BonusNumerator),
            Feeder3BonusNumerator,
            Feeder3BonusDenominator,
            Feeder3BonusNumeratorDefault,
            Feeder3BonusDenominatorDefault,
            LeagueLevel.Feeder3);
        ValidateCupStrengthFactors();
    }

    private void ValidateCupStrengthFactors()
    {
        if (CupSuperleagueStrengthPermille <= 0
            || CupFeeder1StrengthPermille <= 0
            || CupFeeder2StrengthPermille <= 0
            || CupFeeder3StrengthPermille <= 0)
        {
            throw new InvalidOperationException(
                $"Cup competition-strength factors must all be positive, was " +
                $"{CupSuperleagueStrengthPermille}/{CupFeeder1StrengthPermille}/" +
                $"{CupFeeder2StrengthPermille}/{CupFeeder3StrengthPermille}.");
        }

        if (!(CupSuperleagueStrengthPermille > CupFeeder1StrengthPermille
            && CupFeeder1StrengthPermille > CupFeeder2StrengthPermille
            && CupFeeder2StrengthPermille > CupFeeder3StrengthPermille))
        {
            throw new InvalidOperationException(
                $"Cup competition-strength factors must satisfy Superleague > F1 > F2 > F3 > 0, was " +
                $"{CupSuperleagueStrengthPermille}/{CupFeeder1StrengthPermille}/" +
                $"{CupFeeder2StrengthPermille}/{CupFeeder3StrengthPermille}.");
        }

        if (CupSuperleagueStrengthPermille != CupSuperleagueStrengthPermilleDefault
            || CupFeeder1StrengthPermille != CupFeeder1StrengthPermilleDefault
            || CupFeeder2StrengthPermille != CupFeeder2StrengthPermilleDefault
            || CupFeeder3StrengthPermille != CupFeeder3StrengthPermilleDefault)
        {
            throw new InvalidOperationException(
                $"Cup competition-strength factors must be " +
                $"{CupSuperleagueStrengthPermilleDefault}/{CupFeeder1StrengthPermilleDefault}/" +
                $"{CupFeeder2StrengthPermilleDefault}/{CupFeeder3StrengthPermilleDefault} permille, was " +
                $"{CupSuperleagueStrengthPermille}/{CupFeeder1StrengthPermille}/" +
                $"{CupFeeder2StrengthPermille}/{CupFeeder3StrengthPermille}.");
        }
    }

    private static void ValidateTierScale(
        string name,
        int numerator,
        int denominator,
        int expectedNumerator,
        int expectedDenominator,
        LeagueLevel level)
    {
        if (denominator <= 0)
        {
            throw new InvalidOperationException($"{name} denominator must be positive, was {denominator}.");
        }

        if (numerator < 0)
        {
            throw new InvalidOperationException($"{name} numerator cannot be negative, was {numerator}.");
        }

        if (numerator != expectedNumerator || denominator != expectedDenominator)
        {
            throw new InvalidOperationException(
                $"{LeagueHierarchy.DisplayName(level)} bonus scale must be {expectedNumerator}/{expectedDenominator}, was {numerator}/{denominator}.");
        }
    }
}
