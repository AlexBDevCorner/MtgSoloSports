namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Overview of every distinct official league in a save, grouped by the
/// stable cross-season <see cref="LeagueKey"/>. One entry per sporting
/// identity (Superleague once; every feeder division/color separately);
/// never includes the pool, which has no recorded season standings.
/// </summary>
public sealed record ListLeagueProfilesResponse(
    Guid SaveId,
    IReadOnlyList<ListLeagueProfilesResponse.LeagueProfileSummary> Leagues)
{
    public sealed record LeagueProfileSummary(
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
        ChampionRef? LatestChampion);

    public sealed record ChampionRef(
        int AthleteId,
        string AthleteName,
        int SeasonNumber,
        int TotalChampionshipPointsThousandths);
}
