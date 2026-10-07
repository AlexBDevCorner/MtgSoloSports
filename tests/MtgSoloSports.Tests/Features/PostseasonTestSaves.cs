using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;

namespace MtgSoloSports.Tests.Features;

/// <summary>
/// Builds temporary saves positioned just before a round-based postseason
/// event, shared by the step, progress and event-history tests. Setup mirrors
/// the existing QualifierTests / ColorCup*Tests helpers exactly.
/// </summary>
internal static class PostseasonTestSaves
{
    internal static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-post-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    /// <summary>Save with automatic movement resolved: the qualifier is the next legal action.</summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareQualifierAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Qual Step", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        await CompleteSeasonOneAsync(store, saveId).ConfigureAwait(false);
        await new CreateInauguralSuperleagueHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        await FillSeasonTwoFeedersAsync(store, saveId).ConfigureAwait(false);
        await InsertSyntheticSeasonTwoStandingsAsync(store, saveId).ConfigureAwait(false);
        await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        return (store, root, saveId);
    }

    /// <summary>
    /// Lifecycle-valid save driven only through advance-next-event until
    /// <paramref name="action"/> is the next legal action (e.g. RunQualifier
    /// after Season 2 league play and automatic movement).
    /// </summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareLifecycleAsync(ulong seed, ulong stream, string action)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Lifecycle Step", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        await DrainToActionAsync(store, saveId, action).ConfigureAwait(false);
        return (store, root, saveId);
    }

    /// <summary>
    /// MSS-067: advances an already-prepared save (for example a shared Season 1
    /// template fork) through <c>AdvanceToNextEvent</c> until
    /// <paramref name="action"/> is the next legal action. Lets lifecycle tests
    /// skip re-simulating Season 1 while keeping the exact lifecycle path.
    /// </summary>
    internal static async Task DrainToActionAsync(SaveStore store, Guid saveId, string action)
    {
        GetSeasonStatusHandler status = new(store);
        AdvanceToNextEventHandler advance = new(store);
        for (int guard = 0; guard < 200; guard++)
        {
            GetSeasonStatusResponse current = await status.HandleAsync(saveId).ConfigureAwait(false);
            if (current.LegalNextActions.Count > 0 && string.Equals(current.LegalNextActions[0], action, StringComparison.Ordinal))
            {
                return;
            }

            await advance.HandleAsync(saveId).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Lifecycle never reached {action}.");
    }

    /// <summary>Season 1 complete with the Color Cup field selected.</summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareColorCupAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Cup Step", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        await CompleteSeasonOneAsync(store, saveId).ConfigureAwait(false);
        await new SelectColorCupTeamsHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        return (store, root, saveId);
    }

    /// <summary>Even season 2 with two four-athlete creature types and the Type Cup field allocated.</summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTypeCupAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Type Step", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, 8).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        await new SelectTypeCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return (store, root, saveId);
    }

    /// <summary>
    /// Two completed Type Cups: Elf and Dwarf play Seasons 2 and 4, Goblin only
    /// Season 4. Athletes are single-typed so every squad is unambiguous.
    /// </summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTwoTypeCupsAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Two Type Cups", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> ids = await TakeAthletesAsync(store, saveId, 12).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, ids[..4], ["Elf"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, ids[4..8], ["Dwarf"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, ids[8..12], ["Goblin"]).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        RunTypeCupTeamHandler run = new(store);

        await CreateEvenSeasonAsync(store, saveId, 2, ids[..8]).ConfigureAwait(false);
        await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        await run.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);

        await CreateEvenSeasonAsync(store, saveId, 4, ids).ConfigureAwait(false);
        await select.HandleAsync(saveId, sourceSeasonNumber: 4).ConfigureAwait(false);
        await run.HandleAsync(saveId, sourceSeasonNumber: 4).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Take(count)
            .Select(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task SetTypesAsync(SaveStore store, Guid saveId, List<int> ids, string[] types)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToListAsync().ConfigureAwait(false);
        athletes.Count.ShouldBe(ids.Count);
        string json = JsonSerializer.Serialize(types);
        foreach (SaveAthleteEntity athlete in athletes)
        {
            athlete.CreatureTypesJson = json;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task CreateEvenSeasonAsync(SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity? season = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        if (season is null)
        {
            season = new SeasonEntity { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
            context.Seasons.Add(season);
        }
        else
        {
            season.IsComplete = true;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        if (league is null)
        {
            league = new LeagueEntity { SeasonId = season.Id, SportingColor = 0, Kind = (int)LeagueKind.Feeder, Name = "White League" };
            context.Leagues.Add(league);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        foreach (int id in activeIds)
        {
            bool exists = await context.SeasonMemberships.AnyAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == id).ConfigureAwait(false);
            if (!exists)
            {
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = season.Id,
                    LeagueId = league.Id,
                    SaveAthleteId = id,
                    SportingColor = 0,
                    DrawIndex = 0,
                });
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        AddZeroSelectionInputs(context, season, league, activeIds);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void AddZeroSelectionInputs(
        SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
    {
        for (int i = 0; i < activeIds.Count; i++)
        {
            context.SeasonStandings.Add(new SeasonStandingEntity
            {
                SeasonId = season.Id,
                LeagueId = league.Id,
                SaveAthleteId = activeIds[i],
                SeasonRank = i + 1,
                TotalChampionshipPointsThousandths = 0,
                TotalStageScoreThousandths = 0,
                TotalBaseScoreThousandths = 0,
                StageWins = 0,
                RoundWins = 0,
                StagePlaceCountsJson = "[]",
                RoundPlaceCountsJson = "[]",
                IsChampion = i == 0,
            });
        }

        foreach (int id in activeIds)
        {
            for (int stage = 23; stage <= 32; stage++)
            {
                context.StageStandings.Add(new StageStandingEntity
                {
                    SeasonId = season.Id,
                    LeagueId = league.Id,
                    StageId = stage,
                    StageNumber = stage,
                    SaveAthleteId = id,
                    StageRank = 10,
                    StageScoreThousandths = 0,
                    BaseScoreThousandths = 0,
                    ChampionshipPointsThousandths = 0,
                    RoundWins = 0,
                    RoundPlaceCountsJson = "[]",
                    EarnedBonusThousandths = 0,
                });
            }
        }
    }

    internal static async Task CompleteSeasonOneAsync(SaveStore store, Guid saveId)
    {
        CompleteStageForAllLeaguesHandler bulk = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            CompleteStageForAllLeaguesResponse completed = await bulk.HandleAsync(saveId).ConfigureAwait(false);
            completed.CompletedStage.ShouldBe(stage);
        }
    }

    internal static async Task FillSeasonTwoFeedersAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Feeder && e.FeederDivision == (int)MtgSoloSports.SimulationKernel.Leagues.FeederDivision.First)
            .ToListAsync().ConfigureAwait(false);
        foreach (LeagueEntity feeder in feeders.OrderBy(l => l.SportingColor))
        {
            List<SeasonMembershipEntity> poolForColor = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == null && e.SportingColor == feeder.SportingColor)
                .OrderBy(e => e.SaveAthleteId)
                .Take(4)
                .ToListAsync().ConfigureAwait(false);
            foreach (SeasonMembershipEntity pool in poolForColor)
            {
                pool.LeagueId = feeder.Id;
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    internal static async Task InsertSyntheticSeasonTwoStandingsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id)
            .OrderBy(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        foreach (LeagueEntity league in leagues)
        {
            List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == league.Id)
                .ToListAsync().ConfigureAwait(false);
            List<SeasonMembershipEntity> ordered = memberships.OrderBy(m => m.SaveAthleteId).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int rank = i + 1;
                context.SeasonStandings.Add(new SeasonStandingEntity
                {
                    SeasonId = seasonTwo.Id,
                    LeagueId = league.Id,
                    SaveAthleteId = ordered[i].SaveAthleteId,
                    SeasonRank = rank,
                    TotalChampionshipPointsThousandths = 0,
                    TotalStageScoreThousandths = 0,
                    TotalBaseScoreThousandths = 0,
                    StageWins = 0,
                    RoundWins = 0,
                    StagePlaceCountsJson = "[]",
                    RoundPlaceCountsJson = "[]",
                    IsChampion = rank == 1,
                });
            }
        }

        seasonTwo.IsComplete = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    internal static async Task<(long State, long Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }

    internal static async Task MoveRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        rng.State += 1;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    internal static void DeleteRoot(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
