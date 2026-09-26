namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One persisted season standing row per athlete per completed league season.
/// Totals accumulate stage championship points across all 32 stages; stage and
/// round placement vectors plus raw stage/base totals retain the full
/// deterministic tie-break inputs (stage places best-downward, then round
/// places, then raw totals, then a seeded draw) without scanning round
/// payloads. <see cref="SeasonRank"/> 1 holds the league champion
/// (<see cref="IsChampion"/>). These rows are the durable summary for later
/// promotion, history and Cup selection formula; they are created atomically
/// when the last active league completes Stage 32.
/// </summary>
public sealed class SeasonStandingEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int LeagueId { get; set; }

    public int SaveAthleteId { get; set; }

    public int SeasonRank { get; set; }

    public int TotalChampionshipPointsThousandths { get; set; }

    public int TotalStageScoreThousandths { get; set; }

    public int TotalBaseScoreThousandths { get; set; }

    public int StageWins { get; set; }

    public int RoundWins { get; set; }

    public string StagePlaceCountsJson { get; set; } = "[]";

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public bool IsChampion { get; set; }
}
