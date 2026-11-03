using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
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
    /// MSS-122: real Season 2 league play complete (S1 postseason plus 32 S2
    /// stages through production <c>AdvanceToNextEvent</c>), with automatic
    /// movement still pending. Built once per process; consumers fork an
    /// isolated copy and still execute the movement themselves.
    /// </summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkSeason2CompleteAsync(string prefix)
    {
        return ForkAsync("Season2Complete", BuildSeason2CompleteAsync, prefix);
    }

    /// <summary>
    /// MSS-122: real Season 2 complete with automatic movement resolved, so the
    /// qualifier is the next legal action. Built once per process through the
    /// production lifecycle (no synthetic standings); consumers fork an
    /// isolated copy and still execute the qualifier run(s) themselves.
    /// </summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkSeason2QualifierPendingAsync(string prefix)
    {
        return ForkAsync("Season2QualifierPending", BuildSeason2QualifierPendingAsync, prefix);
    }

    /// <summary>
    /// MSS-122: real Season 2 lifecycle through Type Cup team selection (tournament
    /// catalog with a &gt;32-team field), with the team event still pending.
    /// Built once per process; consumers fork an isolated copy and still
    /// execute the team rounds themselves.
    /// </summary>
    internal static Task<(SaveStore Store, string Root, Guid SaveId)> ForkTypeCupSelectedAsync(string prefix)
    {
        return ForkAsync("TypeCupSelected", BuildTypeCupSelectedAsync, prefix);
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

    /// <summary>
    /// Disables Chaos for one forked save by pushing the activation past any real season.
    /// Isolated movement/qualifier/rebalance tests use this to keep the previous chain
    /// when Chaos history is incidental setup; lifecycle tests keep eligible saves.
    /// </summary>
    internal static async Task DisableChaosAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        var activation = await context.ChaosCupActivations.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        activation.EffectiveFromSeasonNumber = 9999;
        await context.SaveChangesAsync().ConfigureAwait(false);
        store.InvalidateMigrationVerified(saveId);
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
        // Incidental qualifier/movement coverage keeps the previous chain:
        // disable Chaos so Season 2 movement does not require a tournament.
        await DisableChaosAsync(store, saveId).ConfigureAwait(false);
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

    private static async Task<Template> BuildSeason2CompleteAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "Season1Complete", BuildSeason1CompleteAsync, "mtgsolosports-shared-s2-").ConfigureAwait(false);
        await PostseasonTestSaves.DrainToActionAsync(store, saveId, SeasonLifecycleActions.ResolveAutomaticMovement).ConfigureAwait(false);
        GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        if (status.LegalNextActions.Count != 1 || !string.Equals(status.LegalNextActions[0], SeasonLifecycleActions.ResolveAutomaticMovement, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Season2Complete template did not reach automatic movement.");
        }

        return new Template(root, saveId);
    }

    private static async Task<Template> BuildSeason2QualifierPendingAsync()
    {
        var (store, root, saveId) = await ForkAsync(
            "Season2Complete", BuildSeason2CompleteAsync, "mtgsolosports-shared-qualpending-").ConfigureAwait(false);
        await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        if (status.LegalNextActions.Count != 1 || !string.Equals(status.LegalNextActions[0], SeasonLifecycleActions.RunQualifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Season2QualifierPending template did not reach qualifier.");
        }

        return new Template(root, saveId);
    }

    private static async Task<Template> BuildTypeCupSelectedAsync()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-shared-typecup-");
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Shared TypeCup", 79079UL, 80079UL, BuildTournamentCatalog()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        GetSeasonStatusHandler status = new(store);
        AdvanceToNextEventHandler advance = new(store);
        for (int guard = 0; guard < 200; guard++)
        {
            GetSeasonStatusResponse current = await status.HandleAsync(saveId).ConfigureAwait(false);
            if (current.LegalNextActions.Count == 1
                && string.Equals(current.LegalNextActions[0], SeasonLifecycleActions.SelectTypeCup, StringComparison.Ordinal))
            {
                break;
            }

            await advance.HandleAsync(saveId).ConfigureAwait(false);
        }

        GetSeasonStatusResponse pending = await status.HandleAsync(saveId).ConfigureAwait(false);
        if (pending.LegalNextActions.Count != 1 || !string.Equals(pending.LegalNextActions[0], SeasonLifecycleActions.SelectTypeCup, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TypeCupSelected template did not reach Type Cup selection.");
        }

        await new SelectTypeCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        GetSeasonStatusResponse selected = await status.HandleAsync(saveId).ConfigureAwait(false);
        if (selected.LegalNextActions.Count != 1 || !string.Equals(selected.LegalNextActions[0], SeasonLifecycleActions.RunTypeCupTeam, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TypeCupSelected template did not reach Type Cup team run.");
        }

        return new Template(root, saveId);
    }

    internal static IReadOnlyList<CatalogAthlete> BuildTournamentCatalog()
    {
        const int types = 64;
        const int perColor = 4;
        List<CatalogAthlete> athletes = new(types * 8 * perColor);
        for (int type = 0; type < types; type++)
        {
            string typeName = $"MSS122-Type-{type:D2}";
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                (IReadOnlyList<string> FrontColors, string ManaCost, bool IsArtifact) shape = color switch
                {
                    SportingColor.White => (["W"], "{W}", false),
                    SportingColor.Blue => (["U"], "{U}", false),
                    SportingColor.Black => (["B"], "{B}", false),
                    SportingColor.Red => (["R"], "{R}", false),
                    SportingColor.Green => (["G"], "{G}", false),
                    SportingColor.Multicolor => (["W", "U"], "{W}{U}", false),
                    SportingColor.Hybrid => (["W", "U"], "{W/U}", false),
                    _ => ([], "{4}", true),
                };
                for (int rep = 0; rep < perColor; rep++)
                {
                    athletes.Add(new CatalogAthlete(
                        $"MSS122 {typeName} {color} {rep:D2}",
                        color,
                        [typeName],
                        shape.IsArtifact,
                        false,
                        color == SportingColor.Hybrid,
                        shape.FrontColors,
                        shape.ManaCost,
                        $"Creature — {typeName}",
                        null,
                        "t122"));
                }
            }
        }

        return athletes;
    }

    private static async Task<Guid> SeedSavesRootAsync(string savesRoot, string key, Func<Task<Template>> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        Template template = await GetOrBuildAsync(key, build).ConfigureAwait(false);
        Directory.CreateDirectory(savesRoot);
        SaveStore templateStore = TestSaveStores.CreateStoreForRoot(template.Root);
        string source = SaveFileNaming.GetSaveFilePath(template.Root, template.SaveId);
        string destination = SaveFileNaming.GetSaveFilePath(savesRoot, template.SaveId);
        await TestSaveStores.CopySaveFileLockedAsync(templateStore, template.SaveId, source, destination).ConfigureAwait(false);
        return template.SaveId;
    }
}
