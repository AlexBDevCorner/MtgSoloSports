using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.GetInauguralRoster;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted
/// inaugural Season 2 Superleague roster (never resimulates): the 32 promoted
/// athletes with Season 1 source league/rank provenance from movement records.
/// Throws <see cref="InauguralRosterNotFoundException"/> (404) when the Season 1
/// transition has not run yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetInauguralRosterHandler
{
    private readonly SaveStore _store;

    public GetInauguralRosterHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetInauguralRosterResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await LoadSeasonTwoAsync(context, cancellationToken).ConfigureAwait(false);
        LeagueEntity superleague = await LoadSuperleagueAsync(context, seasonTwo, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, seasonTwo, superleague, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSeasonTwoAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == 2, cancellationToken)
            .ConfigureAwait(false);
        if (season is null || !season.HasSuperleague)
        {
            throw new InauguralRosterNotFoundException("The inaugural Superleague has not been created yet.");
        }

        return season;
    }

    internal static async Task<LeagueEntity> LoadSuperleagueAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Superleague,
                cancellationToken)
            .ConfigureAwait(false);
        if (league is null)
        {
            throw new InauguralRosterNotFoundException("The inaugural Superleague has not been created yet.");
        }

        return league;
    }

    internal static async Task<GetInauguralRosterResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await LoadRosterMembershipsAsync(context, seasonTwo, superleague, cancellationToken).ConfigureAwait(false);
        List<MovementEntity> movements = await LoadRosterMovementsAsync(context, seasonTwo, superleague, memberships, cancellationToken).ConfigureAwait(false);
        List<InauguralRosterMember> members = await MapRosterMembersAsync(context, seasonTwo, memberships, movements, cancellationToken).ConfigureAwait(false);
        SortRosterMembers(members);

        return new GetInauguralRosterResponse(
            saveId,
            seasonTwo.SeasonNumber,
            superleague.Id,
            superleague.Name,
            members,
            movements.Count);
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadRosterMembershipsAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == superleague.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count == 0)
        {
            throw new InauguralRosterNotFoundException("The inaugural Superleague has not been created yet.");
        }

        if (memberships.Count != RulesV1.CreateDefault().SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Superleague must contain exactly 32 athletes, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static async Task<List<MovementEntity>> LoadRosterMovementsAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        List<SeasonMembershipEntity> memberships,
        CancellationToken cancellationToken)
    {
        List<MovementEntity> movements = await context.Movements
            .AsNoTracking()
            .Where(e => e.ToSeasonId == seasonTwo.Id && e.ToLeagueId == superleague.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (movements.Count != memberships.Count)
        {
            throw new InvalidOperationException(
                $"Superleague roster has {memberships.Count} members but {movements.Count} movement records.");
        }

        return movements;
    }

    internal static async Task<List<InauguralRosterMember>> MapRosterMembersAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        List<SeasonMembershipEntity> memberships,
        List<MovementEntity> movements,
        CancellationToken cancellationToken)
    {
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> leagueNames = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId != seasonTwo.Id)
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, MovementEntity> movementByAthlete = movements.ToDictionary(m => m.SaveAthleteId);
        List<InauguralRosterMember> members = new(memberships.Count);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            members.Add(MapSingleMember(membership, movementByAthlete, names, leagueNames));
        }

        return members;
    }

    internal static InauguralRosterMember MapSingleMember(
        SeasonMembershipEntity membership,
        Dictionary<int, MovementEntity> movementByAthlete,
        Dictionary<int, string> names,
        Dictionary<int, string> leagueNames)
    {
        if (!movementByAthlete.TryGetValue(membership.SaveAthleteId, out MovementEntity? movement))
        {
            throw new InvalidOperationException(
                $"Superleague member {membership.SaveAthleteId} has no movement record.");
        }

        names.TryGetValue(membership.SaveAthleteId, out string? name);
        leagueNames.TryGetValue(movement.FromLeagueId, out string? fromName);
        return new InauguralRosterMember(
            membership.SaveAthleteId,
            name ?? $"Athlete {membership.SaveAthleteId}",
            ((SportingColor)membership.SportingColor).ToString(),
            movement.FromLeagueId,
            fromName ?? $"League {movement.FromLeagueId}",
            movement.FromSeasonRank);
    }

    internal static void SortRosterMembers(List<InauguralRosterMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        members.Sort(static (left, right) =>
        {
            int league = left.FromLeagueId.CompareTo(right.FromLeagueId);
            return league != 0 ? league : left.FromSeasonRank.CompareTo(right.FromSeasonRank);
        });
    }
}
