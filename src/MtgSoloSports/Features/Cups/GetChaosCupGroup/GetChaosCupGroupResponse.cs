namespace MtgSoloSports.Features.Cups.GetChaosCupGroup;

/// <summary>
/// Read-only view of one 32-athlete Chaos Cup group (MSS-107). Returns the drawn
/// entrants with frozen source tier/Pool, current progress, standings and the
/// P16/P17 cutoff. Standings are official (<see cref="StandingsProvisional"/>
/// false) once all 16 rounds complete and provisional before that, derived from
/// the same immutable payloads the simulation wrote. The Final carries no
/// cutoff (<see cref="Cutoff"/> null). Rounds are summaries only; fetch one
/// persisted round via <c>GetChaosCupRoundResponse</c>. Reads never
/// simulate, draw or consume RNG. A stage without a persisted draw 404s and is
/// never portrayed as complete.
/// </summary>
public sealed record GetChaosCupGroupResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int StageIndex,
    int GroupNumber,
    bool IsFinal,
    string State,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    bool StandingsProvisional,
    string DrawChecksum,
    IReadOnlyList<GetChaosCupGroupResponse.Entrant> Entrants,
    IReadOnlyList<GetChaosCupGroupResponse.Standing> Standings,
    GetChaosCupGroupResponse.CutoffInfo? Cutoff,
    IReadOnlyList<GetChaosCupGroupResponse.RoundSummary> Rounds)
{
    public sealed record Entrant(
        int SaveAthleteId,
        string Name,
        int SportingColor,
        string SportingColorName,
        int? SourceLeagueId,
        int SourceLeagueKind,
        int SourceFeederDivision,
        bool IsPoolInvite,
        string SourceLabel,
        int EffectiveBonusThousandths);

    public sealed record Standing(
        int SaveAthleteId,
        string Name,
        int GroupRank,
        int GroupScoreThousandths,
        int BaseScoreThousandths,
        int RoundWins,
        bool IsAdvanced);

    public sealed record CutoffInfo(
        CutoffEntry P16,
        CutoffEntry P17,
        int GapThousandths,
        bool IsProvisional,
        bool IsOfficial);

    public sealed record CutoffEntry(
        int SaveAthleteId,
        string Name,
        int GroupRank,
        int GroupScoreThousandths);

    public sealed record RoundSummary(
        int RoundNumber,
        string Checksum);
}
