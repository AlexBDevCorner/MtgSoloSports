using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.GetRebalanceResult;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted feeder
/// rebalancing for a postseason transition (never resimulates): per-color final
/// 32-athlete rosters with pool-draw and displacement provenance plus pool and
/// movement counts. Without <paramref name="fromSeasonNumber"/> returns the latest
/// rebalanced transition; with it returns that season's transition.
/// Throws <see cref="RebalanceResultNotFoundException"/> (404) when rebalancing
/// has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetRebalanceResultHandler
{
    private readonly SaveStore _store;

    public GetRebalanceResultHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetRebalanceResultResponse> HandleAsync(
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
            if (source is null)
            {
                throw new RebalanceResultNotFoundException(
                    $"Feeder rebalancing for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            SeasonEntity? next = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken)
                .ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new RebalanceResultNotFoundException(
                    $"Feeder rebalancing for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureRebalancedAsync(context, source, next, cancellationToken).ConfigureAwait(false);
            return (source, next);
        }

        List<MovementEntity> rebalanced = await context.Movements
            .AsNoTracking()
            .Where(e => e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rebalanced.Count == 0)
        {
            throw new RebalanceResultNotFoundException("Feeder rebalancing has not been resolved yet.");
        }

        int latestToSeasonId = rebalanced.Max(m => m.ToSeasonId);
        MovementEntity sample = rebalanced.First(m => m.ToSeasonId == latestToSeasonId);
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
            throw new InvalidOperationException("Rebalancing references unknown seasons.");
        }

        return (latestSource, latestNext);
    }

    internal static async Task EnsureRebalancedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int count = await context.Movements
            .CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement),
                cancellationToken)
            .ConfigureAwait(false);
        if (count == 0)
        {
            List<LeagueEntity> feeders = await context.Leagues
                .AsNoTracking()
                .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (feeders.Count == 0)
            {
                throw new RebalanceResultNotFoundException(
                    $"Feeder rebalancing for Season {source.SeasonNumber} has not been resolved yet.");
            }

            bool allFull = true;
            foreach (LeagueEntity feeder in feeders)
            {
                int members = await context.SeasonMemberships
                    .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == feeder.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (members != 32)
                {
                    allFull = false;
                    break;
                }
            }

            if (!allFull)
            {
                throw new RebalanceResultNotFoundException(
                    $"Feeder rebalancing for Season {source.SeasonNumber} has not been resolved yet.");
            }
        }
    }

    internal static async Task<GetRebalanceResultResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await Features.Simulation.AdvanceRound.AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await LoadValidatedFeedersAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        List<MovementEntity> movements = await LoadRebalanceMovementsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        return await MapResponseAsync(context, saveId, source, next, nextFeeders, movements, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<LeagueEntity>> LoadValidatedFeedersAsync(
        SaveDbContext context,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> nextFeeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.SportingColor)
            .ThenBy(e => e.FeederDivision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (nextFeeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {nextFeeders.Count}.");
        }

        foreach (LeagueEntity feeder in nextFeeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == feeder.Id, cancellationToken)
                .ConfigureAwait(false);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must contain exactly {rules.LeagueSize} athletes after rebalancing, was {count}.");
            }
        }

        return nextFeeders;
    }

    internal static async Task<List<MovementEntity>> LoadRebalanceMovementsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.Movements
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<GetRebalanceResultResponse> MapResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        List<MovementEntity> movements,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string?> images = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        (IReadOnlyList<RebalanceMovementMember> departed, IReadOnlyList<RebalanceMovementMember> returned) =
            await LoadSuperleagueTransfersAsync(context, source, next, names, images, leaguesById, cancellationToken).ConfigureAwait(false);
        List<RebalanceColorResult> colors = MapColorResults(nextFeeders, movements, departed, returned, rules);

        List<RebalanceMovementMember> draws = RebalanceFeedersHandler.MapMovements(movements, MovementKind.RebalanceDraw, names, leaguesById, images);
        List<RebalanceMovementMember> displacedMembers = RebalanceFeedersHandler.MapMovements(movements, MovementKind.RebalanceDisplacement, names, leaguesById, images);

        int pool = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);

        return new GetRebalanceResultResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            colors,
            draws,
            displacedMembers,
            departed,
            returned,
            draws.Count,
            displacedMembers.Count,
            departed.Count,
            returned.Count,
            pool,
            movements.Count);
    }

    internal static async Task<(IReadOnlyList<RebalanceMovementMember> Departed, IReadOnlyList<RebalanceMovementMember> Returned)> LoadSuperleagueTransfersAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> sourceFeeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity? sourceSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity? nextSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        if (nextSuperleague is null)
        {
            throw new InvalidOperationException($"Season {next.SeasonNumber} has no Superleague.");
        }

        List<SeasonMembershipEntity> sourceMemberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SeasonMembershipEntity> nextMemberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> rankByAthlete = await RebalanceFeedersHandler.LoadSourceRanksAsync(
            context, source, sourceFeeders, sourceSuperleague, cancellationToken).ConfigureAwait(false);
        return RebalanceSuperleagueTransfers.Map(
            sourceFeeders, sourceSuperleague, nextFeeders, nextSuperleague,
            sourceMemberships, nextMemberships, rankByAthlete, names, images, leaguesById);
    }

    internal static List<RebalanceColorResult> MapColorResults(
        List<LeagueEntity> nextFeeders,
        List<MovementEntity> movements,
        IReadOnlyList<RebalanceMovementMember> departed,
        IReadOnlyList<RebalanceMovementMember> returned,
        RulesV1 rules)
    {
        Dictionary<int, int> drawnByLeague = movements
            .Where(m => m.Kind == (int)MovementKind.RebalanceDraw)
            .GroupBy(m => m.ToLeagueId)
            .ToDictionary(g => g.Key, g => g.Count());
        Dictionary<int, int> displacedByLeague = movements
            .Where(m => m.Kind == (int)MovementKind.RebalanceDisplacement)
            .GroupBy(m => m.FromLeagueId)
            .ToDictionary(g => g.Key, g => g.Count());
        IReadOnlyDictionary<string, (int Departed, int Returned)> transfersByColor =
            RebalanceSuperleagueTransfers.CountsByColor(departed, returned);

        List<RebalanceColorResult> colors = new(nextFeeders.Count);
        foreach (LeagueEntity feeder in nextFeeders)
        {
            drawnByLeague.TryGetValue(feeder.Id, out int drawn);
            displacedByLeague.TryGetValue(feeder.Id, out int displaced);
            int provisional = rules.LeagueSize - drawn + displaced;
            string colorName = ((SportingColor)feeder.SportingColor).ToString();
            transfersByColor.TryGetValue(colorName, out (int Departed, int Returned) transfers);
            int viaTransfers = rules.LeagueSize - transfers.Departed + transfers.Returned;
            if (viaTransfers != provisional)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' provisional count {provisional} does not match Superleague transfers (32 - {transfers.Departed} + {transfers.Returned} = {viaTransfers}).");
            }

            colors.Add(new RebalanceColorResult(
                feeder.Id,
                feeder.Name,
                colorName,
                rules.LeagueSize,
                transfers.Departed,
                transfers.Returned,
                provisional,
                displaced,
                drawn,
                rules.LeagueSize));
        }

        return colors;
    }
}
