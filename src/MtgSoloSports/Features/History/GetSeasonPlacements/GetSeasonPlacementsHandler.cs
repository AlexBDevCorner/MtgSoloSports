using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.History.GetSeasonPlacements;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Returns one compact
/// season/league placement aggregate for the standings matrix: the
/// season-specific 32-athlete roster plus every completed stage's actual
/// P1..P32 finish with earned bonus. Read-only: no lock, no RNG access,
/// no mutation, no round-payload decompression.
/// </summary>
public sealed class GetSeasonPlacementsHandler
{
    private readonly SaveStore _store;

    public GetSeasonPlacementsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetSeasonPlacementsResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber, leagueId);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        PlacementQuery query = await LoadQueryAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, season, league, query);
    }

    internal static void ValidateIds(Guid saveId, int seasonNumber, int leagueId)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }
    }

    internal sealed record PlacementQuery(
        List<SeasonMembershipEntity> Memberships,
        List<StageStandingEntity> Standings,
        Dictionary<int, SaveAthleteEntity> Cards,
        Dictionary<int, int> EffectiveBonus);

    internal static async Task<PlacementQuery> LoadQueryAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<StageStandingEntity> standings = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> rosterIds = memberships.Select(m => m.SaveAthleteId).ToHashSet();
        Dictionary<int, SaveAthleteEntity> cards = rosterIds.Count == 0
            ? new Dictionary<int, SaveAthleteEntity>()
            : await context.SaveAthletes
                .AsNoTracking()
                .Where(e => rosterIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, cancellationToken)
                .ConfigureAwait(false);

        Dictionary<int, int> effective = rosterIds.Count == 0
            ? new Dictionary<int, int>()
            : await context.AthleteCareers
                .AsNoTracking()
                .Where(e => rosterIds.Contains(e.SaveAthleteId))
                .ToDictionaryAsync(e => e.SaveAthleteId, e => e.CurrentEffectiveBonusThousandths, cancellationToken)
                .ConfigureAwait(false);

        return new PlacementQuery(memberships, standings, cards, effective);
    }

    internal static GetSeasonPlacementsResponse MapResponse(Guid saveId, SeasonEntity season, LeagueEntity league, PlacementQuery query)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(query);

        ValidateMemberships(query.Memberships, league);
        ValidateStandings(query.Standings, league, query.Memberships);

        List<int> stageNumbers = query.Standings.Select(r => r.StageNumber).Distinct().OrderBy(n => n).ToList();
        List<SeasonPlacementAthlete> athletes = MapAthletes(query);
        List<SeasonPlacementCell> placements = MapCells(query.Standings);
        string kind = ((LeagueKind)league.Kind).ToString();

        return new GetSeasonPlacementsResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            kind,
            season.IsComplete,
            stageNumbers.Count,
            athletes,
            placements);
    }

    internal static void ValidateMemberships(List<SeasonMembershipEntity> memberships, LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(league);
        if (memberships.Count != 32)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must hold exactly 32 season members, was {memberships.Count}.");
        }

        HashSet<int> ids = memberships.Select(m => m.SaveAthleteId).ToHashSet();
        if (ids.Count != 32)
        {
            throw new InvalidOperationException($"League '{league.Name}' has duplicate season members.");
        }
    }

    internal static void ValidateStandings(List<StageStandingEntity> standings, LeagueEntity league, List<SeasonMembershipEntity> memberships)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(memberships);

        HashSet<int> roster = memberships.Select(m => m.SaveAthleteId).ToHashSet();
        foreach (IGrouping<int, StageStandingEntity> group in standings.GroupBy(r => r.StageNumber))
        {
            if (group.Key < 1 || group.Key > 32)
            {
                throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {group.Key}.");
            }

            List<StageStandingEntity> rows = group.ToList();
            if (rows.Count != 32)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {group.Key} must hold exactly 32 standings, was {rows.Count}.");
            }

            HashSet<int> ranks = rows.Select(r => r.StageRank).ToHashSet();
            if (!ranks.SetEquals(Enumerable.Range(1, 32)))
            {
                throw new InvalidOperationException($"League '{league.Name}' stage {group.Key} must cover ranks 1..32 exactly once.");
            }

            foreach (StageStandingEntity row in rows)
            {
                if (!roster.Contains(row.SaveAthleteId))
                {
                    throw new InvalidOperationException(
                        $"League '{league.Name}' stage {group.Key} references athlete {row.SaveAthleteId} outside the season roster.");
                }
            }
        }
    }

    internal static List<SeasonPlacementAthlete> MapAthletes(PlacementQuery query)
    {
        List<SeasonPlacementAthlete> athletes = new(query.Memberships.Count);
        foreach (SeasonMembershipEntity membership in query.Memberships.OrderBy(m => m.SaveAthleteId))
        {
            if (!query.Cards.TryGetValue(membership.SaveAthleteId, out SaveAthleteEntity? card))
            {
                throw new InvalidOperationException($"Season roster references unknown athlete {membership.SaveAthleteId}.");
            }

            string colorName = Enum.IsDefined(typeof(SportingColor), card.SportingColor)
                ? ((SportingColor)card.SportingColor).ToString()
                : "Unknown";
            query.EffectiveBonus.TryGetValue(membership.SaveAthleteId, out int effective);
            athletes.Add(new SeasonPlacementAthlete(
                card.Id,
                card.Name,
                card.SportingColor,
                colorName,
                card.ImageUrl,
                effective));
        }

        return athletes;
    }

    internal static List<SeasonPlacementCell> MapCells(List<StageStandingEntity> standings)
    {
        List<SeasonPlacementCell> cells = new(standings.Count);
        foreach (StageStandingEntity row in standings.OrderBy(r => r.StageNumber).ThenBy(r => r.StageRank))
        {
            cells.Add(new SeasonPlacementCell(
                row.SaveAthleteId,
                row.StageNumber,
                row.StageRank,
                row.EarnedBonusThousandths,
                row.ChampionshipPointsThousandths,
                row.StageScoreThousandths,
                row.RoundWins));
        }

        return cells;
    }
}
