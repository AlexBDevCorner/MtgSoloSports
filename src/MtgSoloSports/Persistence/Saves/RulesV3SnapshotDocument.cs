using System.Text.Json;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Serializable form of the immutable <see cref="RulesV3"/> tiered prestige snapshot.
/// Same sporting core as v2 plus the exact quarter-point prestige model
/// (Super/F1/F2/F3 titles 1200/400/200/100, Super appearance 80, major Cup
/// title 600, Super stage 80/40/16, F1 40/20/8, F2 20/10/4, F3 10/5/2).
/// Stored as compact JSON in <see cref="RulesSnapshotEntity.RulesJson"/> for new saves.
/// v1 and v2 snapshots stay on their documents and are never rewritten;
/// database-schema migration never touches these payloads.
/// </summary>
public sealed record RulesV3SnapshotDocument(
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
    int CupSuperleagueStrengthPermille,
    int CupFeeder1StrengthPermille,
    int CupFeeder2StrengthPermille,
    int CupFeeder3StrengthPermille,
    int ColorCupColorCount,
    int ColorCupTeamSize,
    int ColorCupIndividualRounds,
    int ColorCupTeamGroupRounds,
    int TypeCupMinTeamSize,
    int TypeCupGroupRounds,
    int TypeCupTournamentFormatVersion,
    int TypeCupMaxDirectFinalTeams,
    int TypeCupFinalTeamCount,
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
    int PrestigeSuperTitlePoints,
    int PrestigeFeeder1TitlePoints,
    int PrestigeFeeder2TitlePoints,
    int PrestigeFeeder3TitlePoints,
    int PrestigeSuperAppearancePoints,
    int PrestigeMajorCupTitlePoints,
    int PrestigeSuperStageWinPoints,
    int PrestigeSuperStageSecondPoints,
    int PrestigeSuperStageThirdPoints,
    int PrestigeFeeder1StageWinPoints,
    int PrestigeFeeder1StageSecondPoints,
    int PrestigeFeeder1StageThirdPoints,
    int PrestigeFeeder2StageWinPoints,
    int PrestigeFeeder2StageSecondPoints,
    int PrestigeFeeder2StageThirdPoints,
    int PrestigeFeeder3StageWinPoints,
    int PrestigeFeeder3StageSecondPoints,
    int PrestigeFeeder3StageThirdPoints,
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

    public static RulesV3SnapshotDocument FromRules(RulesV3 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new RulesV3SnapshotDocument(
            rules.Version, rules.Algorithm, rules.AlgorithmVersion,
            rules.SportingColorCount, rules.AthletesPerSportingColor, rules.TotalAthletesInSave,
            rules.RegularLeagueCount, rules.LeagueSize, rules.SuperleagueSize,
            rules.StagesPerSeason, rules.RoundsPerStage, rules.QualifierSize,
            rules.QualifierRounds, rules.QualifierWinners, rules.SuperleagueSafeCount,
            rules.SuperleagueRelegatedCount, rules.SuperleagueQualifierIncumbentCount,
            rules.FeederAutoPromotedCount, rules.FeederQualifierCount, rules.InauguralQualifiedPerLeague,
            rules.SuperleagueBonusNumerator, rules.SuperleagueBonusDenominator,
            rules.Feeder1BonusNumerator, rules.Feeder1BonusDenominator,
            rules.Feeder2BonusNumerator, rules.Feeder2BonusDenominator,
            rules.Feeder3BonusNumerator, rules.Feeder3BonusDenominator,
            rules.CupSuperleagueStrengthPermille, rules.CupFeeder1StrengthPermille,
            rules.CupFeeder2StrengthPermille, rules.CupFeeder3StrengthPermille,
            rules.ColorCupColorCount, rules.ColorCupTeamSize, rules.ColorCupIndividualRounds,
            rules.ColorCupTeamGroupRounds, rules.TypeCupMinTeamSize, rules.TypeCupGroupRounds,
            rules.TypeCupTournamentFormatVersion, rules.TypeCupMaxDirectFinalTeams, rules.TypeCupFinalTeamCount,
            rules.RecentFormStageCount, rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupPrestigeWeightPermille,
            rules.CupPrestigeFeederTitlePoints, rules.CupPrestigeSuperleagueTitlePoints,
            rules.CupPrestigeSuperleagueAppearancePoints, rules.CupPrestigeStageWinPoints,
            rules.CupPrestigeStageSecondPoints, rules.CupPrestigeStageThirdPoints,
            rules.CupPrestigeOtherMajorHonourPoints,
            rules.PrestigeSuperTitlePoints, rules.PrestigeFeeder1TitlePoints,
            rules.PrestigeFeeder2TitlePoints, rules.PrestigeFeeder3TitlePoints,
            rules.PrestigeSuperAppearancePoints, rules.PrestigeMajorCupTitlePoints,
            rules.PrestigeSuperStageWinPoints, rules.PrestigeSuperStageSecondPoints,
            rules.PrestigeSuperStageThirdPoints, rules.PrestigeFeeder1StageWinPoints,
            rules.PrestigeFeeder1StageSecondPoints, rules.PrestigeFeeder1StageThirdPoints,
            rules.PrestigeFeeder2StageWinPoints, rules.PrestigeFeeder2StageSecondPoints,
            rules.PrestigeFeeder2StageThirdPoints, rules.PrestigeFeeder3StageWinPoints,
            rules.PrestigeFeeder3StageSecondPoints, rules.PrestigeFeeder3StageThirdPoints,
            [.. rules.ScoringTable], [.. rules.RoundBonusThousandths], [.. rules.StageBonusThousandths],
            [.. rules.BonusAgeWeightsThousandths], [.. rules.RecentFormWeights]);
    }

    /// <summary>
    /// Rebuilds a validated <see cref="RulesV3"/> snapshot. Throws on any violation.
    /// </summary>
    public RulesV3 ToRules()
    {
        RulesV3 rules = RulesV3.Create(
            BuildOverrides(),
            SuperleagueBonusNumerator,
            SuperleagueBonusDenominator,
            Feeder1BonusNumerator,
            Feeder1BonusDenominator,
            Feeder2BonusNumerator,
            Feeder2BonusDenominator,
            Feeder3BonusNumerator,
            Feeder3BonusDenominator,
            ResolveStrength(CupSuperleagueStrengthPermille, RulesV2.CupSuperleagueStrengthPermilleDefault),
            ResolveStrength(CupFeeder1StrengthPermille, RulesV2.CupFeeder1StrengthPermilleDefault),
            ResolveStrength(CupFeeder2StrengthPermille, RulesV2.CupFeeder2StrengthPermilleDefault),
            ResolveStrength(CupFeeder3StrengthPermille, RulesV2.CupFeeder3StrengthPermilleDefault),
            ResolveTiered(PrestigeSuperTitlePoints, RulesV3.PrestigeSuperTitlePointsDefault),
            ResolveTiered(PrestigeFeeder1TitlePoints, RulesV3.PrestigeFeeder1TitlePointsDefault),
            ResolveTiered(PrestigeFeeder2TitlePoints, RulesV3.PrestigeFeeder2TitlePointsDefault),
            ResolveTiered(PrestigeFeeder3TitlePoints, RulesV3.PrestigeFeeder3TitlePointsDefault),
            ResolveTiered(PrestigeSuperAppearancePoints, RulesV3.PrestigeSuperAppearancePointsDefault),
            ResolveTiered(PrestigeMajorCupTitlePoints, RulesV3.PrestigeMajorCupTitlePointsDefault),
            ResolveTiered(PrestigeSuperStageWinPoints, RulesV3.PrestigeSuperStageWinPointsDefault),
            ResolveTiered(PrestigeSuperStageSecondPoints, RulesV3.PrestigeSuperStageSecondPointsDefault),
            ResolveTiered(PrestigeSuperStageThirdPoints, RulesV3.PrestigeSuperStageThirdPointsDefault),
            ResolveTiered(PrestigeFeeder1StageWinPoints, RulesV3.PrestigeFeeder1StageWinPointsDefault),
            ResolveTiered(PrestigeFeeder1StageSecondPoints, RulesV3.PrestigeFeeder1StageSecondPointsDefault),
            ResolveTiered(PrestigeFeeder1StageThirdPoints, RulesV3.PrestigeFeeder1StageThirdPointsDefault),
            ResolveTiered(PrestigeFeeder2StageWinPoints, RulesV3.PrestigeFeeder2StageWinPointsDefault),
            ResolveTiered(PrestigeFeeder2StageSecondPoints, RulesV3.PrestigeFeeder2StageSecondPointsDefault),
            ResolveTiered(PrestigeFeeder2StageThirdPoints, RulesV3.PrestigeFeeder2StageThirdPointsDefault),
            ResolveTiered(PrestigeFeeder3StageWinPoints, RulesV3.PrestigeFeeder3StageWinPointsDefault),
            ResolveTiered(PrestigeFeeder3StageSecondPoints, RulesV3.PrestigeFeeder3StageSecondPointsDefault),
            ResolveTiered(PrestigeFeeder3StageThirdPoints, RulesV3.PrestigeFeeder3StageThirdPointsDefault));

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
            TypeCupTournamentFormatVersion = TypeCupTournamentFormatVersion,
            TypeCupMaxDirectFinalTeams = TypeCupMaxDirectFinalTeams == 0 ? RulesV1.DefaultTypeCupMaxDirectFinalTeams : TypeCupMaxDirectFinalTeams,
            TypeCupFinalTeamCount = TypeCupFinalTeamCount == 0 ? RulesV1.DefaultTypeCupFinalTeamCount : TypeCupFinalTeamCount,
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

    private static int ResolveStrength(int stored, int @default) => stored == 0 ? @default : stored;

    private static int ResolveTiered(int stored, int @default) => stored == 0 ? @default : stored;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RulesV3SnapshotDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RulesV3SnapshotDocument? document = JsonSerializer.Deserialize<RulesV3SnapshotDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Rules snapshot payload is empty.");
    }
}
