using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Superleague.GetFeederMovements;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted
/// competitive feeder movement for one tiered season transition (never
/// resimulates): automatic promotions/relegations and qualifier
/// incumbents/challengers across F1↔F2 and F2↔F3, 8 athletes per color per
/// kind per boundary (512 rows). Tier identity comes from league rows, never
/// from league-name parsing. Without <paramref name="fromSeasonNumber"/>
/// returns the latest resolved tiered transition; with it returns that
/// season's transition. Throws
/// <see cref="FeederMovementsNotFoundException"/> (404) when the transition
/// has no feeder movement (v1 or inaugural transitions), and aborts on
/// corrupt counts.
/// </summary>
public sealed class GetFeederMovementsHandler
{
    private readonly SaveStore _store;

    public GetFeederMovementsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetFeederMovementsResponse> HandleAsync(
        Guid saveId,
        int? fromSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (fromSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(fromSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        (SeasonEntity source, SeasonEntity next) = await LoadSeasonsAsync(context, fromSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, next, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadSeasonsAsync(
        SaveDbContext context, int? fromSeasonNumber, CancellationToken cancellationToken)
    {
        if (fromSeasonNumber.HasValue)
        {
            SeasonEntity? source = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (source is null || !source.HasSuperleague)
            {
                throw new FeederMovementsNotFoundException(
                    $"Feeder movement for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            SeasonEntity? next = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken)
                .ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new FeederMovementsNotFoundException(
                    $"Feeder movement for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureMovementExistsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
            return (source, next);
        }

        List<MovementEntity> feeder = await context.Movements
            .AsNoTracking()
            .Where(e => e.Kind == (int)MovementKind.FeederAutomaticPromotion
                || e.Kind == (int)MovementKind.FeederAutomaticRelegation
                || e.Kind == (int)MovementKind.FeederQualifierIncumbent
                || e.Kind == (int)MovementKind.FeederQualifierChallenger)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feeder.Count == 0)
        {
            throw new FeederMovementsNotFoundException("Feeder movement has not been resolved yet.");
        }

        int latestToSeasonId = feeder.Max(m => m.ToSeasonId);
        MovementEntity sample = feeder.First(m => m.ToSeasonId == latestToSeasonId);
        SeasonEntity? latestNext = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.ToSeasonId, cancellationToken)
            .ConfigureAwait(false);
        SeasonEntity? latestSource = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.FromSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latestNext is null || latestSource is null)
        {
            throw new InvalidOperationException("Feeder movement references unknown seasons.");
        }

        return (latestSource, latestNext);
    }

    internal static async Task EnsureMovementExistsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int count = await context.Movements
            .CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && (e.Kind == (int)MovementKind.FeederAutomaticPromotion
                        || e.Kind == (int)MovementKind.FeederAutomaticRelegation
                        || e.Kind == (int)MovementKind.FeederQualifierIncumbent
                        || e.Kind == (int)MovementKind.FeederQualifierChallenger),
                cancellationToken)
            .ConfigureAwait(false);
        if (count == 0)
        {
            throw new FeederMovementsNotFoundException(
                $"Feeder movement for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetFeederMovementsResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        List<MovementEntity> movements = await context.Movements
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.FeederAutomaticPromotion
                    || e.Kind == (int)MovementKind.FeederAutomaticRelegation
                    || e.Kind == (int)MovementKind.FeederQualifierIncumbent
                    || e.Kind == (int)MovementKind.FeederQualifierChallenger))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateMovements(movements);

        // Reuse the Superleague movement read-model loaders: names, artwork,
        // league rows and next-season memberships. Read-only queries never lock.
        Dictionary<int, string> names = await GetAutomaticMovementHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string?> images = await GetAutomaticMovementHandler.LoadImagesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await GetAutomaticMovementHandler.LoadLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await GetAutomaticMovementHandler.LoadNextMembershipsAsync(context, next, cancellationToken).ConfigureAwait(false);

        List<FeederMovementMember> members = MapMembers(movements, names, images, leaguesById, nextByAthlete);
        return new GetFeederMovementsResponse(saveId, source.SeasonNumber, next.SeasonNumber, members, movements.Count);
    }

    internal static void ValidateMovements(List<MovementEntity> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        // 8 sporting colors x 2 boundaries x 4 kinds x 8 athletes.
        const int expected = 8 * 2 * 4 * 8;
        if (movements.Count != expected)
        {
            throw new InvalidOperationException(
                $"Feeder movement must hold exactly {expected} records, was {movements.Count}.");
        }

        foreach (MovementKind kind in new[]
            {
                MovementKind.FeederAutomaticPromotion,
                MovementKind.FeederAutomaticRelegation,
                MovementKind.FeederQualifierIncumbent,
                MovementKind.FeederQualifierChallenger,
            })
        {
            int count = movements.Count(m => m.Kind == (int)kind);
            if (count != 8 * 2 * 8)
            {
                throw new InvalidOperationException(
                    $"Feeder movement kind {kind} must hold exactly {8 * 2 * 8} records, was {count}.");
            }
        }
    }

    internal static List<FeederMovementMember> MapMembers(
        List<MovementEntity> movements,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete)
    {
        List<FeederMovementMember> members = new(movements.Count);
        foreach (MovementEntity movement in movements
            .OrderBy(m => m.FromLeagueId)
            .ThenBy(m => m.FromSeasonRank))
        {
            names.TryGetValue(movement.SaveAthleteId, out string? name);
            images.TryGetValue(movement.SaveAthleteId, out string? imageUrl);
            leaguesById.TryGetValue(movement.FromLeagueId, out LeagueEntity? from);
            if (from is null)
            {
                throw new InvalidOperationException(
                    $"Feeder movement references unknown source league {movement.FromLeagueId}.");
            }

            nextByAthlete.TryGetValue(movement.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            int toLeague = nextMembership?.LeagueId ?? movement.ToLeagueId;
            leaguesById.TryGetValue(toLeague, out LeagueEntity? to);
            string colorName = nextMembership is null
                ? ((SportingColor)movement.SportingColor).ToString()
                : ((SportingColor)nextMembership.SportingColor).ToString();
            QualifierBoundary boundary = ResolveBoundary((MovementKind)movement.Kind, from);
            members.Add(new FeederMovementMember(
                movement.SaveAthleteId,
                name ?? $"Athlete {movement.SaveAthleteId}",
                movement.SportingColor,
                colorName,
                (int)boundary,
                boundary.ToString(),
                movement.FromLeagueId,
                from.Name,
                LeagueEntityLevels.GetLevel(from).ToString(),
                movement.FromSeasonRank,
                toLeague,
                to?.Name ?? $"League {toLeague}",
                to is null ? null : LeagueEntityLevels.GetLevel(to).ToString(),
                ((MovementKind)movement.Kind).ToString(),
                imageUrl));
        }

        return members;
    }

    /// <summary>
    /// The qualifier boundary a feeder movement belongs to, derived from the
    /// movement kind and the source tier only. Qualifier incumbents and
    /// challengers provisionally hold their source division until the
    /// qualifier resolves, so the destination tier cannot identify the event;
    /// automatic promotions/relegations always cross exactly one adjacent
    /// boundary. Anything else is corrupt sporting state and aborts.
    /// </summary>
    internal static QualifierBoundary ResolveBoundary(MovementKind kind, LeagueEntity from)
    {
        ArgumentNullException.ThrowIfNull(from);
        LeagueLevel fromLevel = LeagueEntityLevels.GetLevel(from);
        return (kind, fromLevel) switch
        {
            (MovementKind.FeederAutomaticPromotion, LeagueLevel.Feeder2) => QualifierBoundary.Feeder1Feeder2,
            (MovementKind.FeederAutomaticPromotion, LeagueLevel.Feeder3) => QualifierBoundary.Feeder2Feeder3,
            (MovementKind.FeederAutomaticRelegation, LeagueLevel.Feeder1) => QualifierBoundary.Feeder1Feeder2,
            (MovementKind.FeederAutomaticRelegation, LeagueLevel.Feeder2) => QualifierBoundary.Feeder2Feeder3,
            (MovementKind.FeederQualifierIncumbent, LeagueLevel.Feeder1) => QualifierBoundary.Feeder1Feeder2,
            (MovementKind.FeederQualifierIncumbent, LeagueLevel.Feeder2) => QualifierBoundary.Feeder2Feeder3,
            (MovementKind.FeederQualifierChallenger, LeagueLevel.Feeder2) => QualifierBoundary.Feeder1Feeder2,
            (MovementKind.FeederQualifierChallenger, LeagueLevel.Feeder3) => QualifierBoundary.Feeder2Feeder3,
            _ => throw new InvalidOperationException(
                $"Feeder movement kind {kind} from {fromLevel} is not on an adjacent feeder boundary."),
        };
    }
}
