namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Type Cup team round row per rank group per round for a completed
/// even source season. The team event runs four rank groups (all #1 athletes,
/// then #2, #3, #4) with a dynamically varying team count N (one athlete per
/// participating creature type at the same selection rank) over exactly eight
/// rounds per group (MSS-027): active career bonus applies with normal fixed-point
/// scoring, but no new career bonus is generated and no league <c>Round</c>,
/// <c>Stage</c> or <c>StageStanding</c> rows are created. Detailed replay lives
/// in the compact immutable compressed payload (JSON + Brotli <c>br1:</c>,
/// matching normal league round history), never in one row per athlete
/// placement. RNG before/after states are stored alongside the result so the
/// RNG commit and the sporting result share one transaction.
/// Tournament identity (MSS-061) uses three separate fields, never one overloaded
/// integer: <see cref="TournamentPhase"/> (0 legacy single-field, 1 qualification,
/// 2 Final), <see cref="QualificationGroup"/> (0 for legacy/Final, 1..G for
/// qualification), and <see cref="GroupNumber"/> (athlete rank group 1..4).
/// Legacy rows predate the scalable format and store 0/0; new-format qualification
/// and Final rounds use 1/G and 2/0 respectively so the two stages never collide.
/// </summary>
public sealed class TypeCupTeamRoundEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int TournamentPhase { get; set; }

    public int QualificationGroup { get; set; }

    public int GroupNumber { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
