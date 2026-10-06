using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Superleague.GetAutomaticMovement;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted
/// automatic movement for a completed Superleague season (never resimulates):
/// 16 safe, 8 promoted, 8 relegated, 8 qualifier incumbents and 24 qualifier
/// challengers with source rank provenance and next-league assignment, plus
/// pool/movement counts. Without <paramref name="fromSeasonNumber"/> returns
/// the latest resolved transition; with it returns that season's transition.
/// Throws <see cref="AutomaticMovementNotFoundException"/> (404) when the
/// movement has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetAutomaticMovementHandler
{
    private readonly SaveStore _store;

    public GetAutomaticMovementHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetAutomaticMovementResponse> HandleAsync(
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
        LeagueEntity nextSuperleague = await LoadNextSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, next, nextSuperleague, cancellationToken).ConfigureAwait(false);
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
                throw new AutomaticMovementNotFoundException(
                    $"Automatic movement for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            SeasonEntity? next = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken)
                .ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new AutomaticMovementNotFoundException(
                    $"Automatic movement for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureMovementExistsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
            return (source, next);
        }

        List<MovementEntity> automatic = await context.Movements
            .AsNoTracking()
            .Where(e => e.Kind == (int)MovementKind.AutomaticPromotion
                || e.Kind == (int)MovementKind.AutomaticRelegation
                || e.Kind == (int)MovementKind.QualifierIncumbent
                || e.Kind == (int)MovementKind.QualifierChallenger)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (automatic.Count == 0)
        {
            throw new AutomaticMovementNotFoundException("Automatic movement has not been resolved yet.");
        }

        int latestToSeasonId = automatic.Max(m => m.ToSeasonId);
        MovementEntity sample = automatic.First(m => m.ToSeasonId == latestToSeasonId);
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
            throw new InvalidOperationException("Automatic movement references unknown seasons.");
        }

        return (latestSource, latestNext);
    }

    internal static async Task EnsureMovementExistsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int count = await context.Movements
            .CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && (e.Kind == (int)MovementKind.AutomaticPromotion
                        || e.Kind == (int)MovementKind.AutomaticRelegation
                        || e.Kind == (int)MovementKind.QualifierIncumbent
                        || e.Kind == (int)MovementKind.QualifierChallenger),
                cancellationToken)
            .ConfigureAwait(false);
        if (count == 0)
        {
            throw new AutomaticMovementNotFoundException(
                $"Automatic movement for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<LeagueEntity> LoadNextSuperleagueAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague,
                cancellationToken)
            .ConfigureAwait(false);
        if (league is null)
        {
            throw new AutomaticMovementNotFoundException("Automatic movement has not been resolved yet.");
        }

        return league;
    }

    internal static async Task<GetAutomaticMovementResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        CancellationToken cancellationToken)
    {
        List<MovementEntity> movements = await LoadSuperleagueMovementsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        ValidateMovements(movements);

        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string?> images = await LoadImagesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await LoadNextMembershipsAsync(context, next, cancellationToken).ConfigureAwait(false);
        (LeagueEntity sourceSuperleague, List<SeasonStandingEntity> sourceSuperRows) = await LoadSourceSuperRowsAsync(context, source, cancellationToken).ConfigureAwait(false);

        int pool = await LoadPoolCountAsync(context, next, cancellationToken).ConfigureAwait(false);

        return MapResponse(saveId, source, next, nextSuperleague, movements, names, images, leaguesById, nextByAthlete, sourceSuperleague, sourceSuperRows, pool);
    }

    internal static async Task<List<MovementEntity>> LoadSuperleagueMovementsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        // Superleague read model stays scoped to Superleague kinds for backward
        // compatibility; feeder movements (MSS-058) use distinct kinds and are
        // exposed via the feeder qualifier/movement APIs with tier provenance.
        return await context.Movements
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.AutomaticPromotion
                    || e.Kind == (int)MovementKind.AutomaticRelegation
                    || e.Kind == (int)MovementKind.QualifierIncumbent
                    || e.Kind == (int)MovementKind.QualifierChallenger))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, string?>> LoadImagesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, LeagueEntity>> LoadLeaguesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, SeasonMembershipEntity>> LoadNextMembershipsAsync(
        SaveDbContext context,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<(LeagueEntity SourceSuperleague, List<SeasonStandingEntity> Rows)> LoadSourceSuperRowsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        LeagueEntity sourceSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> sourceSuperRows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == sourceSuperleague.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (sourceSuperleague, sourceSuperRows);
    }

    internal static async Task<int> LoadPoolCountAsync(
        SaveDbContext context,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static GetAutomaticMovementResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        List<MovementEntity> movements,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        LeagueEntity sourceSuperleague,
        List<SeasonStandingEntity> sourceSuperRows,
        int pool)
    {
        List<AutomaticMovementMember> safe = MapSafe(names, images, nextByAthlete, sourceSuperleague, nextSuperleague, sourceSuperRows);
        List<AutomaticMovementMember> promoted = MapKind(names, images, leaguesById, nextByAthlete, movements, MovementKind.AutomaticPromotion);
        List<AutomaticMovementMember> relegated = MapKind(names, images, leaguesById, nextByAthlete, movements, MovementKind.AutomaticRelegation);
        List<AutomaticMovementMember> incumbents = MapKind(names, images, leaguesById, nextByAthlete, movements, MovementKind.QualifierIncumbent);
        List<AutomaticMovementMember> challengers = MapKind(names, images, leaguesById, nextByAthlete, movements, MovementKind.QualifierChallenger);

        return new GetAutomaticMovementResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            nextSuperleague.Id,
            nextSuperleague.Name,
            safe,
            promoted,
            relegated,
            incumbents,
            challengers,
            pool,
            movements.Count);
    }

    internal static void ValidateMovements(List<MovementEntity> movements)
    {
        int promotions = movements.Count(m => m.Kind == (int)MovementKind.AutomaticPromotion);
        int relegations = movements.Count(m => m.Kind == (int)MovementKind.AutomaticRelegation);
        int incumbents = movements.Count(m => m.Kind == (int)MovementKind.QualifierIncumbent);
        int challengers = movements.Count(m => m.Kind == (int)MovementKind.QualifierChallenger);
        if (promotions != 8 || relegations != 8 || incumbents != 8 || challengers != 24)
        {
            throw new InvalidOperationException(
                $"Automatic movement must hold 8 promotions, 8 relegations, 8 incumbents and 24 challengers, was {promotions}/{relegations}/{incumbents}/{challengers}.");
        }

        if (movements.Count != 48)
        {
            throw new InvalidOperationException(
                $"Automatic movement must hold exactly 48 records, was {movements.Count}.");
        }
    }

    private static List<AutomaticMovementMember> MapSafe(
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        LeagueEntity sourceSuperleague,
        LeagueEntity nextSuperleague,
        List<SeasonStandingEntity> sourceSuperRows)
    {
        List<AutomaticMovementMember> members = new(16);
        foreach (SeasonStandingEntity row in sourceSuperRows.Where(r => r.SeasonRank >= 1 && r.SeasonRank <= 16).OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            images.TryGetValue(row.SaveAthleteId, out string? imageUrl);
            nextByAthlete.TryGetValue(row.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            string color = nextMembership is null ? "Unknown" : ((SportingColor)nextMembership.SportingColor).ToString();
            members.Add(new AutomaticMovementMember(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                color,
                sourceSuperleague.Id,
                sourceSuperleague.Name,
                row.SeasonRank,
                nextSuperleague.Id,
                nextSuperleague.Name,
                "Safe",
                imageUrl));
        }

        if (members.Count != 16)
        {
            throw new InvalidOperationException($"Automatic movement must expose exactly 16 safe athletes, was {members.Count}.");
        }

        return members;
    }

    private static List<AutomaticMovementMember> MapKind(
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        List<MovementEntity> movements,
        MovementKind kind)
    {
        List<AutomaticMovementMember> members = new();
        foreach (MovementEntity movement in movements
            .Where(m => m.Kind == (int)kind)
            .OrderBy(m => m.FromLeagueId)
            .ThenBy(m => m.FromSeasonRank))
        {
            names.TryGetValue(movement.SaveAthleteId, out string? name);
            images.TryGetValue(movement.SaveAthleteId, out string? imageUrl);
            leaguesById.TryGetValue(movement.FromLeagueId, out LeagueEntity? from);
            nextByAthlete.TryGetValue(movement.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            int toLeague = nextMembership?.LeagueId ?? movement.ToLeagueId;
            string toName = leaguesById.TryGetValue(toLeague, out LeagueEntity? to)
                ? to.Name
                : $"League {toLeague}";
            string color = nextMembership is null
                ? ((SportingColor)movement.SportingColor).ToString()
                : ((SportingColor)nextMembership.SportingColor).ToString();
            members.Add(new AutomaticMovementMember(
                movement.SaveAthleteId,
                name ?? $"Athlete {movement.SaveAthleteId}",
                color,
                movement.FromLeagueId,
                from?.Name ?? $"League {movement.FromLeagueId}",
                movement.FromSeasonRank,
                toLeague,
                toName,
                kind.ToString(),
                imageUrl));
        }

        return members;
    }
}
