using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Leagues.InauguralDraw;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Season 1
/// inaugural draw (never resimulates): eight feeder-league rosters in draw order
/// plus per-color common-pool counts. Unknown saves abort with not-found; saves
/// without Season 1 abort as not-found so presentation never invents rosters.
/// </summary>
public sealed class GetSeason1LeaguesHandler
{
    private readonly SaveStore _store;

    public GetSeason1LeaguesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetSeason1LeaguesResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity? season = await LoadSeasonAsync(context, cancellationToken).ConfigureAwait(false);
        if (season is null)
        {
            throw new SaveNotFoundException(saveId);
        }

        RulesV1 rules = await LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> leagues = await LoadLeaguesAsync(context, season.Id, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> memberships = await LoadMembershipsAsync(context, season.Id, cancellationToken).ConfigureAwait(false);
        List<SaveAthleteEntity> athletes = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        return BuildResponse(saveId, season, leagues, memberships, athletes, rules);
    }

    internal static Task<SeasonEntity?> LoadSeasonAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Seasons.AsNoTracking().SingleOrDefaultAsync(e => e.SeasonNumber == 1, cancellationToken);
    }

    internal static Task<List<LeagueEntity>> LoadLeaguesAsync(SaveDbContext context, int seasonId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .OrderBy(e => e.SportingColor)
            .ToListAsync(cancellationToken);
    }

    internal static Task<List<SeasonMembershipEntity>> LoadMembershipsAsync(SaveDbContext context, int seasonId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .ToListAsync(cancellationToken);
    }

    internal static Task<List<SaveAthleteEntity>> LoadAthleteNamesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.SaveAthletes
            .AsNoTracking()
            .Select(e => new SaveAthleteEntity { Id = e.Id, Name = e.Name })
            .ToListAsync(cancellationToken);
    }

    internal static async Task<RulesV1> LoadRulesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RulesSnapshotEntity? row = await context.RulesSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.RulesJson))
        {
            throw new InvalidOperationException("Save is missing its rules snapshot.");
        }

        return RulesSnapshotDocument.FromJson(row.RulesJson).ToRules();
    }

    internal static GetSeason1LeaguesResponse BuildResponse(
        Guid saveId,
        SeasonEntity season,
        IReadOnlyList<LeagueEntity> leagues,
        IReadOnlyList<SeasonMembershipEntity> memberships,
        IReadOnlyList<SaveAthleteEntity> athletes,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(athletes);
        ArgumentNullException.ThrowIfNull(rules);

        ValidatePersisted(season, leagues, memberships, athletes, rules);
        Dictionary<int, string> names = athletes.ToDictionary(e => e.Id, e => e.Name);
        SplitMemberships(leagues, memberships, out Dictionary<int, List<SeasonMembershipEntity>> active, out Dictionary<SportingColor, int> pools);
        List<Season1LeagueRoster> rosters = BuildRosters(leagues, active, names, out List<InauguralDrawEntry> checksumEntries);
        return AssembleResponse(saveId, season, memberships, rosters, pools, checksumEntries);
    }

    private static void ValidatePersisted(
        SeasonEntity season,
        IReadOnlyList<LeagueEntity> leagues,
        IReadOnlyList<SeasonMembershipEntity> memberships,
        IReadOnlyList<SaveAthleteEntity> athletes,
        RulesV1 rules)
    {
        List<PersistedLeague> invariantLeagues = leagues
            .Select(l => new PersistedLeague(l.Id, (SportingColor)l.SportingColor, l.Kind, l.Name))
            .ToList();
        List<PersistedMembership> invariantMemberships = memberships
            .Select(m => new PersistedMembership(m.SaveAthleteId, (SportingColor)m.SportingColor, m.LeagueId, m.DrawIndex))
            .ToList();
        Season1PersistedInvariants.ValidatePersistedSeason1(
            season.SeasonNumber,
            season.HasSuperleague,
            invariantLeagues,
            invariantMemberships,
            athletes.Count,
            rules);
    }

    private static void SplitMemberships(
        IReadOnlyList<LeagueEntity> leagues,
        IReadOnlyList<SeasonMembershipEntity> memberships,
        out Dictionary<int, List<SeasonMembershipEntity>> active,
        out Dictionary<SportingColor, int> pools)
    {
        active = leagues.ToDictionary(l => l.Id, _ => new List<SeasonMembershipEntity>());
        pools = new Dictionary<SportingColor, int>();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            pools[color] = 0;
        }

        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.LeagueId is null)
            {
                pools[(SportingColor)membership.SportingColor]++;
            }
            else
            {
                active[membership.LeagueId.Value].Add(membership);
            }
        }
    }

    private static List<Season1LeagueRoster> BuildRosters(
        IReadOnlyList<LeagueEntity> leagues,
        Dictionary<int, List<SeasonMembershipEntity>> active,
        Dictionary<int, string> names,
        out List<InauguralDrawEntry> checksumEntries)
    {
        List<Season1LeagueRoster> rosters = [];
        checksumEntries = [];
        foreach (LeagueEntity league in leagues.OrderBy(l => l.SportingColor))
        {
            List<SeasonMembershipEntity> members = active[league.Id];
            members.Sort(static (a, b) => a.DrawIndex.CompareTo(b.DrawIndex));
            SportingColor color = (SportingColor)league.SportingColor;
            List<Season1RosterAthlete> roster = new(members.Count);
            foreach (SeasonMembershipEntity member in members)
            {
                if (!names.TryGetValue(member.SaveAthleteId, out string? name))
                {
                    throw new InvalidOperationException($"Season 1 membership references unknown athlete {member.SaveAthleteId}.");
                }

                roster.Add(new Season1RosterAthlete(name, member.DrawIndex));
                checksumEntries.Add(new InauguralDrawEntry(name, color, member.DrawIndex, true));
            }

            rosters.Add(new Season1LeagueRoster(league.Name, color.ToString(), league.Id, roster));
        }

        return rosters;
    }

    private static GetSeason1LeaguesResponse AssembleResponse(
        Guid saveId,
        SeasonEntity season,
        IReadOnlyList<SeasonMembershipEntity> memberships,
        IReadOnlyList<Season1LeagueRoster> rosters,
        Dictionary<SportingColor, int> pools,
        IReadOnlyList<InauguralDrawEntry> checksumEntries)
    {
        string checksum = InauguralDrawSelector.ComputeChecksum(checksumEntries);
        List<Season1PoolCount> poolList = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            poolList.Add(new Season1PoolCount(color.ToString(), pools[color]));
        }

        int active = memberships.Count(m => m.LeagueId is not null);
        int pooled = memberships.Count - active;
        return new GetSeason1LeaguesResponse(
            saveId,
            season.SeasonNumber,
            season.HasSuperleague,
            checksum,
            active,
            pooled,
            rosters,
            poolList);
    }
}
