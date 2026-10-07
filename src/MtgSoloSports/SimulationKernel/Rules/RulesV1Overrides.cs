namespace MtgSoloSports.SimulationKernel.Rules;

/// <summary>
/// Optional overrides for <see cref="RulesV1.Create"/>, used by tests and future rule versions.
/// A null property means "use the Game Rules v1 default".
/// </summary>
public sealed class RulesV1Overrides
{
    public int? SportingColorCount { get; init; }

    public int? AthletesPerSportingColor { get; init; }

    public int? TotalAthletesInSave { get; init; }

    public int? RegularLeagueCount { get; init; }

    public int? LeagueSize { get; init; }

    public int? SuperleagueSize { get; init; }

    public int? StagesPerSeason { get; init; }

    public int? RoundsPerStage { get; init; }

    public int? QualifierSize { get; init; }

    public int? QualifierRounds { get; init; }

    public int? QualifierWinners { get; init; }

    public int? SuperleagueSafeCount { get; init; }

    public int? SuperleagueRelegatedCount { get; init; }

    public int? SuperleagueQualifierIncumbentCount { get; init; }

    public int? FeederAutoPromotedCount { get; init; }

    public int? FeederQualifierCount { get; init; }

    public int? InauguralQualifiedPerLeague { get; init; }

    public int? SuperleagueBonusMultiplier { get; init; }

    public int? ColorCupColorCount { get; init; }

    public int? ColorCupTeamSize { get; init; }

    public int? ColorCupIndividualRounds { get; init; }

    public int? ColorCupTeamGroupRounds { get; init; }

    public int? TypeCupMinTeamSize { get; init; }

    public int? TypeCupGroupRounds { get; init; }

    public int? TypeCupTournamentFormatVersion { get; init; }

    public int? TypeCupMaxDirectFinalTeams { get; init; }

    public int? TypeCupFinalTeamCount { get; init; }

    public int? RecentFormStageCount { get; init; }

    public int? CupBonusWeightPermille { get; init; }

    public int? CupPerformanceWeightPermille { get; init; }

    public int? CupFormWeightPermille { get; init; }

    public int? CupPrestigeWeightPermille { get; init; }

    public int? CupPrestigeFeederTitlePoints { get; init; }

    public int? CupPrestigeSuperleagueTitlePoints { get; init; }

    public int? CupPrestigeSuperleagueAppearancePoints { get; init; }

    public int? CupPrestigeStageWinPoints { get; init; }

    public int? CupPrestigeStageSecondPoints { get; init; }

    public int? CupPrestigeStageThirdPoints { get; init; }

    public int? CupPrestigeOtherMajorHonourPoints { get; init; }

    public IReadOnlyList<int>? ScoringTable { get; init; }

    public IReadOnlyList<int>? RoundBonusThousandths { get; init; }

    public IReadOnlyList<int>? StageBonusThousandths { get; init; }

    public IReadOnlyList<int>? BonusAgeWeightsThousandths { get; init; }

    public IReadOnlyList<int>? RecentFormWeights { get; init; }
}
