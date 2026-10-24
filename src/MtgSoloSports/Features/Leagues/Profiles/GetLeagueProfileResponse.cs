namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Full historical profile for one stable league identity. Podiums, the
/// all-time <c>Total league points</c> leaderboard and records are derived
/// exclusively from completed official final season standings in this league:
/// per-athlete, per-league, per-season totals counted exactly once. The
/// weighted Hall of Fame career score, current bonuses, MTG card stats and Cup
/// results never contribute. An in-progress season appears only in
/// <see cref="CurrentSeason"/> and never affects awards, records or totals.
/// </summary>
public sealed record GetLeagueProfileResponse(
    Guid SaveId,
    string LeagueKey,
    string Name,
    string Kind,
    int FeederDivision,
    string LeagueLevel,
    int SportingColor,
    string SportingColorName,
    int TotalSeasons,
    int CompletedSeasons,
    int UniqueAthletes,
    int? FirstSeasonNumber,
    int? LastCompletedSeasonNumber,
    CurrentSeasonInfo? CurrentSeason,
    IReadOnlyList<GetLeagueProfileResponse.PodiumSeason> PodiumHistory,
    IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> AllTimeLeaderboard,
    IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> Records)
{
    public sealed record PodiumSeason(
        int SeasonNumber,
        int LeagueId,
        bool IsComplete,
        bool HasFinalTable,
        PodiumAthlete? Champion,
        PodiumAthlete? RunnerUp,
        PodiumAthlete? Third,
        int? WinningMarginThousandths);

    public sealed record PodiumAthlete(
        int AthleteId,
        string AthleteName,
        int SeasonRank,
        int TotalChampionshipPointsThousandths);

    /// <summary>
    /// One athlete's career within this league only.
    /// <see cref="TotalLeaguePointsThousandths"/> sums official final season
    /// championship points earned while competing in this league.
    /// </summary>
    public sealed record AllTimeRow(
        int AthleteId,
        string AthleteName,
        long TotalLeaguePointsThousandths,
        int CompletedSeasons,
        int BestRank,
        int Titles,
        int Podiums,
        int RunnerUps);

    public sealed record LeagueRecordEntry(
        string RecordKey,
        string Label,
        long Value,
        string ValueDisplay,
        IReadOnlyList<LeagueRecordHolder> Holders,
        string? ScopeNote);

    public sealed record LeagueRecordHolder(
        int AthleteId,
        string AthleteName,
        IReadOnlyList<int> SeasonNumbers,
        int? SeasonNumber,
        int? ValueThousandths);
}
