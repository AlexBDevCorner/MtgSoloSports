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
/// Unsealed with a protected carrier constructor (MSS-152) so future RulesV4+
/// subclasses inherit the tiered prestige accessors instead of falling into a
/// legacy version-cast path.
/// </summary>
public class RulesV3 : RulesV2
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

    protected RulesV3(
        RulesCoreSnapshot core,
        RulesV2SportingSnapshot sporting,
        RulesV3PrestigeSnapshot prestige)
        : base(core, sporting)
    {
        ArgumentNullException.ThrowIfNull(prestige);
        PrestigeSuperTitlePoints = prestige.SuperTitlePoints;
        PrestigeFeeder1TitlePoints = prestige.Feeder1TitlePoints;
        PrestigeFeeder2TitlePoints = prestige.Feeder2TitlePoints;
        PrestigeFeeder3TitlePoints = prestige.Feeder3TitlePoints;
        PrestigeSuperAppearancePoints = prestige.SuperAppearancePoints;
        PrestigeMajorCupTitlePoints = prestige.MajorCupTitlePoints;
        PrestigeSuperStageWinPoints = prestige.SuperStageWinPoints;
        PrestigeSuperStageSecondPoints = prestige.SuperStageSecondPoints;
        PrestigeSuperStageThirdPoints = prestige.SuperStageThirdPoints;
        PrestigeFeeder1StageWinPoints = prestige.Feeder1StageWinPoints;
        PrestigeFeeder1StageSecondPoints = prestige.Feeder1StageSecondPoints;
        PrestigeFeeder1StageThirdPoints = prestige.Feeder1StageThirdPoints;
        PrestigeFeeder2StageWinPoints = prestige.Feeder2StageWinPoints;
        PrestigeFeeder2StageSecondPoints = prestige.Feeder2StageSecondPoints;
        PrestigeFeeder2StageThirdPoints = prestige.Feeder2StageThirdPoints;
        PrestigeFeeder3StageWinPoints = prestige.Feeder3StageWinPoints;
        PrestigeFeeder3StageSecondPoints = prestige.Feeder3StageSecondPoints;
        PrestigeFeeder3StageThirdPoints = prestige.Feeder3StageThirdPoints;
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

    /// <summary>
    /// Tiered league-title prestige (MSS-152): overrides the level-blind v1/v2
    /// legacy values so RulesV4+ subclasses resolve through inheritance instead
    /// of <c>is RulesV3</c> version casts.
    /// </summary>
    public override int GetLeagueTitlePrestige(LeagueLevel level) => GetTieredTitlePrestige(level);

    /// <summary>Tiered stage-podium prestige (MSS-152); see <see cref="GetLeagueTitlePrestige"/>.</summary>
    public override int GetStagePrestige(LeagueLevel level, int place) => GetTieredStagePrestige(level, place);

    /// <summary>Tiered Superleague appearance prestige (MSS-152).</summary>
    public override int GetSuperleagueAppearancePrestige() => PrestigeSuperAppearancePoints;

    /// <summary>Tiered major Cup title prestige (MSS-152).</summary>
    public override int GetMajorCupTitlePrestige() => PrestigeMajorCupTitlePoints;

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
            v2.ExtractCore(),
            v2.ExtractSportingSnapshot(),
            RulesV3PrestigeSnapshot.Defaults);
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
        RulesV3PrestigeSnapshot prestige = new(
            SuperTitlePoints: prestigeSuperTitlePoints,
            Feeder1TitlePoints: prestigeFeeder1TitlePoints,
            Feeder2TitlePoints: prestigeFeeder2TitlePoints,
            Feeder3TitlePoints: prestigeFeeder3TitlePoints,
            SuperAppearancePoints: prestigeSuperAppearancePoints,
            MajorCupTitlePoints: prestigeMajorCupTitlePoints,
            SuperStageWinPoints: prestigeSuperStageWinPoints,
            SuperStageSecondPoints: prestigeSuperStageSecondPoints,
            SuperStageThirdPoints: prestigeSuperStageThirdPoints,
            Feeder1StageWinPoints: prestigeFeeder1StageWinPoints,
            Feeder1StageSecondPoints: prestigeFeeder1StageSecondPoints,
            Feeder1StageThirdPoints: prestigeFeeder1StageThirdPoints,
            Feeder2StageWinPoints: prestigeFeeder2StageWinPoints,
            Feeder2StageSecondPoints: prestigeFeeder2StageSecondPoints,
            Feeder2StageThirdPoints: prestigeFeeder2StageThirdPoints,
            Feeder3StageWinPoints: prestigeFeeder3StageWinPoints,
            Feeder3StageSecondPoints: prestigeFeeder3StageSecondPoints,
            Feeder3StageThirdPoints: prestigeFeeder3StageThirdPoints);
        var candidate = new RulesV3(
            core.ExtractCore(),
            core.ExtractSportingSnapshot(),
            prestige);
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
