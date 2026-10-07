using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;

namespace MtgSoloSports.Tests.Features;

/// <summary>
/// MSS-067: process-wide shared prepared saves. Dozens of integration tests
/// each simulate a full Season 1 (plus standard postseason scaffolding) from
/// scratch before asserting their own feature. The sporting setup is identical
/// in each case — the same seeded universe run through the same deterministic
/// handlers — so the suite builds each template once per test run and hands
/// every test an isolated file-copy fork (see <see cref="TestSaveStores"/>).
/// Forks are fully independent temp roots; no mutable sporting state is shared,
/// tests stay order-independent and individually runnable (a lone test simply
/// builds the template it needs on demand).
/// Canonical seeds: templates use fixed seeds because the covered invariants
/// (selection shape, qualifier counts, record keys, story emission) are
/// universe-independent. Seed-specific golden coverage stays pinned in the
/// pre-refactor golden tests, which keep their own builds.
/// </summary>
internal static class SharedSaveTemplates
{
    private sealed record Template(string Root, Guid SaveId);

    private static readonly ConcurrentDictionary<string, Template> Built = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Season 1 complete via the fast path (proven equivalent to 32x bulk).</summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkSeason1CompleteAsync(string prefix)
    {
        return ForkAsync("Season1Complete", BuildSeason1CompleteAsync, prefix);
    }

    /// <summary>Season 1 complete with the Color Cup field selected.</summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkColorCupSelectedAsync(string prefix)
    {
        return ForkAsync("ColorCupSelected", BuildColorCupSelectedAsync, prefix);
    }

    /// <summary>
    /// Season 1 complete, inaugural resolved, Season 2 feeders filled with
    /// synthetic standings: the qualifier/automatic-movement starting state
    /// (same shape as <c>PostseasonTestSaves.PrepareQualifierAsync</c> before
    /// the movement resolution).
    /// </summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkQualifierPreResolveAsync(string prefix)
    {
        return ForkAsync("QualifierPreResolve", BuildQualifierPreResolveAsync, prefix);
    }

    /// <summary>Qualifier starting state with automatic movement resolved.</summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkQualifierResolvedAsync(string prefix)
    {
        return ForkAsync("QualifierResolved", BuildQualifierResolvedAsync, prefix);
    }

    /// <summary>
    /// Season 1 plus its postseason (inaugural, rebalance, Cups, next season
    /// started) via bulk simulation: the read-model state ordinarily produced
    /// by <c>SimulateSeasons(1)</c> from a fresh save.
    /// </summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkSeason1PlusCupsAsync(string prefix)
    {
        return ForkAsync("Season1PlusCups", BuildSeason1PlusCupsAsync, prefix);
    }

    /// <summary>
    /// MSS-067: seeds an API test's saves root with a copy of a shared
    /// template, so endpoint tests can skip the catalog-import, save-creation
    /// and full-season simulation performed over HTTP and focus on the
    /// endpoints under test. Returns the seeded save id.
    /// </summary>
    internal static Task<Guid> SeedSeason1CompleteAsync(string savesRoot)
    {
        return SeedSavesRootAsync(savesRoot, "Season1Complete", BuildSeason1CompleteAsync);
    }

    /// <summary>Seeds a Season 1 plus postseason save (see <c>ForkSeason1PlusCupsAsync</c>).</summary>
    internal static Task<Guid> SeedSeason1PlusCupsAsync(string savesRoot)
    {
        return SeedSavesRootAsync(savesRoot, "Season1PlusCups", BuildSeason1PlusCupsAsync);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> ForkAsync(
        string key,
        Func<Task<Template>> build,
        string prefix)
    {
        Template template = await GetOrBuildAsync(key, build).ConfigureAwait(false);
        SaveStore templateStore = TestSaveStores.CreateStoreForRoot(template.Root);
        return await TestSaveStores.ForkAsync(templateStore, template.SaveId, prefix).ConfigureAwait(false);
    }

    private static async Task<Template> GetOrBuildAsync(string key, Func<Task<Template>> build)
    {
        if (Built.TryGetValue(key, out Template? existing))
        {
            return existing;
        }

        SemaphoreSlim gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Built.TryGetValue(key, out existing))
            {
                return existing;
            }

            Template fresh = await build().ConfigureAwait(false);
            Built[key] = fresh;
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Template> BuildSeason1CompleteAsync()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-shared-season1-");
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Shared Season1", 4242UL, 777UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await new CompleteSeasonHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return new Template(root, created.Detail.SaveId);
    }

    private static async Task<Template> BuildColorCupSelectedAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "Season1Complete", BuildSeason1CompleteAsync, "mtgsolosports-shared-colorcup-").ConfigureAwait(false);
        await new SelectColorCupTeamsHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        return new Template(root, saveId);
    }

    private static async Task<Template> BuildQualifierPreResolveAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "Season1Complete", BuildSeason1CompleteAsync, "mtgsolosports-shared-qualpre-").ConfigureAwait(false);
        await new CreateInauguralSuperleagueHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        await PostseasonTestSaves.FillSeasonTwoFeedersAsync(store, saveId).ConfigureAwait(false);
        await PostseasonTestSaves.InsertSyntheticSeasonTwoStandingsAsync(store, saveId).ConfigureAwait(false);
        return new Template(root, saveId);
    }

    private static async Task<Template> BuildQualifierResolvedAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "QualifierPreResolve", BuildQualifierPreResolveAsync, "mtgsolosports-shared-qual-").ConfigureAwait(false);
        await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        return new Template(root, saveId);
    }

    private static async Task<Template> BuildSeason1PlusCupsAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "Season1Complete", BuildSeason1CompleteAsync, "mtgsolosports-shared-pluscups-").ConfigureAwait(false);
        await new SimulateSeasonsHandler(store).HandleAsync(saveId, new SimulateSeasonsRequest(1)).ConfigureAwait(false);
        return new Template(root, saveId);
    }

    private static async Task<Guid> SeedSavesRootAsync(string savesRoot, string key, Func<Task<Template>> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        Template template = await GetOrBuildAsync(key, build).ConfigureAwait(false);
        Directory.CreateDirectory(savesRoot);
        SaveStore templateStore = TestSaveStores.CreateStoreForRoot(template.Root);
        using (SaveDbContext context = templateStore.OpenDbContext(template.SaveId))
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
        string source = SaveFileNaming.GetSaveFilePath(template.Root, template.SaveId);
        File.Copy(source, SaveFileNaming.GetSaveFilePath(savesRoot, template.SaveId));
        return template.SaveId;
    }
}
