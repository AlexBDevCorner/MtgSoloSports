using System.Text.Json;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Serializable form of the immutable <see cref="RulesV2"/> tiered snapshot.
/// Same sporting core as v1 plus the explicit per-tier rational bonus scales
/// (Superleague 2/1, Feeder 1 1/1, Feeder 2 1/2, Feeder 3 1/4). Stored as
/// compact JSON in <see cref="RulesSnapshotEntity.RulesJson"/> for new saves.
/// v1 snapshots stay on <see cref="RulesSnapshotDocument"/> and are never
/// rewritten; database-schema migration never touches either payload.
/// </summary>
public sealed record RulesV2SnapshotDocument(
    int Version,
    string Algorithm,
    int AlgorithmVersion,
    int SportingColorCount,
    int AthletesPerSportingColor,
    int TotalAthletesInSave,
    int RegularLeagueCount,
    int LeagueSize,
    int SuperleagueSize,
    int StagesPerSeason,
    int RoundsPerStage,
    int QualifierSize,
    int QualifierRounds,
    int QualifierWinners,
    int SuperleagueSafeCount,
    int SuperleagueRelegatedCount,
    int SuperleagueQualifierIncumbentCount,
    int FeederAutoPromotedCount,
    int FeederQualifierCount,
    int InauguralQualifiedPerLeague,
    int SuperleagueBonusNumerator,
    int SuperleagueBonusDenominator,
    int Feeder1BonusNumerator,
    int Feeder1BonusDenominator,
    int Feeder2BonusNumerator,
    int Feeder2BonusDenominator,
    int Feeder3BonusNumerator,
    int Feeder3BonusDenominator,
    int ColorCupColorCount,
    int ColorCupTeamSize,
    int ColorCupIndividualRounds,
    int ColorCupTeamGroupRounds,
    int TypeCupMinTeamSize,
    int TypeCupGroupRounds,
    int RecentFormStageCount,
    int CupBonusWeightPermille,
    int CupPerformanceWeightPermille,
    int CupFormWeightPermille,
    int CupPrestigeWeightPermille,
    int CupPrestigeFeederTitlePoints,
    int CupPrestigeSuperleagueTitlePoints,
    int CupPrestigeSuperleagueAppearancePoints,
    int CupPrestigeStageWinPoints,
    int CupPrestigeStageSecondPoints,
    int CupPrestigeStageThirdPoints,
    int CupPrestigeOtherMajorHonourPoints,
    int[] ScoringTable,
    int[] RoundBonusThousandths,
    int[] StageBonusThousandths,
    int[] BonusAgeWeightsThousandths,
    int[] RecentFormWeights)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static RulesV2SnapshotDocument FromRules(RulesV2 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new RulesV2SnapshotDocument(
            rules.Version,
            rules.Algorithm,
            rules.AlgorithmVersion,
            rules.SportingColorCount,
            rules.AthletesPerSportingColor,
            rules.TotalAthletesInSave,
            rules.RegularLeagueCount,
            rules.LeagueSize,
            rules.SuperleagueSize,
            rules.StagesPerSeason,
            rules.RoundsPerStage,
            rules.QualifierSize,
            rules.QualifierRounds,
            rules.QualifierWinners,
            rules.SuperleagueSafeCount,
            rules.SuperleagueRelegatedCount,
            rules.SuperleagueQualifierIncumbentCount,
            rules.FeederAutoPromotedCount,
            rules.FeederQualifierCount,
            rules.InauguralQualifiedPerLeague,
            rules.SuperleagueBonusNumerator,
            rules.SuperleagueBonusDenominator,
            rules.Feeder1BonusNumerator,
            rules.Feeder1BonusDenominator,
            rules.Feeder2BonusNumerator,
            rules.Feeder2BonusDenominator,
            rules.Feeder3BonusNumerator,
            rules.Feeder3BonusDenominator,
            rules.ColorCupColorCount,
            rules.ColorCupTeamSize,
            rules.ColorCupIndividualRounds,
            rules.ColorCupTeamGroupRounds,
            rules.TypeCupMinTeamSize,
            rules.TypeCupGroupRounds,
            rules.RecentFormStageCount,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            rules.CupPrestigeFeederTitlePoints,
            rules.CupPrestigeSuperleagueTitlePoints,
            rules.CupPrestigeSuperleagueAppearancePoints,
            rules.CupPrestigeStageWinPoints,
            rules.CupPrestigeStageSecondPoints,
            rules.CupPrestigeStageThirdPoints,
            rules.CupPrestigeOtherMajorHonourPoints,
            [.. rules.ScoringTable],
            [.. rules.RoundBonusThousandths],
            [.. rules.StageBonusThousandths],
            [.. rules.BonusAgeWeightsThousandths],
            [.. rules.RecentFormWeights]);
    }

    /// <summary>
    /// Rebuilds a validated <see cref="RulesV2"/> snapshot. Throws on any violation.
    /// </summary>
    public RulesV2 ToRules()
    {
        RulesV2 rules = RulesV2.Create(
            BuildOverrides(),
            SuperleagueBonusNumerator,
            SuperleagueBonusDenominator,
            Feeder1BonusNumerator,
            Feeder1BonusDenominator,
            Feeder2BonusNumerator,
            Feeder2BonusDenominator,
            Feeder3BonusNumerator,
            Feeder3BonusDenominator);

        if (rules.Version != Version)
        {
            throw new InvalidOperationException($"Rules version mismatch: expected {Version}, was {rules.Version}.");
        }

        if (!string.Equals(rules.Algorithm, Algorithm, StringComparison.Ordinal) || rules.AlgorithmVersion != AlgorithmVersion)
        {
            throw new InvalidOperationException("Rules RNG algorithm mismatch.");
        }

        return rules;
    }

    private RulesV1Overrides BuildOverrides()
    {
        return new RulesV1Overrides
        {
            SportingColorCount = SportingColorCount,
            AthletesPerSportingColor = AthletesPerSportingColor,
            TotalAthletesInSave = TotalAthletesInSave,
            RegularLeagueCount = RegularLeagueCount,
            LeagueSize = LeagueSize,
            SuperleagueSize = SuperleagueSize,
            StagesPerSeason = StagesPerSeason,
            RoundsPerStage = RoundsPerStage,
            QualifierSize = QualifierSize,
            QualifierRounds = QualifierRounds,
            QualifierWinners = QualifierWinners,
            SuperleagueSafeCount = SuperleagueSafeCount,
            SuperleagueRelegatedCount = SuperleagueRelegatedCount,
            SuperleagueQualifierIncumbentCount = SuperleagueQualifierIncumbentCount,
            FeederAutoPromotedCount = FeederAutoPromotedCount,
            FeederQualifierCount = FeederQualifierCount,
            InauguralQualifiedPerLeague = InauguralQualifiedPerLeague,
            SuperleagueBonusMultiplier = 2,
            ColorCupColorCount = ColorCupColorCount,
            ColorCupTeamSize = ColorCupTeamSize,
            ColorCupIndividualRounds = ColorCupIndividualRounds,
            ColorCupTeamGroupRounds = ColorCupTeamGroupRounds,
            TypeCupMinTeamSize = TypeCupMinTeamSize,
            TypeCupGroupRounds = TypeCupGroupRounds,
            RecentFormStageCount = RecentFormStageCount,
            CupBonusWeightPermille = CupBonusWeightPermille,
            CupPerformanceWeightPermille = CupPerformanceWeightPermille,
            CupFormWeightPermille = CupFormWeightPermille,
            CupPrestigeWeightPermille = CupPrestigeWeightPermille,
            CupPrestigeFeederTitlePoints = ResolvePrestige(CupPrestigeFeederTitlePoints, RulesV1.DefaultPrestigeFeederTitlePoints),
            CupPrestigeSuperleagueTitlePoints = ResolvePrestige(CupPrestigeSuperleagueTitlePoints, RulesV1.DefaultPrestigeSuperleagueTitlePoints),
            CupPrestigeSuperleagueAppearancePoints = ResolvePrestige(CupPrestigeSuperleagueAppearancePoints, RulesV1.DefaultPrestigeSuperleagueAppearancePoints),
            CupPrestigeStageWinPoints = ResolvePrestige(CupPrestigeStageWinPoints, RulesV1.DefaultPrestigeStageWinPoints),
            CupPrestigeStageSecondPoints = ResolvePrestige(CupPrestigeStageSecondPoints, RulesV1.DefaultPrestigeStageSecondPoints),
            CupPrestigeStageThirdPoints = ResolvePrestige(CupPrestigeStageThirdPoints, RulesV1.DefaultPrestigeStageThirdPoints),
            CupPrestigeOtherMajorHonourPoints = ResolvePrestige(CupPrestigeOtherMajorHonourPoints, RulesV1.DefaultPrestigeOtherMajorHonourPoints),
            ScoringTable = (int[])ScoringTable.Clone(),
            RoundBonusThousandths = (int[])RoundBonusThousandths.Clone(),
            StageBonusThousandths = (int[])StageBonusThousandths.Clone(),
            BonusAgeWeightsThousandths = (int[])BonusAgeWeightsThousandths.Clone(),
            RecentFormWeights = (int[])RecentFormWeights.Clone(),
        };
    }

    private static int ResolvePrestige(int stored, int @default) => stored == 0 ? @default : stored;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RulesV2SnapshotDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RulesV2SnapshotDocument? document = JsonSerializer.Deserialize<RulesV2SnapshotDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Rules snapshot payload is empty.");
    }
}
