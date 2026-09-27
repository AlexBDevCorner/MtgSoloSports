using System.Text.Json;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Serializable form of the immutable <see cref="RulesV1"/> snapshot.
/// Stored as compact JSON in <see cref="RulesSnapshotEntity.RulesJson"/> so an
/// existing save never silently changes sporting mathematics when defaults evolve.
/// </summary>
public sealed record RulesSnapshotDocument(
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
    int SuperleagueBonusMultiplier,
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

    public static RulesSnapshotDocument FromRules(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new RulesSnapshotDocument(
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
            rules.SuperleagueBonusMultiplier,
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
    /// Rebuilds a validated <see cref="RulesV1"/> snapshot. Throws on any violation;
    /// invariant failures abort the mutation and never silently repair state.
    /// </summary>
    public RulesV1 ToRules()
    {
        RulesV1 rules = RulesV1.Create(BuildOverrides());

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
        // Backward compatibility: saves persisted before MSS-023 have zeroed
        // prestige fields (missing JSON properties deserialize to 0). Zero is
        // never a valid v1 prestige constant, so substitute the v1 defaults.
        return BuildCoreOverrides(
            ResolvePrestige(CupPrestigeFeederTitlePoints, RulesV1.DefaultPrestigeFeederTitlePoints),
            ResolvePrestige(CupPrestigeSuperleagueTitlePoints, RulesV1.DefaultPrestigeSuperleagueTitlePoints),
            ResolvePrestige(CupPrestigeSuperleagueAppearancePoints, RulesV1.DefaultPrestigeSuperleagueAppearancePoints),
            ResolvePrestige(CupPrestigeStageWinPoints, RulesV1.DefaultPrestigeStageWinPoints),
            ResolvePrestige(CupPrestigeStageSecondPoints, RulesV1.DefaultPrestigeStageSecondPoints),
            ResolvePrestige(CupPrestigeStageThirdPoints, RulesV1.DefaultPrestigeStageThirdPoints),
            ResolvePrestige(CupPrestigeOtherMajorHonourPoints, RulesV1.DefaultPrestigeOtherMajorHonourPoints));
    }

    private RulesV1Overrides BuildCoreOverrides(
        int feederTitle,
        int superTitle,
        int superAppearance,
        int stageWin,
        int stageSecond,
        int stageThird,
        int otherMajor)
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
            SuperleagueBonusMultiplier = SuperleagueBonusMultiplier,
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
            CupPrestigeFeederTitlePoints = feederTitle,
            CupPrestigeSuperleagueTitlePoints = superTitle,
            CupPrestigeSuperleagueAppearancePoints = superAppearance,
            CupPrestigeStageWinPoints = stageWin,
            CupPrestigeStageSecondPoints = stageSecond,
            CupPrestigeStageThirdPoints = stageThird,
            CupPrestigeOtherMajorHonourPoints = otherMajor,
            ScoringTable = (int[])ScoringTable.Clone(),
            RoundBonusThousandths = (int[])RoundBonusThousandths.Clone(),
            StageBonusThousandths = (int[])StageBonusThousandths.Clone(),
            BonusAgeWeightsThousandths = (int[])BonusAgeWeightsThousandths.Clone(),
            RecentFormWeights = (int[])RecentFormWeights.Clone(),
        };
    }

    private static int ResolvePrestige(int stored, int @default) => stored == 0 ? @default : stored;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RulesSnapshotDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RulesSnapshotDocument? document = JsonSerializer.Deserialize<RulesSnapshotDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Rules snapshot payload is empty.");
    }
}
