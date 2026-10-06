using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Immutable versioned snapshot of Game Rules v1.
/// Each save owns one snapshot so later default changes never silently alter existing universes.
/// All sporting mathematics must read from the snapshot, never from hard-coded literals.
/// Tables are defensively copied and exposed as read-only lists.
/// v1 knows only the single-tier feeder model: Superleague earns double via
/// <see cref="SuperleagueBonusMultiplier"/>, every feeder earns the baseline.
/// v1 supports <see cref="LeagueLevel.Superleague"/> and
/// <see cref="LeagueLevel.Feeder1"/> only; F2/F3 throw rather than silently
/// reinterpreting historical seasons. Tiered saves use <see cref="RulesV2"/>.
/// </summary>
public class RulesV1
{
    public const int RulesVersion = 1;
    public const string RngAlgorithm = "Pcg32V1";
    public const int RngVersion = 1;
    public const int FixedScale = 1000;
    public const int CupWeightScale = 1000;

    // Active bonus is a percentage held in thousandths of a percent (7552 == +7.552%),
    // so final = base x (BonusPercentScale + bonus) / BonusPercentScale.
    public const int BonusPercentScale = 100_000;

    private static readonly int[] DefaultScoringTable =
        [77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];

    private static readonly int[] DefaultRoundBonusThousandths = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10];
    private static readonly int[] DefaultStageBonusThousandths = [200, 180, 160, 140, 120, 100, 80, 60, 40, 20];
    private static readonly int[] DefaultBonusAgeWeightsThousandths = [1000, 800, 600, 400, 200, 0];
    private static readonly int[] DefaultRecentFormWeights = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    // Color Cup career-prestige v1 constants (raw prestige points per achievement).
    // Prestige raw = feederTitles * 100 + superleagueTitles * 300
    //   + superleagueAppearances * 20 + stageWins * 10 + stageSeconds * 5
    //   + stageThirds * 2 + otherMajorHonours * 150.
    // Other major honours counts official Honour rows beyond feeder/Superleague
    // titles (future Color Cup individual/team and Type Cup team honours); it is
    // zero for saves created before Cups and keeps the formula forward compatible.
    // Calibrated so prestige decides close calls (10% weight) without dominating
    // bonus (35%) or season performance (30%).
    public const int DefaultPrestigeFeederTitlePoints = 100;
    public const int DefaultPrestigeSuperleagueTitlePoints = 300;
    public const int DefaultPrestigeSuperleagueAppearancePoints = 20;
    public const int DefaultPrestigeStageWinPoints = 10;
    public const int DefaultPrestigeStageSecondPoints = 5;
    public const int DefaultPrestigeStageThirdPoints = 2;
    public const int DefaultPrestigeOtherMajorHonourPoints = 150;

    protected RulesV1(
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
        int superleagueBonusMultiplier,
        int colorCupColorCount,
        int colorCupTeamSize,
        int colorCupIndividualRounds,
        int colorCupTeamGroupRounds,
        int typeCupMinTeamSize,
        int typeCupGroupRounds,
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
        int[] recentFormWeights)
    {
        SportingColorCount = sportingColorCount;
        AthletesPerSportingColor = athletesPerSportingColor;
        TotalAthletesInSave = totalAthletesInSave;
        RegularLeagueCount = regularLeagueCount;
        LeagueSize = leagueSize;
        SuperleagueSize = superleagueSize;
        StagesPerSeason = stagesPerSeason;
        RoundsPerStage = roundsPerStage;
        QualifierSize = qualifierSize;
        QualifierRounds = qualifierRounds;
        QualifierWinners = qualifierWinners;
        SuperleagueSafeCount = superleagueSafeCount;
        SuperleagueRelegatedCount = superleagueRelegatedCount;
        SuperleagueQualifierIncumbentCount = superleagueQualifierIncumbentCount;
        FeederAutoPromotedCount = feederAutoPromotedCount;
        FeederQualifierCount = feederQualifierCount;
        InauguralQualifiedPerLeague = inauguralQualifiedPerLeague;
        SuperleagueBonusMultiplier = superleagueBonusMultiplier;
        ColorCupColorCount = colorCupColorCount;
        ColorCupTeamSize = colorCupTeamSize;
        ColorCupIndividualRounds = colorCupIndividualRounds;
        ColorCupTeamGroupRounds = colorCupTeamGroupRounds;
        TypeCupMinTeamSize = typeCupMinTeamSize;
        TypeCupGroupRounds = typeCupGroupRounds;
        RecentFormStageCount = recentFormStageCount;
        CupBonusWeightPermille = cupBonusWeightPermille;
        CupPerformanceWeightPermille = cupPerformanceWeightPermille;
        CupFormWeightPermille = cupFormWeightPermille;
        CupPrestigeWeightPermille = cupPrestigeWeightPermille;
        Prestige = prestige ?? throw new ArgumentNullException(nameof(prestige));
        ScoringTable = Array.AsReadOnly(scoringTable);
        RoundBonusThousandths = Array.AsReadOnly(roundBonusThousandths);
        StageBonusThousandths = Array.AsReadOnly(stageBonusThousandths);
        BonusAgeWeightsThousandths = Array.AsReadOnly(bonusAgeWeightsThousandths);
        RecentFormWeights = Array.AsReadOnly(recentFormWeights);
    }

    public virtual int Version => RulesVersion;

    public string Algorithm => RngAlgorithm;

    public int AlgorithmVersion => RngVersion;

    public int SportingColorCount { get; }

    public int AthletesPerSportingColor { get; }

    public int TotalAthletesInSave { get; }

    public int RegularLeagueCount { get; }

    public int LeagueSize { get; }

    public int SuperleagueSize { get; }

    public int StagesPerSeason { get; }

    public int RoundsPerStage { get; }

    public int QualifierSize { get; }

    public int QualifierRounds { get; }

    public int QualifierWinners { get; }

    public int SuperleagueSafeCount { get; }

    public int SuperleagueRelegatedCount { get; }

    public int SuperleagueQualifierIncumbentCount { get; }

    public int FeederAutoPromotedCount { get; }

    public int FeederQualifierCount { get; }

    public int InauguralQualifiedPerLeague { get; }

    public int SuperleagueBonusMultiplier { get; }

    /// <summary>
    /// Whether this snapshot supports the given competitive tier.
    /// v1 supports Superleague and Feeder 1 only; tiered saves use v2.
    /// </summary>
    public virtual bool SupportsLevel(LeagueLevel level) =>
        level is LeagueLevel.Superleague or LeagueLevel.Feeder1;

    /// <summary>
    /// Number of feeder divisions per sporting color under this snapshot.
    /// v1 has one (Feeder 1 only); v2 has three (Feeder 1/2/3).
    /// Derived from <see cref="SupportsLevel"/>, never stored, so historical
    /// snapshots keep their meaning and new defaults never reinterpret history.
    /// </summary>
    public virtual int FeederDivisionsPerColor => SupportsLevel(LeagueLevel.Feeder2) ? 3 : 1;

    /// <summary>
    /// Number of Season 1 feeder leagues under this snapshot: 8 for v1, 24 for v2.
    /// </summary>
    public int Season1FeederLeagueCount => RegularLeagueCount * FeederDivisionsPerColor;

    /// <summary>
    /// Active feeder athletes per sporting color in Season 1: 32 for v1, 96 for v2.
    /// </summary>
    public int Season1ActivePerColor => LeagueSize * FeederDivisionsPerColor;

    /// <summary>
    /// Common-pool athletes per sporting color in Season 1: 224 for v1, 160 for v2.
    /// </summary>
    public int Season1PoolPerColor => AthletesPerSportingColor - Season1ActivePerColor;

    /// <summary>
    /// Global Season 1 active membership: 256 for v1, 768 for v2.
    /// </summary>
    public int Season1ActiveTotal => Season1FeederLeagueCount * LeagueSize;

    /// <summary>
    /// Feeder leagues in a post-inaugural (Superleague) season: 8 for v1, 24 for v2.
    /// </summary>
    public int TieredFeederLeagueCount => Season1FeederLeagueCount;

    /// <summary>
    /// Total leagues in a post-inaugural season including the Superleague: 9 for v1, 25 for v2.
    /// </summary>
    public int PostInauguralLeagueCount => TieredFeederLeagueCount + 1;

    /// <summary>
    /// Global active membership in a post-inaugural season (feeders + Superleague):
    /// 288 for v1 (256 + 32), 800 for v2 (768 + 32).
    /// </summary>
    public int PostInauguralActiveTotal => Season1ActiveTotal + SuperleagueSize;

    /// <summary>
    /// Exact bonus scale for a tier under this snapshot.
    /// v1: Superleague 2/1, Feeder 1 1/1; F2/F3 throw instead of silently
    /// reinterpreting historical seasons.
    /// </summary>
    public virtual TierBonusScale GetBonusScale(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => new TierBonusScale(2, 1),
        LeagueLevel.Feeder1 => new TierBonusScale(1, 1),
        LeagueLevel.Feeder2 or LeagueLevel.Feeder3 => throw new InvalidOperationException(
            $"Rules v1 has no tier scale for {LeagueHierarchy.DisplayName(level)}; tiered saves require rules v2."),
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    public int ColorCupColorCount { get; }

    public int ColorCupTeamSize { get; }

    public int ColorCupIndividualRounds { get; }

    public int ColorCupTeamGroupRounds { get; }

    public int TypeCupMinTeamSize { get; }

    public int TypeCupGroupRounds { get; }

    public int RecentFormStageCount { get; }

    public int CupBonusWeightPermille { get; }

    public int CupPerformanceWeightPermille { get; }

    public int CupFormWeightPermille { get; }

    public int CupPrestigeWeightPermille { get; }

    /// <summary>
    /// Versioned career-prestige constants. Stored in every save snapshot.
    /// Initial v1: feeder 100, Superleague title 300, appearance 20,
    /// stage win 10, second 5, third 2, other major 150.
    /// </summary>
    public ColorCupPrestigeConstants Prestige { get; }

    /// <summary>
    /// Raw prestige points per feeder-league championship. Initial v1 value 100.
    /// </summary>
    public int CupPrestigeFeederTitlePoints => Prestige.FeederTitlePoints;

    /// <summary>
    /// Raw prestige points per Superleague championship. Initial v1 value 300.
    /// </summary>
    public int CupPrestigeSuperleagueTitlePoints => Prestige.SuperleagueTitlePoints;

    /// <summary>
    /// Raw prestige points per active Superleague season. Initial v1 value 20.
    /// </summary>
    public int CupPrestigeSuperleagueAppearancePoints => Prestige.SuperleagueAppearancePoints;

    /// <summary>
    /// Raw prestige points per league stage win. Initial v1 value 10.
    /// </summary>
    public int CupPrestigeStageWinPoints => Prestige.StageWinPoints;

    /// <summary>
    /// Raw prestige points per league stage second place. Initial v1 value 5.
    /// </summary>
    public int CupPrestigeStageSecondPoints => Prestige.StageSecondPoints;

    /// <summary>
    /// Raw prestige points per league stage third place. Initial v1 value 2.
    /// </summary>
    public int CupPrestigeStageThirdPoints => Prestige.StageThirdPoints;

    /// <summary>
    /// Raw prestige points per other official major honour (future Cup titles).
    /// Initial v1 value 150.
    /// </summary>
    public int CupPrestigeOtherMajorHonourPoints => Prestige.OtherMajorHonourPoints;

    public IReadOnlyList<int> ScoringTable { get; }

    public IReadOnlyList<int> RoundBonusThousandths { get; }

    public IReadOnlyList<int> StageBonusThousandths { get; }

    public IReadOnlyList<int> BonusAgeWeightsThousandths { get; }

    public IReadOnlyList<int> RecentFormWeights { get; }

    public static RulesV1 CreateDefault() => Create(null);

    public static RulesV1 Create(RulesV1Overrides? overrides)
    {
        RulesV1Overrides active = overrides ?? new RulesV1Overrides();
        int[] resolvedScoring = ResolveTable(active.ScoringTable, DefaultScoringTable, nameof(active.ScoringTable));
        int[] resolvedRoundBonus = ResolveTable(active.RoundBonusThousandths, DefaultRoundBonusThousandths, nameof(active.RoundBonusThousandths));
        int[] resolvedStageBonus = ResolveTable(active.StageBonusThousandths, DefaultStageBonusThousandths, nameof(active.StageBonusThousandths));
        int[] resolvedAgeWeights = ResolveTable(active.BonusAgeWeightsThousandths, DefaultBonusAgeWeightsThousandths, nameof(active.BonusAgeWeightsThousandths));
        int[] resolvedFormWeights = ResolveTable(active.RecentFormWeights, DefaultRecentFormWeights, nameof(active.RecentFormWeights));
        ColorCupPrestigeConstants prestige = ResolvePrestige(active);

        var candidate = new RulesV1(
            active.SportingColorCount ?? 8,
            active.AthletesPerSportingColor ?? 256,
            active.TotalAthletesInSave ?? 2048,
            active.RegularLeagueCount ?? 8,
            active.LeagueSize ?? 32,
            active.SuperleagueSize ?? 32,
            active.StagesPerSeason ?? 32,
            active.RoundsPerStage ?? 16,
            active.QualifierSize ?? 32,
            active.QualifierRounds ?? 16,
            active.QualifierWinners ?? 8,
            active.SuperleagueSafeCount ?? 16,
            active.SuperleagueRelegatedCount ?? 8,
            active.SuperleagueQualifierIncumbentCount ?? 8,
            active.FeederAutoPromotedCount ?? 8,
            active.FeederQualifierCount ?? 24,
            active.InauguralQualifiedPerLeague ?? 4,
            active.SuperleagueBonusMultiplier ?? 2,
            active.ColorCupColorCount ?? 8,
            active.ColorCupTeamSize ?? 4,
            active.ColorCupIndividualRounds ?? 16,
            active.ColorCupTeamGroupRounds ?? 8,
            active.TypeCupMinTeamSize ?? 4,
            active.TypeCupGroupRounds ?? 8,
            active.RecentFormStageCount ?? 10,
            active.CupBonusWeightPermille ?? 350,
            active.CupPerformanceWeightPermille ?? 300,
            active.CupFormWeightPermille ?? 250,
            active.CupPrestigeWeightPermille ?? 100,
            prestige,
            resolvedScoring,
            resolvedRoundBonus,
            resolvedStageBonus,
            resolvedAgeWeights,
            resolvedFormWeights);
        candidate.Validate();
        return candidate;
    }

    private static ColorCupPrestigeConstants ResolvePrestige(RulesV1Overrides active)
    {
        ArgumentNullException.ThrowIfNull(active);
        return new ColorCupPrestigeConstants(
            active.CupPrestigeFeederTitlePoints ?? DefaultPrestigeFeederTitlePoints,
            active.CupPrestigeSuperleagueTitlePoints ?? DefaultPrestigeSuperleagueTitlePoints,
            active.CupPrestigeSuperleagueAppearancePoints ?? DefaultPrestigeSuperleagueAppearancePoints,
            active.CupPrestigeStageWinPoints ?? DefaultPrestigeStageWinPoints,
            active.CupPrestigeStageSecondPoints ?? DefaultPrestigeStageSecondPoints,
            active.CupPrestigeStageThirdPoints ?? DefaultPrestigeStageThirdPoints,
            active.CupPrestigeOtherMajorHonourPoints ?? DefaultPrestigeOtherMajorHonourPoints);
    }

    /// <summary>
    /// Verifies the snapshot is internally consistent. Throws on the first violation.
    /// Fundamental invariant failures must abort the mutation, never silently repair state.
    /// </summary>
    public virtual void Validate()
    {
        if (SportingColorCount != 8)
        {
            throw new InvalidOperationException($"SportingColorCount must be 8, was {SportingColorCount}.");
        }

        if (AthletesPerSportingColor != 256)
        {
            throw new InvalidOperationException($"AthletesPerSportingColor must be 256, was {AthletesPerSportingColor}.");
        }

        if (TotalAthletesInSave != SportingColorCount * AthletesPerSportingColor)
        {
            throw new InvalidOperationException($"TotalAthletesInSave must equal colors * per-color ({SportingColorCount * AthletesPerSportingColor}), was {TotalAthletesInSave}.");
        }

        if (RegularLeagueCount != 8)
        {
            throw new InvalidOperationException($"RegularLeagueCount must be 8, was {RegularLeagueCount}.");
        }

        if (LeagueSize != 32)
        {
            throw new InvalidOperationException($"LeagueSize must be 32, was {LeagueSize}.");
        }

        if (SuperleagueSize != 32)
        {
            throw new InvalidOperationException($"SuperleagueSize must be 32, was {SuperleagueSize}.");
        }

        if (StagesPerSeason != 32)
        {
            throw new InvalidOperationException($"StagesPerSeason must be 32, was {StagesPerSeason}.");
        }

        if (RoundsPerStage != 16)
        {
            throw new InvalidOperationException($"RoundsPerStage must be 16, was {RoundsPerStage}.");
        }

        ValidateScoringTable();
        ValidateBonusTables();
        ValidateDecayAndForm();
        ValidateMovement();
        ValidateCups();
    }

    private void ValidateScoringTable()
    {
        if (ScoringTable.Count != LeagueSize)
        {
            throw new InvalidOperationException($"ScoringTable must have {LeagueSize} entries, was {ScoringTable.Count}.");
        }

        for (int i = 0; i < DefaultScoringTable.Length; i++)
        {
            if (ScoringTable[i] != DefaultScoringTable[i])
            {
                throw new InvalidOperationException($"ScoringTable position {i + 1} must be {DefaultScoringTable[i]}, was {ScoringTable[i]}.");
            }
        }
    }

    private void ValidateBonusTables()
    {
        if (RoundBonusThousandths.Count != 10)
        {
            throw new InvalidOperationException($"RoundBonusThousandths must have 10 entries, was {RoundBonusThousandths.Count}.");
        }

        if (StageBonusThousandths.Count != 10)
        {
            throw new InvalidOperationException($"StageBonusThousandths must have 10 entries, was {StageBonusThousandths.Count}.");
        }

        for (int i = 0; i < DefaultRoundBonusThousandths.Length; i++)
        {
            if (RoundBonusThousandths[i] != DefaultRoundBonusThousandths[i])
            {
                throw new InvalidOperationException($"RoundBonusThousandths place {i + 1} must be {DefaultRoundBonusThousandths[i]}, was {RoundBonusThousandths[i]}.");
            }
        }

        for (int i = 0; i < DefaultStageBonusThousandths.Length; i++)
        {
            if (StageBonusThousandths[i] != DefaultStageBonusThousandths[i])
            {
                throw new InvalidOperationException($"StageBonusThousandths place {i + 1} must be {DefaultStageBonusThousandths[i]}, was {StageBonusThousandths[i]}.");
            }
        }

        if (SuperleagueBonusMultiplier != 2)
        {
            throw new InvalidOperationException($"SuperleagueBonusMultiplier must be 2, was {SuperleagueBonusMultiplier}.");
        }
    }

    private void ValidateDecayAndForm()
    {
        if (BonusAgeWeightsThousandths.Count != 6)
        {
            throw new InvalidOperationException($"BonusAgeWeightsThousandths must have 6 entries, was {BonusAgeWeightsThousandths.Count}.");
        }

        for (int i = 0; i < DefaultBonusAgeWeightsThousandths.Length; i++)
        {
            if (BonusAgeWeightsThousandths[i] != DefaultBonusAgeWeightsThousandths[i])
            {
                throw new InvalidOperationException($"BonusAgeWeightsThousandths age {i} must be {DefaultBonusAgeWeightsThousandths[i]}, was {BonusAgeWeightsThousandths[i]}.");
            }
        }

        if (RecentFormStageCount != 10)
        {
            throw new InvalidOperationException($"RecentFormStageCount must be 10, was {RecentFormStageCount}.");
        }

        if (RecentFormWeights.Count != RecentFormStageCount)
        {
            throw new InvalidOperationException($"RecentFormWeights must have {RecentFormStageCount} entries, was {RecentFormWeights.Count}.");
        }

        for (int i = 0; i < DefaultRecentFormWeights.Length; i++)
        {
            if (RecentFormWeights[i] != DefaultRecentFormWeights[i])
            {
                throw new InvalidOperationException($"RecentFormWeights index {i} must be {DefaultRecentFormWeights[i]}, was {RecentFormWeights[i]}.");
            }
        }
    }

    private void ValidateMovement()
    {
        ValidateMovementCounts();
        ValidateMovementIdentities();
    }

    private void ValidateMovementCounts()
    {
        if (QualifierSize != 32)
        {
            throw new InvalidOperationException($"QualifierSize must be 32, was {QualifierSize}.");
        }

        if (QualifierRounds != 16)
        {
            throw new InvalidOperationException($"QualifierRounds must be 16, was {QualifierRounds}.");
        }

        if (QualifierWinners != 8)
        {
            throw new InvalidOperationException($"QualifierWinners must be 8, was {QualifierWinners}.");
        }

        if (SuperleagueSafeCount != 16)
        {
            throw new InvalidOperationException($"SuperleagueSafeCount must be 16, was {SuperleagueSafeCount}.");
        }

        if (SuperleagueRelegatedCount != 8)
        {
            throw new InvalidOperationException($"SuperleagueRelegatedCount must be 8, was {SuperleagueRelegatedCount}.");
        }

        if (SuperleagueQualifierIncumbentCount != 8)
        {
            throw new InvalidOperationException($"SuperleagueQualifierIncumbentCount must be 8, was {SuperleagueQualifierIncumbentCount}.");
        }

        if (FeederAutoPromotedCount != 8)
        {
            throw new InvalidOperationException($"FeederAutoPromotedCount must be 8, was {FeederAutoPromotedCount}.");
        }

        if (FeederQualifierCount != 24)
        {
            throw new InvalidOperationException($"FeederQualifierCount must be 24, was {FeederQualifierCount}.");
        }

        if (InauguralQualifiedPerLeague != 4)
        {
            throw new InvalidOperationException($"InauguralQualifiedPerLeague must be 4, was {InauguralQualifiedPerLeague}.");
        }
    }

    private void ValidateMovementIdentities()
    {

        if (SuperleagueSafeCount + SuperleagueRelegatedCount + SuperleagueQualifierIncumbentCount != SuperleagueSize)
        {
            throw new InvalidOperationException("Superleague movement counts must sum to SuperleagueSize (16 + 8 + 8 = 32).");
        }

        if (SuperleagueSafeCount + FeederAutoPromotedCount + QualifierWinners != SuperleagueSize)
        {
            throw new InvalidOperationException("Next Superleague roster must sum to SuperleagueSize (16 + 8 + 8 = 32).");
        }

        if (SuperleagueQualifierIncumbentCount + FeederQualifierCount != QualifierSize)
        {
            throw new InvalidOperationException("Qualifier roster must sum to QualifierSize (8 + 24 = 32).");
        }

        if (RegularLeagueCount * InauguralQualifiedPerLeague != SuperleagueSize)
        {
            throw new InvalidOperationException("Inaugural Superleague must satisfy 8 x 4 = 32.");
        }
    }

    private void ValidateCups()
    {
        ValidateCupCounts();
        ValidateCupWeights();
        ValidatePrestige();
    }

    private void ValidateCupCounts()
    {
        if (ColorCupColorCount != SportingColorCount)
        {
            throw new InvalidOperationException($"ColorCupColorCount must equal SportingColorCount ({SportingColorCount}), was {ColorCupColorCount}.");
        }

        if (ColorCupTeamSize != 4)
        {
            throw new InvalidOperationException($"ColorCupTeamSize must be 4, was {ColorCupTeamSize}.");
        }

        if (ColorCupColorCount * ColorCupTeamSize != QualifierSize)
        {
            throw new InvalidOperationException("Color Cup field must be 8 x 4 = 32 athletes.");
        }

        if (ColorCupIndividualRounds != RoundsPerStage)
        {
            throw new InvalidOperationException($"ColorCupIndividualRounds must equal RoundsPerStage ({RoundsPerStage}), was {ColorCupIndividualRounds}.");
        }

        if (ColorCupTeamGroupRounds != 8)
        {
            throw new InvalidOperationException($"ColorCupTeamGroupRounds must be 8, was {ColorCupTeamGroupRounds}.");
        }

        if (TypeCupMinTeamSize != 4)
        {
            throw new InvalidOperationException($"TypeCupMinTeamSize must be 4, was {TypeCupMinTeamSize}.");
        }

        if (TypeCupGroupRounds != 8)
        {
            throw new InvalidOperationException($"TypeCupGroupRounds must be 8, was {TypeCupGroupRounds}.");
        }
    }

    private void ValidateCupWeights()
    {
        checked
        {
            int total = CupBonusWeightPermille + CupPerformanceWeightPermille + CupFormWeightPermille + CupPrestigeWeightPermille;
            if (total != CupWeightScale)
            {
                throw new InvalidOperationException($"Cup selection weights must sum to {CupWeightScale}, was {total}.");
            }
        }

        if (CupBonusWeightPermille != 350 || CupPerformanceWeightPermille != 300 || CupFormWeightPermille != 250 || CupPrestigeWeightPermille != 100)
        {
            throw new InvalidOperationException("Cup selection weights must be 350/300/250/100.");
        }
    }

    private void ValidatePrestige()
    {
        ValidatePrestigeTitles();
        ValidatePrestigePodiums();
    }

    private void ValidatePrestigeTitles()
    {
        if (CupPrestigeFeederTitlePoints != DefaultPrestigeFeederTitlePoints)
        {
            throw new InvalidOperationException($"CupPrestigeFeederTitlePoints must be {DefaultPrestigeFeederTitlePoints}, was {CupPrestigeFeederTitlePoints}.");
        }

        if (CupPrestigeSuperleagueTitlePoints != DefaultPrestigeSuperleagueTitlePoints)
        {
            throw new InvalidOperationException($"CupPrestigeSuperleagueTitlePoints must be {DefaultPrestigeSuperleagueTitlePoints}, was {CupPrestigeSuperleagueTitlePoints}.");
        }

        if (CupPrestigeSuperleagueAppearancePoints != DefaultPrestigeSuperleagueAppearancePoints)
        {
            throw new InvalidOperationException($"CupPrestigeSuperleagueAppearancePoints must be {DefaultPrestigeSuperleagueAppearancePoints}, was {CupPrestigeSuperleagueAppearancePoints}.");
        }

        if (CupPrestigeOtherMajorHonourPoints != DefaultPrestigeOtherMajorHonourPoints)
        {
            throw new InvalidOperationException($"CupPrestigeOtherMajorHonourPoints must be {DefaultPrestigeOtherMajorHonourPoints}, was {CupPrestigeOtherMajorHonourPoints}.");
        }
    }

    private void ValidatePrestigePodiums()
    {
        if (CupPrestigeStageWinPoints != DefaultPrestigeStageWinPoints)
        {
            throw new InvalidOperationException($"CupPrestigeStageWinPoints must be {DefaultPrestigeStageWinPoints}, was {CupPrestigeStageWinPoints}.");
        }

        if (CupPrestigeStageSecondPoints != DefaultPrestigeStageSecondPoints)
        {
            throw new InvalidOperationException($"CupPrestigeStageSecondPoints must be {DefaultPrestigeStageSecondPoints}, was {CupPrestigeStageSecondPoints}.");
        }

        if (CupPrestigeStageThirdPoints != DefaultPrestigeStageThirdPoints)
        {
            throw new InvalidOperationException($"CupPrestigeStageThirdPoints must be {DefaultPrestigeStageThirdPoints}, was {CupPrestigeStageThirdPoints}.");
        }
    }

    private static int[] ResolveTable(IReadOnlyList<int>? overrideTable, int[] defaults, string name)
    {
        if (overrideTable is null)
        {
            return (int[])defaults.Clone();
        }

        return CopyRequired(overrideTable, name);
    }

    private static int[] CopyRequired(IReadOnlyList<int> source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        int[] copy = new int[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i];
        }

        if (copy.Length == 0)
        {
            throw new InvalidOperationException($"{name} must not be empty.");
        }

        return copy;
    }
}
