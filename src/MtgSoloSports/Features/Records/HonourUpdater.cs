using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Transactional persistence of official honours. Called from season
/// finalization inside the caller's SQLite transaction so honours, RNG state
/// and standings commit atomically. Idempotent: existing rows are kept,
/// missing rows are inserted, corrupt champion references abort.
/// Rebuildable from <c>SeasonStandings</c> plus <c>Leagues</c> without round
/// payloads; read paths lazily ensure sync so saves created before MSS-022
/// remain readable.
/// </summary>
public static class HonourUpdater
{
    /// <summary>
    /// Syncs honours for one finalized season. Must run after
    /// <c>SeasonStanding</c> rows are staged (still inside the caller's
    /// transaction). One honour per league champion; exactly one champion per
    /// league is required.
    /// </summary>
    public static async Task SyncSeasonAsync(
        SaveDbContext context,
        SeasonEntity season,
        IReadOnlyList<LeagueEntity> leagues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(leagues);
        foreach (LeagueEntity league in leagues)
        {
            await SyncLeagueAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds all honours from authoritative standings. Used for audits and
    /// for saves created before MSS-022. Never deletes sporting history;
    /// only upserts honour rows.
    /// </summary>
    public static async Task RebuildAllAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<SeasonEntity> seasons = await context.Seasons
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .Where(e => e.IsChampion)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(e => e.Id);
        foreach (SeasonStandingEntity champion in champions)
        {
            if (!seasonsById.TryGetValue(champion.SeasonId, out SeasonEntity? season))
            {
                throw new InvalidOperationException($"Season standing {champion.Id} references unknown season {champion.SeasonId}.");
            }

            if (!leaguesById.TryGetValue(champion.LeagueId, out LeagueEntity? league))
            {
                throw new InvalidOperationException($"Season standing {champion.Id} references unknown league {champion.LeagueId}.");
            }

            await UpsertAsync(context, season, league, champion, cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures league honours exist for every finalized season. Read paths call
    /// this before serving so pre-MSS-022 saves backfill without a dedicated
    /// migration of sporting data (schema migration stays separate from
    /// sporting history). Cup honours (MSS-024+) are ignored here: they are
    /// major honours but have no <c>SeasonStandings</c> champion row, so only
    /// league kinds participate in the comparison and rebuilds never delete
    /// Cup rows.
    /// </summary>
    public static async Task EnsureSyncedAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        int championCount = await context.SeasonStandings
            .CountAsync(e => e.IsChampion, cancellationToken)
            .ConfigureAwait(false);
        int leagueHonourCount = await context.Honours
            .CountAsync(
                e => e.Kind == (int)HonourKind.FeederTitle || e.Kind == (int)HonourKind.SuperleagueTitle,
                cancellationToken)
            .ConfigureAwait(false);
        if (leagueHonourCount == championCount)
        {
            return;
        }

        if (leagueHonourCount > championCount)
        {
            throw new InvalidOperationException($"League honours ({leagueHonourCount}) exceed champions ({championCount}); sporting state is corrupt.");
        }

        await RebuildAllAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SyncLeagueAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.IsChampion)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (champions.Count != 1)
        {
            throw new InvalidOperationException($"League '{league.Name}' must have exactly one champion for honours, was {champions.Count}.");
        }

        await UpsertAsync(context, season, league, champions[0], cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UpsertAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        SeasonStandingEntity champion,
        CancellationToken cancellationToken)
    {
        HonourKind kind = HonourKindMapper.FromLeagueKind(league.Kind);
        HonourEntity? existing = await context.Honours
            .SingleOrDefaultAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            context.Honours.Add(new HonourEntity
            {
                SeasonId = season.Id,
                SeasonNumber = season.SeasonNumber,
                LeagueId = league.Id,
                LeagueName = league.Name,
                LeagueKind = league.Kind,
                SaveAthleteId = champion.SaveAthleteId,
                Kind = (int)kind,
            });
            return;
        }

        if (existing.SaveAthleteId != champion.SaveAthleteId)
        {
            throw new InvalidOperationException(
                $"Honour for league '{league.Name}' season {season.SeasonNumber} conflicts with the persisted champion; sporting state is corrupt.");
        }

        existing.SeasonNumber = season.SeasonNumber;
        existing.LeagueName = league.Name;
        existing.LeagueKind = league.Kind;
        existing.Kind = (int)kind;
    }
}
