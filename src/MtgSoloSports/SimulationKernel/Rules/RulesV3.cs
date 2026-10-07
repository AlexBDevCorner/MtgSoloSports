using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Immutable versioned snapshot of Game Rules v3: tiered career prestige.
/// Same sporting core as v2 (tables, counts, decay, cups, movement, tier bonus
/// scales, competition-strength factors) plus an exact integer prestige model
/// that represents competition level: Superleague titles outrank Feeder 1/2/3,
/// and stage podiums scale 2x/1x/1/2x/1/4x around the Feeder 1 baseline.
/// All prestige arithmetic uses quarter-prestige-points (scale 4) with checked
/// integer-only math; no double/float/decimal appears in sporting math.
/// New saves use v3; v1 and v2 snapshots stay readable through the versioned
/// compatibility path and are never reinterpreted as tiered prestige.
/// Historical v1 feeder achievements map to Feeder 1 via
/// <see cref="LeagueHierarchy.LevelForDivision"/> when evaluated under v3.
/// </summary>
public sealed class RulesV3 : RulesV2
{
    public new const int RulesVersion = 3;

    /// <summary>
    /// Exact integer prestige unit scale: one semantic prestige point equals
    /// four scaled points. Fractional semantic values (F2/F3 stage 2nd/3rd)
    /// become exact integers (10/5/2 are quarter-point-exact).
    /// </summary>
    public const int PrestigeScale = 4;

    public const int PrestigeSuperTitlePointsDefault = 1200;

    public const int PrestigeFeeder1TitlePointsDefault = 400;

    public const int PrestigeFeeder2TitlePointsDefault = 200;

    public const int PrestigeFeeder3TitlePointsDefault = 100;

    public const int PrestigeSuperAppearancePointsDefault = 80;

    public const int PrestigeMajorCupTitlePointsDefault = 600;

    public const int PrestigeSuperStageWinPointsDefault = 80;

    public const int PrestigeSuperStageSecondPointsDefault = 40;

    public const int PrestigeSuperStageThirdPointsDefault = 16;

    public const int PrestigeFeeder1StageWinPointsDefault = 40;

    public const int PrestigeFeeder1StageSecondPointsDefault = 20;

    public const int PrestigeFeeder1StageThirdPointsDefault = 8;

    public const int PrestigeFeeder2StageWinPointsDefault = 20;

    public const int PrestigeFeeder2StageSecondPointsDefault = 10;

    public const int PrestigeFeeder2StageThirdPointsDefault = 4;

    public const int PrestigeFeeder3StageWinPointsDefault = 10;

    public const int PrestigeFeeder3StageSecondPointsDefault = 5;

    public const int PrestigeFeeder3StageThirdPointsDefault = 2;

    private RulesV3(
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
        int feeder3BonusDenominator,
        int cupSuperleagueStrengthPermille,
        int cupFeeder1StrengthPermille,
        int cupFeeder2StrengthPermille,
        int cupFeeder3StrengthPermille,
        int prestigeSuperTitlePoints,
        int prestigeFeeder1TitlePoints,
        int prestigeFeeder2TitlePoints,
        int prestigeFeeder3TitlePoints,
        int prestigeSuperAppearancePoints,
        int prestigeMajorCupTitlePoints,
        int prestigeSuperStageWinPoints,
        int prestigeSuperStageSecondPoints,
        int prestigeSuperStageThirdPoints,
        int prestigeFeeder1StageWinPoints,
        int prestigeFeeder1StageSecondPoints,
        int prestigeFeeder1StageThirdPoints,
        int prestigeFeeder2StageWinPoints,
        int prestigeFeeder2StageSecondPoints,
        int prestigeFeeder2StageThirdPoints,
        int prestigeFeeder3StageWinPoints,
        int prestigeFeeder3StageSecondPoints,
        int prestigeFeeder3StageThirdPoints)
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
            recentFormWeights,
            superleagueBonusNumerator,
            superleagueBonusDenominator,
            feeder1BonusNumerator,
            feeder1BonusDenominator,
            feeder2BonusNumerator,
            feeder2BonusDenominator,
            feeder3BonusNumerator,
            feeder3BonusDenominator,
            cupSuperleagueStrengthPermille,
            cupFeeder1StrengthPermille,
            cupFeeder2StrengthPermille,
            cupFeeder3StrengthPermille)
    {
        PrestigeSuperTitlePoints = prestigeSuperTitlePoints;
        PrestigeFeeder1TitlePoints = prestigeFeeder1TitlePoints;
        PrestigeFeeder2TitlePoints = prestigeFeeder2TitlePoints;
        PrestigeFeeder3TitlePoints = prestigeFeeder3TitlePoints;
        PrestigeSuperAppearancePoints = prestigeSuperAppearancePoints;
        PrestigeMajorCupTitlePoints = prestigeMajorCupTitlePoints;
        PrestigeSuperStageWinPoints = prestigeSuperStageWinPoints;
        PrestigeSuperStageSecondPoints = prestigeSuperStageSecondPoints;
        PrestigeSuperStageThirdPoints = prestigeSuperStageThirdPoints;
        PrestigeFeeder1StageWinPoints = prestigeFeeder1StageWinPoints;
        PrestigeFeeder1StageSecondPoints = prestigeFeeder1StageSecondPoints;
        PrestigeFeeder1StageThirdPoints = prestigeFeeder1StageThirdPoints;
        PrestigeFeeder2StageWinPoints = prestigeFeeder2StageWinPoints;
        PrestigeFeeder2StageSecondPoints = prestigeFeeder2StageSecondPoints;
        PrestigeFeeder2StageThirdPoints = prestigeFeeder2StageThirdPoints;
        PrestigeFeeder3StageWinPoints = prestigeFeeder3StageWinPoints;
        PrestigeFeeder3StageSecondPoints = prestigeFeeder3StageSecondPoints;
        PrestigeFeeder3StageThirdPoints = prestigeFeeder3StageThirdPoints;
    }

    public override int Version => RulesVersion;

    /// <summary>Scaled prestige per Superleague championship: 300 semantic x 4.</summary>
    public int PrestigeSuperTitlePoints { get; }

    /// <summary>Scaled prestige per Feeder 1 championship: 100 semantic x 4.</summary>
    public int PrestigeFeeder1TitlePoints { get; }

    /// <summary>Scaled prestige per Feeder 2 championship: 50 semantic x 4.</summary>
    public int PrestigeFeeder2TitlePoints { get; }

    /// <summary>Scaled prestige per Feeder 3 championship: 25 semantic x 4.</summary>
    public int PrestigeFeeder3TitlePoints { get; }

    /// <summary>Scaled prestige per completed Superleague season: 20 semantic x 4.</summary>
    public int PrestigeSuperAppearancePoints { get; }

    /// <summary>Scaled prestige per official major Cup championship: 150 semantic x 4.</summary>
    public int PrestigeMajorCupTitlePoints { get; }

    public int PrestigeSuperStageWinPoints { get; }

    public int PrestigeSuperStageSecondPoints { get; }

    public int PrestigeSuperStageThirdPoints { get; }

    public int PrestigeFeeder1StageWinPoints { get; }

    public int PrestigeFeeder1StageSecondPoints { get; }

    public int PrestigeFeeder1StageThirdPoints { get; }

    public int PrestigeFeeder2StageWinPoints { get; }

    public int PrestigeFeeder2StageSecondPoints { get; }

    public int PrestigeFeeder2StageThirdPoints { get; }

    public int PrestigeFeeder3StageWinPoints { get; }

    public int PrestigeFeeder3StageSecondPoints { get; }

    public int PrestigeFeeder3StageThirdPoints { get; }

    /// <summary>
    /// Scaled prestige for a league championship at an explicit level.
    /// Historical single-feeder v1 competition maps to Feeder 1 before calling.
    /// </summary>
    public int GetTieredTitlePrestige(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => PrestigeSuperTitlePoints,
        LeagueLevel.Feeder1 => PrestigeFeeder1TitlePoints,
        LeagueLevel.Feeder2 => PrestigeFeeder2TitlePoints,
        LeagueLevel.Feeder3 => PrestigeFeeder3TitlePoints,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    /// <summary>
    /// Scaled prestige for one league stage podium (place 1..3) at an explicit level.
    /// F1 is the baseline (40/20/8); Superleague doubles it, F2 halves it,
    /// F3 quarters it, all exact in quarter-points.
    /// </summary>
    public int GetTieredStagePrestige(LeagueLevel level, int place) => (level, place) switch
    {
        (LeagueLevel.Superleague, 1) => PrestigeSuperStageWinPoints,
        (LeagueLevel.Superleague, 2) => PrestigeSuperStageSecondPoints,
        (LeagueLevel.Superleague, 3) => PrestigeSuperStageThirdPoints,
        (LeagueLevel.Feeder1, 1) => PrestigeFeeder1StageWinPoints,
        (LeagueLevel.Feeder1, 2) => PrestigeFeeder1StageSecondPoints,
        (LeagueLevel.Feeder1, 3) => PrestigeFeeder1StageThirdPoints,
        (LeagueLevel.Feeder2, 1) => PrestigeFeeder2StageWinPoints,
        (LeagueLevel.Feeder2, 2) => PrestigeFeeder2StageSecondPoints,
        (LeagueLevel.Feeder2, 3) => PrestigeFeeder2StageThirdPoints,
        (LeagueLevel.Feeder3, 1) => PrestigeFeeder3StageWinPoints,
        (LeagueLevel.Feeder3, 2) => PrestigeFeeder3StageSecondPoints,
        (LeagueLevel.Feeder3, 3) => PrestigeFeeder3StageThirdPoints,
        (_, 1 or 2 or 3) => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
        _ => throw new ArgumentOutOfRangeException(nameof(place), $"Stage place {place} is not a podium."),
    };

    public static new RulesV3 CreateDefault() => Create(null);

    /// <summary>
    /// Upgrades a validated v1 snapshot to v3, preserving every sporting core
    /// value and applying the canonical tier scales, competition-strength
    /// factors and tiered prestige model. Historical results keep the amount
    /// earned at their original tier and are never rescaled.
    /// </summary>
    public static new RulesV3 FromV1(RulesV1 v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.Validate();
        if (v1 is RulesV3 already)
        {
            return already;
        }

        RulesV2 tiered = v1 is RulesV2 v2 ? v2 : RulesV2.FromV1(v1);
        return FromV2(tiered);
    }

    /// <summary>
    /// Upgrades a validated v2 snapshot to v3, preserving every sporting core
    /// value and applying the tiered prestige model. No historical prestige
    /// is rewritten; only future calculations use the new model.
    /// </summary>
    public static RulesV3 FromV2(RulesV2 v2)
    {
        ArgumentNullException.ThrowIfNull(v2);
        v2.Validate();
        if (v2 is RulesV3 already)
        {
            return already;
        }

        var candidate = new RulesV3(
            v2.SportingColorCount, v2.AthletesPerSportingColor, v2.TotalAthletesInSave,
            v2.RegularLeagueCount, v2.LeagueSize, v2.SuperleagueSize,
            v2.StagesPerSeason, v2.RoundsPerStage, v2.QualifierSize,
            v2.QualifierRounds, v2.QualifierWinners, v2.SuperleagueSafeCount,
            v2.SuperleagueRelegatedCount, v2.SuperleagueQualifierIncumbentCount,
            v2.FeederAutoPromotedCount, v2.FeederQualifierCount, v2.InauguralQualifiedPerLeague,
            v2.ColorCupColorCount, v2.ColorCupTeamSize, v2.ColorCupIndividualRounds,
            v2.ColorCupTeamGroupRounds, v2.TypeCupMinTeamSize, v2.TypeCupGroupRounds,
            v2.TypeCupTournamentFormatVersion, v2.TypeCupMaxDirectFinalTeams, v2.TypeCupFinalTeamCount,
            v2.RecentFormStageCount, v2.CupBonusWeightPermille, v2.CupPerformanceWeightPermille,
            v2.CupFormWeightPermille, v2.CupPrestigeWeightPermille, v2.Prestige,
            [.. v2.ScoringTable], [.. v2.RoundBonusThousandths], [.. v2.StageBonusThousandths],
            [.. v2.BonusAgeWeightsThousandths], [.. v2.RecentFormWeights],
            v2.SuperleagueBonusNumerator, v2.SuperleagueBonusDenominator,
            v2.Feeder1BonusNumerator, v2.Feeder1BonusDenominator,
            v2.Feeder2BonusNumerator, v2.Feeder2BonusDenominator,
            v2.Feeder3BonusNumerator, v2.Feeder3BonusDenominator,
            v2.CupSuperleagueStrengthPermille, v2.CupFeeder1StrengthPermille,
            v2.CupFeeder2StrengthPermille, v2.CupFeeder3StrengthPermille,
            PrestigeSuperTitlePointsDefault, PrestigeFeeder1TitlePointsDefault,
            PrestigeFeeder2TitlePointsDefault, PrestigeFeeder3TitlePointsDefault,
            PrestigeSuperAppearancePointsDefault, PrestigeMajorCupTitlePointsDefault,
            PrestigeSuperStageWinPointsDefault, PrestigeSuperStageSecondPointsDefault,
            PrestigeSuperStageThirdPointsDefault, PrestigeFeeder1StageWinPointsDefault,
            PrestigeFeeder1StageSecondPointsDefault, PrestigeFeeder1StageThirdPointsDefault,
            PrestigeFeeder2StageWinPointsDefault, PrestigeFeeder2StageSecondPointsDefault,
            PrestigeFeeder2StageThirdPointsDefault, PrestigeFeeder3StageWinPointsDefault,
            PrestigeFeeder3StageSecondPointsDefault, PrestigeFeeder3StageThirdPointsDefault);
        candidate.Validate();
        return candidate;
    }

    public static RulesV3 Create(
        RulesV1Overrides? overrides,
        int superleagueNumerator = RulesV2.SuperleagueBonusNumeratorDefault,
        int superleagueDenominator = RulesV2.SuperleagueBonusDenominatorDefault,
        int feeder1Numerator = RulesV2.Feeder1BonusNumeratorDefault,
        int feeder1Denominator = RulesV2.Feeder1BonusDenominatorDefault,
        int feeder2Numerator = RulesV2.Feeder2BonusNumeratorDefault,
        int feeder2Denominator = RulesV2.Feeder2BonusDenominatorDefault,
        int feeder3Numerator = RulesV2.Feeder3BonusNumeratorDefault,
        int feeder3Denominator = RulesV2.Feeder3BonusDenominatorDefault,
        int cupSuperleagueStrengthPermille = RulesV2.CupSuperleagueStrengthPermilleDefault,
        int cupFeeder1StrengthPermille = RulesV2.CupFeeder1StrengthPermilleDefault,
        int cupFeeder2StrengthPermille = RulesV2.CupFeeder2StrengthPermilleDefault,
        int cupFeeder3StrengthPermille = RulesV2.CupFeeder3StrengthPermilleDefault,
        int prestigeSuperTitlePoints = PrestigeSuperTitlePointsDefault,
        int prestigeFeeder1TitlePoints = PrestigeFeeder1TitlePointsDefault,
        int prestigeFeeder2TitlePoints = PrestigeFeeder2TitlePointsDefault,
        int prestigeFeeder3TitlePoints = PrestigeFeeder3TitlePointsDefault,
        int prestigeSuperAppearancePoints = PrestigeSuperAppearancePointsDefault,
        int prestigeMajorCupTitlePoints = PrestigeMajorCupTitlePointsDefault,
        int prestigeSuperStageWinPoints = PrestigeSuperStageWinPointsDefault,
        int prestigeSuperStageSecondPoints = PrestigeSuperStageSecondPointsDefault,
        int prestigeSuperStageThirdPoints = PrestigeSuperStageThirdPointsDefault,
        int prestigeFeeder1StageWinPoints = PrestigeFeeder1StageWinPointsDefault,
        int prestigeFeeder1StageSecondPoints = PrestigeFeeder1StageSecondPointsDefault,
        int prestigeFeeder1StageThirdPoints = PrestigeFeeder1StageThirdPointsDefault,
        int prestigeFeeder2StageWinPoints = PrestigeFeeder2StageWinPointsDefault,
        int prestigeFeeder2StageSecondPoints = PrestigeFeeder2StageSecondPointsDefault,
        int prestigeFeeder2StageThirdPoints = PrestigeFeeder2StageThirdPointsDefault,
        int prestigeFeeder3StageWinPoints = PrestigeFeeder3StageWinPointsDefault,
        int prestigeFeeder3StageSecondPoints = PrestigeFeeder3StageSecondPointsDefault,
        int prestigeFeeder3StageThirdPoints = PrestigeFeeder3StageThirdPointsDefault)
    {
        RulesV2 core = RulesV2.Create(
            overrides,
            superleagueNumerator,
            superleagueDenominator,
            feeder1Numerator,
            feeder1Denominator,
            feeder2Numerator,
            feeder2Denominator,
            feeder3Numerator,
            feeder3Denominator,
            cupSuperleagueStrengthPermille,
            cupFeeder1StrengthPermille,
            cupFeeder2StrengthPermille,
            cupFeeder3StrengthPermille);
        var candidate = new RulesV3(
            core.SportingColorCount, core.AthletesPerSportingColor, core.TotalAthletesInSave,
            core.RegularLeagueCount, core.LeagueSize, core.SuperleagueSize,
            core.StagesPerSeason, core.RoundsPerStage, core.QualifierSize,
            core.QualifierRounds, core.QualifierWinners, core.SuperleagueSafeCount,
            core.SuperleagueRelegatedCount, core.SuperleagueQualifierIncumbentCount,
            core.FeederAutoPromotedCount, core.FeederQualifierCount, core.InauguralQualifiedPerLeague,
            core.ColorCupColorCount, core.ColorCupTeamSize, core.ColorCupIndividualRounds,
            core.ColorCupTeamGroupRounds, core.TypeCupMinTeamSize, core.TypeCupGroupRounds,
            core.TypeCupTournamentFormatVersion, core.TypeCupMaxDirectFinalTeams, core.TypeCupFinalTeamCount,
            core.RecentFormStageCount, core.CupBonusWeightPermille, core.CupPerformanceWeightPermille,
            core.CupFormWeightPermille, core.CupPrestigeWeightPermille, core.Prestige,
            [.. core.ScoringTable], [.. core.RoundBonusThousandths], [.. core.StageBonusThousandths],
            [.. core.BonusAgeWeightsThousandths], [.. core.RecentFormWeights],
            core.SuperleagueBonusNumerator, core.SuperleagueBonusDenominator,
            core.Feeder1BonusNumerator, core.Feeder1BonusDenominator,
            core.Feeder2BonusNumerator, core.Feeder2BonusDenominator,
            core.Feeder3BonusNumerator, core.Feeder3BonusDenominator,
            core.CupSuperleagueStrengthPermille, core.CupFeeder1StrengthPermille,
            core.CupFeeder2StrengthPermille, core.CupFeeder3StrengthPermille,
            prestigeSuperTitlePoints, prestigeFeeder1TitlePoints,
            prestigeFeeder2TitlePoints, prestigeFeeder3TitlePoints,
            prestigeSuperAppearancePoints, prestigeMajorCupTitlePoints,
            prestigeSuperStageWinPoints, prestigeSuperStageSecondPoints,
            prestigeSuperStageThirdPoints, prestigeFeeder1StageWinPoints,
            prestigeFeeder1StageSecondPoints, prestigeFeeder1StageThirdPoints,
            prestigeFeeder2StageWinPoints, prestigeFeeder2StageSecondPoints,
            prestigeFeeder2StageThirdPoints, prestigeFeeder3StageWinPoints,
            prestigeFeeder3StageSecondPoints, prestigeFeeder3StageThirdPoints);
        candidate.Validate();
        return candidate;
    }

    public override void Validate()
    {
        base.Validate();
        ValidateTieredPrestige();
    }

    private void ValidateTieredPrestige()
    {
        if (PrestigeSuperTitlePoints != PrestigeSuperTitlePointsDefault
            || PrestigeFeeder1TitlePoints != PrestigeFeeder1TitlePointsDefault
            || PrestigeFeeder2TitlePoints != PrestigeFeeder2TitlePointsDefault
            || PrestigeFeeder3TitlePoints != PrestigeFeeder3TitlePointsDefault)
        {
            throw new InvalidOperationException(
                $"Cup prestige league titles must be {PrestigeSuperTitlePointsDefault}/{PrestigeFeeder1TitlePointsDefault}/" +
                $"{PrestigeFeeder2TitlePointsDefault}/{PrestigeFeeder3TitlePointsDefault} quarter-points, was " +
                $"{PrestigeSuperTitlePoints}/{PrestigeFeeder1TitlePoints}/" +
                $"{PrestigeFeeder2TitlePoints}/{PrestigeFeeder3TitlePoints}.");
        }

        if (PrestigeSuperAppearancePoints != PrestigeSuperAppearancePointsDefault)
        {
            throw new InvalidOperationException(
                $"Cup prestige Superleague appearance must be {PrestigeSuperAppearancePointsDefault} quarter-points, was {PrestigeSuperAppearancePoints}.");
        }

        if (PrestigeMajorCupTitlePoints != PrestigeMajorCupTitlePointsDefault)
        {
            throw new InvalidOperationException(
                $"Cup prestige major Cup title must be {PrestigeMajorCupTitlePointsDefault} quarter-points, was {PrestigeMajorCupTitlePoints}.");
        }

        ValidateTieredStage(nameof(PrestigeSuperStageWinPoints),
            PrestigeSuperStageWinPoints, PrestigeSuperStageSecondPoints, PrestigeSuperStageThirdPoints,
            PrestigeSuperStageWinPointsDefault, PrestigeSuperStageSecondPointsDefault, PrestigeSuperStageThirdPointsDefault,
            LeagueLevel.Superleague);
        ValidateTieredStage(nameof(PrestigeFeeder1StageWinPoints),
            PrestigeFeeder1StageWinPoints, PrestigeFeeder1StageSecondPoints, PrestigeFeeder1StageThirdPoints,
            PrestigeFeeder1StageWinPointsDefault, PrestigeFeeder1StageSecondPointsDefault, PrestigeFeeder1StageThirdPointsDefault,
            LeagueLevel.Feeder1);
        ValidateTieredStage(nameof(PrestigeFeeder2StageWinPoints),
            PrestigeFeeder2StageWinPoints, PrestigeFeeder2StageSecondPoints, PrestigeFeeder2StageThirdPoints,
            PrestigeFeeder2StageWinPointsDefault, PrestigeFeeder2StageSecondPointsDefault, PrestigeFeeder2StageThirdPointsDefault,
            LeagueLevel.Feeder2);
        ValidateTieredStage(nameof(PrestigeFeeder3StageWinPoints),
            PrestigeFeeder3StageWinPoints, PrestigeFeeder3StageSecondPoints, PrestigeFeeder3StageThirdPoints,
            PrestigeFeeder3StageWinPointsDefault, PrestigeFeeder3StageSecondPointsDefault, PrestigeFeeder3StageThirdPointsDefault,
            LeagueLevel.Feeder3);
    }

    private static void ValidateTieredStage(
        string name,
        int win,
        int second,
        int third,
        int expectedWin,
        int expectedSecond,
        int expectedThird,
        LeagueLevel level)
    {
        if (win != expectedWin || second != expectedSecond || third != expectedThird)
        {
            throw new InvalidOperationException(
                $"{LeagueHierarchy.DisplayName(level)} stage prestige must be {expectedWin}/{expectedSecond}/{expectedThird} quarter-points, was {win}/{second}/{third} ({name}).");
        }
    }
}
