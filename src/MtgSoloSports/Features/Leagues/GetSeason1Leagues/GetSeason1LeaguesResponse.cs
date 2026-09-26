namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// Season 1 inaugural league rosters plus remaining pool counts. Rosters are
/// returned in persisted draw order; pool athletes stay counted, not listed, to
/// keep the payload small. The draw checksum fingerprints the league rosters so
/// presentation can verify it replays persisted facts.
/// </summary>
public sealed record GetSeason1LeaguesResponse(
    Guid SaveId,
    int SeasonNumber,
    bool HasSuperleague,
    string DrawChecksum,
    int ActiveAthletes,
    int PoolAthletes,
    IReadOnlyList<Season1LeagueRoster> Leagues,
    IReadOnlyList<Season1PoolCount> PoolCounts);
