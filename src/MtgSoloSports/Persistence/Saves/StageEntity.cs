namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One stage cursor per league per season. MSS-008 creates Stage 1 on the
/// first <c>AdvanceRound</c> and advances <see cref="CompletedRounds"/> from
/// 0 to 16; stage completion (championship points, bonus activation) arrives
/// with the stage slice and next-stage creation arrives later. The cursor plus
/// the unique round rows are the source of truth for the currently legal round.
/// </summary>
public sealed class StageEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int LeagueId { get; set; }

    public int StageNumber { get; set; }

    public int CompletedRounds { get; set; }
}
