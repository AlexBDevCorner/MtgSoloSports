namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One persisted stage standing row per athlete per completed league stage.
/// The stage score is the sum of final round points across all 16 rounds;
/// championship points reuse the 32-position scoring table with no bonus
/// multiplier. <see cref="EarnedBonusThousandths"/> is the round-plus-stage
/// bonus earned during this stage and stays pending until the next-stage
/// boundary. <see cref="RoundPlaceCountsJson"/> is a compact JSON array of 32
/// round-placement counts (index 0 counts round 1st places) retained for season
/// tie-breaking and career projection without scanning round payloads.
/// </summary>
public sealed class StageStandingEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int LeagueId { get; set; }

    public int StageId { get; set; }

    public int StageNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int StageRank { get; set; }

    public int StageScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int ChampionshipPointsThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int EarnedBonusThousandths { get; set; }
}
