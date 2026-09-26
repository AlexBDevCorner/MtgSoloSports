namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One stage cursor per league per season. MSS-008 creates Stage 1 on the
/// first <c>AdvanceRound</c> and advances <see cref="CompletedRounds"/> from
/// 0 to 16; the stage slice finalizes the stage with championship points and
/// pending bonus (MSS-009) and creates the next stage row. <see cref="IsComplete"/>
/// marks a stage whose 16 rounds plus 32 <c>StageStanding</c> rows are committed;
/// the cursor plus the unique round rows remain the source of truth for the
/// currently legal round.
/// </summary>
public sealed class StageEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int LeagueId { get; set; }

    public int StageNumber { get; set; }

    public int CompletedRounds { get; set; }

    public bool IsComplete { get; set; }
}
