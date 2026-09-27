using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Diagnostics.LongRunChecksum;
using MtgSoloSports.Features.Diagnostics.LongRunInvariants;
using MtgSoloSports.Features.Diagnostics.LongRunStats;
using MtgSoloSports.Features.History.GetRoundReplay;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// Opt-in long-run benchmark harness. Simulates N seasons in bulk (chunked to
/// respect <see cref="SimulateSeasonsHandler.MaxSeasonsPerRequest"/>) and then
/// measures runtime, database size, memory behavior, common dashboard/profile/
/// records query latency and historical round replay latency plus the stable
/// checksum and structural invariants. Never runs in normal CI: callers gate on
/// the <c>MTG_LONGRUN</c> environment variable and choose seasons via
/// <c>MTG_LONGRUN_SEASONS</c> (100 routine, 1000 stress outside CI).
/// </summary>
public static class LongRunBenchmarkHarness
{
    public sealed record Options(
        int Seasons,
        ulong Seed,
        ulong Stream,
        string SaveName);

    public sealed record SeasonTiming(int SeasonNumber, TimeSpan Elapsed);

    public sealed record Report(
        Guid SaveId,
        int SeasonsRequested,
        int SeasonsCompleted,
        IReadOnlyList<SeasonTiming> SeasonTimings,
        TimeSpan TotalSimulation,
        long DatabaseBytes,
        int Rounds,
        int StageStandings,
        int SeasonStandings,
        TimeSpan StatusLatency,
        TimeSpan StandingsLatency,
        TimeSpan ProfileLatency,
        TimeSpan RecordsLatency,
        TimeSpan ReplayLatency,
        long ManagedMemoryBytes,
        string Checksum,
        bool InvariantsPassed);

    public static Options ReadFromEnvironmentOrDefault(int defaultSeasons, ulong seed, ulong stream)
    {
        int seasons = defaultSeasons;
        string? raw = Environment.GetEnvironmentVariable("MTG_LONGRUN_SEASONS");
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 1 && parsed <= 1000)
        {
            seasons = parsed;
        }

        return new Options(seasons, seed, stream, $"LongRun {seasons}");
    }

    public static async Task<Report> RunAsync(
        SaveStore store,
        IReadOnlyList<MtgSoloSports.Features.Catalog.ImportCatalog.CatalogAthlete> catalog,
        Options options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Seasons < 1 || options.Seasons > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Seasons must be 1..1000.");
        }

        SaveStore.CreationRecord created = await store.CreateAsync(
            options.SaveName, options.Seed, options.Stream, catalog, cancellationToken).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;

        SimulateSeasonsHandler sim = new(store);
        List<SeasonTiming> timings = new(options.Seasons);
        Stopwatch total = Stopwatch.StartNew();
        int completed = 0;
        int remaining = options.Seasons;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, SimulateSeasonsHandler.MaxSeasonsPerRequest);
            for (int i = 0; i < chunk; i++)
            {
                Stopwatch perSeason = Stopwatch.StartNew();
                SimulateSeasonsResponse response = await sim.HandleAsync(
                    saveId, new SimulateSeasonsRequest(1), cancellationToken).ConfigureAwait(false);
                perSeason.Stop();
                completed = checked(completed + response.SeasonsCompleted);
                timings.Add(new SeasonTiming(response.EndSeasonNumber, perSeason.Elapsed));
            }

            remaining -= chunk;
        }

        total.Stop();

        GetLongRunStatsHandler statsHandler = new(store);
        GetLongRunStatsResponse stats = await statsHandler.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);

        TimeSpan statusLatency = await TimeAsync(
            () => new GetSeasonStatusHandler(store).HandleAsync(saveId, cancellationToken)).ConfigureAwait(false);
        TimeSpan standingsLatency = await TimeStandingsAsync(store, saveId, cancellationToken).ConfigureAwait(false);
        TimeSpan profileLatency = await TimeAsync(
            () => new GetAthleteProfileHandler(store).HandleAsync(saveId, 1, cancellationToken)).ConfigureAwait(false);
        TimeSpan recordsLatency = await TimeAsync(
            () => new GetRecordsHandler(store).HandleAsync(saveId, cancellationToken)).ConfigureAwait(false);
        TimeSpan replayLatency = await TimeReplayAsync(store, saveId, cancellationToken).ConfigureAwait(false);

        GetLongRunChecksumResponse checksum =
            await new GetLongRunChecksumHandler(store).HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
        ValidateLongRunInvariantsResponse invariants =
            await new ValidateLongRunInvariantsHandler(store).HandleAsync(saveId, cancellationToken).ConfigureAwait(false);

        long managedMemory = GC.GetTotalMemory(forceFullCollection: false);
        return new Report(
            saveId, options.Seasons, completed, timings, total.Elapsed,
            stats.DatabaseBytes, stats.Rounds, stats.StageStandings, stats.SeasonStandings,
            statusLatency, standingsLatency, profileLatency, recordsLatency, replayLatency,
            managedMemory, checksum.Checksum, invariants.Passed);
    }

    internal static async Task<TimeSpan> TimeAsync<T>(Func<Task<T>> action)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _ = await action().ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed;
    }

    internal static async Task<TimeSpan> TimeStandingsAsync(SaveStore store, Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int leagueId = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == context.Seasons.OrderByDescending(s => s.SeasonNumber).Select(s => s.Id).First())
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
        Stopwatch sw = Stopwatch.StartNew();
        _ = await new GetCurrentStandingsHandler(store).HandleAsync(saveId, leagueId, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed;
    }

    internal static async Task<TimeSpan> TimeReplayAsync(SaveStore store, Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        var season = await context.Seasons.AsNoTracking().OrderBy(e => e.SeasonNumber).FirstAsync(cancellationToken).ConfigureAwait(false);
        int leagueId = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == season.Id).OrderBy(e => e.Id).Select(e => e.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch sw = Stopwatch.StartNew();
        _ = await new GetHistoryRoundReplayHandler(store).HandleAsync(saveId, season.SeasonNumber, leagueId, 1, 1, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed;
    }
}
