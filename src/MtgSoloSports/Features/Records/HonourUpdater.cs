using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Transactional persistence of official honours. Called from season
/// finalization inside the caller's SQLite transaction so honours, RNG state
/// and standings commit atomically. Idempotent: existing rows are kept,
/// missing rows are inserted, corrupt podium references abort.
/// Rebuildable from <c>SeasonStandings</c> plus <c>Leagues</c> without round
/// payloads; read paths lazily ensure sync so saves created before MSS-022
/// remain readable. MSS-047: each league season contributes three honours
/// (1st/2nd/3rd) with distinct kinds; titles remain win-only via
/// <see cref="HonourKindMapper.IsChampionKind"/>.
/// </summary>
public static class HonourUpdater
{
    /// <summary>
    /// Syncs honours for one finalized season. Must run after
    /// <c>SeasonStanding</c> rows are staged (still inside the caller's
    /// transaction). Three honours per league (ranks 1/2/3); exactly one
    /// athlete per podium rank is required.
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
    /// Rebuilds all league podium honours from authoritative standings. Used for audits and
    /// for saves created before MSS-022/MSS-047. Never deletes sporting history;
    /// only upserts honour rows. Cup honours are never deleted here.
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
        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(e => e.Id);
        foreach (SeasonStandingEntity podium in podiums)
        {
            if (!seasonsById.TryGetValue(podium.SeasonId, out SeasonEntity? season))
            {
                throw new InvalidOperationException($"Season standing {podium.Id} references unknown season {podium.SeasonId}.");
            }

            if (!leaguesById.TryGetValue(podium.LeagueId, out LeagueEntity? league))
            {
                throw new InvalidOperationException($"Season standing {podium.Id} references unknown league {podium.LeagueId}.");
            }

            await UpsertAsync(context, season, league, podium, cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures league podium honours exist for every finalized season. Read paths call
    /// this before serving so pre-MSS-022 and pre-MSS-047 saves backfill without a dedicated
    /// migration of sporting data (schema migration stays separate from
    /// sporting history). Cup honours (MSS-024+) are ignored here: they are
    /// major honours but have no <c>SeasonStandings</c> podium row, so only
    /// league kinds participate in the comparison and rebuilds never delete
    /// Cup rows.
    /// </summary>
    public static async Task EnsureSyncedAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        int podiumCount = await context.SeasonStandings
            .CountAsync(e => e.SeasonRank >= 1 && e.SeasonRank <= 3, cancellationToken)
            .ConfigureAwait(false);
        int leagueHonourCount = await context.Honours
            .CountAsync(
                e => e.Kind == (int)HonourKind.FeederTitle
                    || e.Kind == (int)HonourKind.FeederRunnerUp
                    || e.Kind == (int)HonourKind.FeederThirdPlace
                    || e.Kind == (int)HonourKind.SuperleagueTitle
                    || e.Kind == (int)HonourKind.SuperleagueRunnerUp
                    || e.Kind == (int)HonourKind.SuperleagueThirdPlace,
                cancellationToken)
            .ConfigureAwait(false);
        if (leagueHonourCount == podiumCount)
        {
            return;
        }

        if (leagueHonourCount > podiumCount)
        {
            throw new InvalidOperationException($"League honours ({leagueHonourCount}) exceed podiums ({podiumCount}); sporting state is corrupt.");
        }

        await RebuildAllAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SyncLeagueAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.SeasonRank >= 1 && e.SeasonRank <= 3)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (podiums.Count != 3)
        {
            throw new InvalidOperationException($"League '{league.Name}' must have exactly three podium finishers for honours, was {podiums.Count}.");
        }

        if (podiums[0].SeasonRank != 1 || podiums[1].SeasonRank != 2 || podiums[2].SeasonRank != 3)
        {
            throw new InvalidOperationException($"League '{league.Name}' has corrupt podium ranks for honours.");
        }

        HashSet<int> athletes = new();
        foreach (SeasonStandingEntity podium in podiums)
        {
            if (!athletes.Add(podium.SaveAthleteId))
            {
                throw new InvalidOperationException($"League '{league.Name}' has duplicate podium athlete {podium.SaveAthleteId}.");
            }

            bool expectedChampion = podium.SeasonRank == 1;
            if (podium.IsChampion != expectedChampion)
            {
                throw new InvalidOperationException($"League '{league.Name}' rank {podium.SeasonRank} has corrupt champion flag for honours.");
            }
        }

        foreach (SeasonStandingEntity podium in podiums)
        {
            await UpsertAsync(context, season, league, podium, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task UpsertAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        SeasonStandingEntity podium,
        CancellationToken cancellationToken)
    {
        if (podium.SeasonRank < 1 || podium.SeasonRank > 3)
        {
            throw new InvalidOperationException($"Season standing {podium.Id} rank {podium.SeasonRank} is not a podium honour.");
        }

        HonourKind kind = HonourKindMapper.FromLeagueRank(league.Kind, podium.SeasonRank);
        HonourEntity? existing = await context.Honours
            .SingleOrDefaultAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.Kind == (int)kind,
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
                SaveAthleteId = podium.SaveAthleteId,
                Kind = (int)kind,
            });
            return;
        }

        if (existing.SaveAthleteId != podium.SaveAthleteId)
        {
            throw new InvalidOperationException(
                $"Honour for league '{league.Name}' season {season.SeasonNumber} rank {podium.SeasonRank} conflicts with the persisted podium; sporting state is corrupt.");
        }

        existing.SeasonNumber = season.SeasonNumber;
        existing.LeagueName = league.Name;
        existing.LeagueKind = league.Kind;
        existing.Kind = (int)kind;
    }
}
