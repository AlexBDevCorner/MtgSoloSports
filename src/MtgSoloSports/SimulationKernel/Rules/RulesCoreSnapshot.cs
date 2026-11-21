namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Named carrier for the v1 sporting core shared by every rules construction
/// path (MSS-152). Copying one carrier reference replaces the hand-written
/// ~40-value positional copy blocks in RulesV2/RulesV3, where adjacent
/// same-typed integers made transposition undetectable: every field is named
/// at its single construction site, so a transposed value no longer compiles
/// past review as a plausible neighbour. Not persisted: encoding still walks
/// the public rules properties, so persisted JSON bytes and
/// <c>RulesSnapshotCodec</c> compatibility are unchanged and existing saves
/// re-validate byte-identically.
/// </summary>
public sealed record RulesCoreSnapshot(
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
    int TypeCupTournamentFormatVersion,
    int TypeCupMaxDirectFinalTeams,
    int TypeCupFinalTeamCount,
    int RecentFormStageCount,
    int CupBonusWeightPermille,
    int CupPerformanceWeightPermille,
    int CupFormWeightPermille,
    int CupChaosWeightPermille,
    int CupPrestigeWeightPermille,
    ColorCupPrestigeConstants Prestige,
    int[] ScoringTable,
    int[] RoundBonusThousandths,
    int[] StageBonusThousandths,
    int[] BonusAgeWeightsThousandths,
    int[] RecentFormWeights);
