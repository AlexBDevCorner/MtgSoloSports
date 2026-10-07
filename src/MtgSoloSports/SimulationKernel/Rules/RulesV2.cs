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
public sealed class RulesV2 : RulesV1
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

    private RulesV2(
        int sportingColorCount,
        int athletesPerSportingColor,
        int totalAthletesInSave,
        int regularLeagueCount,
        int leagueSize,
        int superleagueSize,
        int stagesPerSeason,
        int roundsPerStage,
        int qualifierSize,
        int qualifierRounds,
        int qualifierWinners,
        int superleagueSafeCount,
        int superleagueRelegatedCount,
        int superleagueQualifierIncumbentCount,
        int feederAutoPromotedCount,
        int feederQualifierCount,
        int inauguralQualifiedPerLeague,
        int colorCupColorCount,
        int colorCupTeamSize,
        int colorCupIndividualRounds,
        int colorCupTeamGroupRounds,
        int typeCupMinTeamSize,
        int typeCupGroupRounds,
        int typeCupTournamentFormatVersion,
        int typeCupMaxDirectFinalTeams,
        int typeCupFinalTeamCount,
        int recentFormStageCount,
        int cupBonusWeightPermille,
        int cupPerformanceWeightPermille,
        int cupFormWeightPermille,
        int cupPrestigeWeightPermille,
        ColorCupPrestigeConstants prestige,
        int[] scoringTable,
        int[] roundBonusThousandths,
        int[] stageBonusThousandths,
        int[] bonusAgeWeightsThousandths,
        int[] recentFormWeights,
        int superleagueBonusNumerator,
        int superleagueBonusDenominator,
        int feeder1BonusNumerator,
        int feeder1BonusDenominator,
        int feeder2BonusNumerator,
        int feeder2BonusDenominator,
        int feeder3BonusNumerator,
        int feeder3BonusDenominator)
        : base(
            sportingColorCount,
            athletesPerSportingColor,
            totalAthletesInSave,
            regularLeagueCount,
            leagueSize,
            superleagueSize,
            stagesPerSeason,
            roundsPerStage,
            qualifierSize,
            qualifierRounds,
            qualifierWinners,
            superleagueSafeCount,
            superleagueRelegatedCount,
            superleagueQualifierIncumbentCount,
            feederAutoPromotedCount,
            feederQualifierCount,
            inauguralQualifiedPerLeague,
            superleagueBonusMultiplier: 2,
            colorCupColorCount,
            colorCupTeamSize,
            colorCupIndividualRounds,
            colorCupTeamGroupRounds,
            typeCupMinTeamSize,
            typeCupGroupRounds,
            typeCupTournamentFormatVersion,
            typeCupMaxDirectFinalTeams,
            typeCupFinalTeamCount,
            recentFormStageCount,
            cupBonusWeightPermille,
            cupPerformanceWeightPermille,
            cupFormWeightPermille,
            cupPrestigeWeightPermille,
            prestige,
            scoringTable,
            roundBonusThousandths,
            stageBonusThousandths,
            bonusAgeWeightsThousandths,
            recentFormWeights)
    {
        SuperleagueBonusNumerator = superleagueBonusNumerator;
        SuperleagueBonusDenominator = superleagueBonusDenominator;
        Feeder1BonusNumerator = feeder1BonusNumerator;
        Feeder1BonusDenominator = feeder1BonusDenominator;
        Feeder2BonusNumerator = feeder2BonusNumerator;
        Feeder2BonusDenominator = feeder2BonusDenominator;
        Feeder3BonusNumerator = feeder3BonusNumerator;
        Feeder3BonusDenominator = feeder3BonusDenominator;
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

    public override bool SupportsLevel(LeagueLevel level) => true;

    public override TierBonusScale GetBonusScale(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => new TierBonusScale(SuperleagueBonusNumerator, SuperleagueBonusDenominator),
        LeagueLevel.Feeder1 => new TierBonusScale(Feeder1BonusNumerator, Feeder1BonusDenominator),
        LeagueLevel.Feeder2 => new TierBonusScale(Feeder2BonusNumerator, Feeder2BonusDenominator),
        LeagueLevel.Feeder3 => new TierBonusScale(Feeder3BonusNumerator, Feeder3BonusDenominator),
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

        var candidate = new RulesV2(
            v1.SportingColorCount,
            v1.AthletesPerSportingColor,
            v1.TotalAthletesInSave,
            v1.RegularLeagueCount,
            v1.LeagueSize,
            v1.SuperleagueSize,
            v1.StagesPerSeason,
            v1.RoundsPerStage,
            v1.QualifierSize,
            v1.QualifierRounds,
            v1.QualifierWinners,
            v1.SuperleagueSafeCount,
            v1.SuperleagueRelegatedCount,
            v1.SuperleagueQualifierIncumbentCount,
            v1.FeederAutoPromotedCount,
            v1.FeederQualifierCount,
            v1.InauguralQualifiedPerLeague,
            v1.ColorCupColorCount,
            v1.ColorCupTeamSize,
            v1.ColorCupIndividualRounds,
            v1.ColorCupTeamGroupRounds,
            v1.TypeCupMinTeamSize,
            v1.TypeCupGroupRounds,
            v1.TypeCupTournamentFormatVersion,
            v1.TypeCupMaxDirectFinalTeams,
            v1.TypeCupFinalTeamCount,
            v1.RecentFormStageCount,
            v1.CupBonusWeightPermille,
            v1.CupPerformanceWeightPermille,
            v1.CupFormWeightPermille,
            v1.CupPrestigeWeightPermille,
            v1.Prestige,
            [.. v1.ScoringTable],
            [.. v1.RoundBonusThousandths],
            [.. v1.StageBonusThousandths],
            [.. v1.BonusAgeWeightsThousandths],
            [.. v1.RecentFormWeights],
            SuperleagueBonusNumeratorDefault,
            SuperleagueBonusDenominatorDefault,
            Feeder1BonusNumeratorDefault,
            Feeder1BonusDenominatorDefault,
            Feeder2BonusNumeratorDefault,
            Feeder2BonusDenominatorDefault,
            Feeder3BonusNumeratorDefault,
            Feeder3BonusDenominatorDefault);
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
        int feeder3Denominator = Feeder3BonusDenominatorDefault)
    {
        RulesV1Overrides active = overrides ?? new RulesV1Overrides();
        RulesV1 core = RulesV1.Create(active);
        var candidate = new RulesV2(
            core.SportingColorCount,
            core.AthletesPerSportingColor,
            core.TotalAthletesInSave,
            core.RegularLeagueCount,
            core.LeagueSize,
            core.SuperleagueSize,
            core.StagesPerSeason,
            core.RoundsPerStage,
            core.QualifierSize,
            core.QualifierRounds,
            core.QualifierWinners,
            core.SuperleagueSafeCount,
            core.SuperleagueRelegatedCount,
            core.SuperleagueQualifierIncumbentCount,
            core.FeederAutoPromotedCount,
            core.FeederQualifierCount,
            core.InauguralQualifiedPerLeague,
            core.ColorCupColorCount,
            core.ColorCupTeamSize,
            core.ColorCupIndividualRounds,
            core.ColorCupTeamGroupRounds,
            core.TypeCupMinTeamSize,
            core.TypeCupGroupRounds,
            core.TypeCupTournamentFormatVersion,
            core.TypeCupMaxDirectFinalTeams,
            core.TypeCupFinalTeamCount,
            core.RecentFormStageCount,
            core.CupBonusWeightPermille,
            core.CupPerformanceWeightPermille,
            core.CupFormWeightPermille,
            core.CupPrestigeWeightPermille,
            core.Prestige,
            [.. core.ScoringTable],
            [.. core.RoundBonusThousandths],
            [.. core.StageBonusThousandths],
            [.. core.BonusAgeWeightsThousandths],
            [.. core.RecentFormWeights],
            superleagueNumerator,
            superleagueDenominator,
            feeder1Numerator,
            feeder1Denominator,
            feeder2Numerator,
            feeder2Denominator,
            feeder3Numerator,
            feeder3Denominator);
        candidate.Validate();
        return candidate;
    }

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
