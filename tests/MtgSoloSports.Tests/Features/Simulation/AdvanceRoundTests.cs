using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class AdvanceRoundTests
{
    [Fact]
    public async Task AdvanceFirstRound_PersistsOneRoundPayload_AdvancesRng()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round One", 101UL, 202UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);
            ulong rngBefore = created.Detail.RngState;

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse response = await handler.HandleAsync(created.Detail.SaveId, leagueId);

            response.SeasonNumber.ShouldBe(1);
            response.StageNumber.ShouldBe(1);
            response.RoundNumber.ShouldBe(1);
            response.RulesVersion.ShouldBe(RulesV1.RulesVersion);
            response.Placements.Count.ShouldBe(32);
            response.PayloadChecksum.Length.ShouldBe(64);
            response.RngBeforeState.ShouldBe(rngBefore);
            response.RngAfterState.ShouldNotBe(response.RngBeforeState);

            AssertRoundOnePayload(response);
            await AssertPersistedAsync(store, created.Detail.SaveId, leagueId, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalFirstRound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord first = await store.CreateAsync("Round Golden One", 9001UL, 7002UL, UniverseTestCatalog.Build());
            SaveStore.CreationRecord second = await store.CreateAsync("Round Golden Two", 9001UL, 7002UL, UniverseTestCatalog.Build());

            int firstLeague = await LeagueIdByColorAsync(store, first.Detail.SaveId, "White");
            int secondLeague = await LeagueIdByColorAsync(store, second.Detail.SaveId, "White");

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse firstRound = await handler.HandleAsync(first.Detail.SaveId, firstLeague);
            AdvanceRoundResponse secondRound = await handler.HandleAsync(second.Detail.SaveId, secondLeague);

            firstRound.PayloadChecksum.ShouldBe(secondRound.PayloadChecksum);
            firstRound.Placements.Select(p => p.Name).ShouldBe(secondRound.Placements.Select(p => p.Name).ToList());
            firstRound.RngAfterState.ShouldBe(secondRound.RngAfterState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SecondRound_ChainsCumulativeTotals_AndRankMovement()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Chain", 11UL, 12UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse first = await handler.HandleAsync(created.Detail.SaveId, leagueId);
            AdvanceRoundResponse second = await handler.HandleAsync(created.Detail.SaveId, leagueId);

            second.StageNumber.ShouldBe(1);
            second.RoundNumber.ShouldBe(2);
            second.RngBeforeState.ShouldBe(first.RngAfterState);

            Dictionary<string, int> afterFirst = first.Placements.ToDictionary(p => p.Name, p => p.CumulativeAfterThousandths, StringComparer.Ordinal);
            foreach (AdvanceRoundPlacement placement in second.Placements)
            {
                placement.CumulativeBeforeThousandths.ShouldBe(afterFirst[placement.Name]);
                placement.CumulativeAfterThousandths.ShouldBe(
                    checked(placement.CumulativeBeforeThousandths + placement.FinalThousandths));
                placement.RankMovement.ShouldBe(placement.RankBefore - placement.RankAfter);
            }

            Dictionary<string, int> rankAfterFirst = first.Placements.ToDictionary(p => p.Name, p => p.RankAfter, StringComparer.Ordinal);
            foreach (AdvanceRoundPlacement placement in second.Placements)
            {
                placement.RankBefore.ShouldBe(rankAfterFirst[placement.Name]);
            }

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int rounds = await context.Rounds.CountAsync(e => e.LeagueId == leagueId);
            rounds.ShouldBe(2);
            StageEntity stage = await context.Stages.SingleAsync(e => e.LeagueId == leagueId);
            stage.CompletedRounds.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SixteenRounds_ThenSeventeenthConflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Sixteen", 21UL, 22UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse last = null!;
            for (int i = 1; i <= 16; i++)
            {
                last = await handler.HandleAsync(created.Detail.SaveId, leagueId);
                last.RoundNumber.ShouldBe(i);
            }

            last.Placements.Count.ShouldBe(32);
            await Should.ThrowAsync<AdvanceRoundConflictException>(() => handler.HandleAsync(created.Detail.SaveId, leagueId));

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int rounds = await context.Rounds.CountAsync(e => e.LeagueId == leagueId);
            rounds.ShouldBe(16);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedRound_ReplaysWithoutResimulation()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Replay", 31UL, 32UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse response = await handler.HandleAsync(created.Detail.SaveId, leagueId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            RoundEntity row = await context.Rounds.AsNoTracking().SingleAsync(e => e.LeagueId == leagueId);
            row.PayloadChecksum.ShouldBe(response.PayloadChecksum);
            row.PayloadChecksum.Length.ShouldBe(64);

            RoundPayloadDocument payload = RoundPayloadDocument.FromJson(row.PayloadJson);
            payload.Checksum.ShouldBe(response.PayloadChecksum);
            payload.Placements.Count.ShouldBe(32);
            payload.StageNumber.ShouldBe(1);
            payload.RoundNumber.ShouldBe(1);
            payload.Placements.Select(p => p.Name).ShouldBe(response.Placements.Select(p => p.Name).ToList());
            payload.Placements.Select(p => p.FinalThousandths).ShouldBe(response.Placements.Select(p => p.FinalThousandths).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RngState_CommitsInSameTransaction_AsRoundResult()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Tx", 41UL, 42UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse response = await handler.HandleAsync(created.Detail.SaveId, leagueId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1);
            unchecked
            {
                ((ulong)rng.State).ShouldBe(response.RngAfterState);
                ((ulong)rng.Stream).ShouldBe(response.RngAfterStream);
            }

            RoundEntity round = await context.Rounds.AsNoTracking().SingleAsync(e => e.LeagueId == leagueId);
            unchecked
            {
                ((ulong)round.RngAfterState).ShouldBe(response.RngAfterState);
                ((ulong)round.RngBeforeState).ShouldBe(response.RngBeforeState);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownLeague_ThrowsConflict_UnknownSave_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Errors", 51UL, 52UL, UniverseTestCatalog.Build());
            AdvanceRoundHandler handler = new(store);

            await Should.ThrowAsync<AdvanceRoundConflictException>(() => handler.HandleAsync(created.Detail.SaveId, 999999));
            await Should.ThrowAsync<SaveNotFoundException>(() => handler.HandleAsync(Guid.NewGuid(), 1));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty, 1));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(created.Detail.SaveId, 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpgradePreRoundSimulationSave_MigratesSchemaAndSimulatesRound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Round Upgrade", 61UL, 62UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            int leagueId;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                leagueId = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).FirstAsync();
            }

            // Simulate a save file created before MSS-008: remove the Stages/Rounds
            // and StageStandings schema plus their EF migration history entries so
            // the file matches the pre-round-simulation schema.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Rounds\";");
                await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"StageStandings\";");
                await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Stages\";");
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260926130000_AddRoundSimulation';");
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260926140000_AddStageCompletion';");
            }

            AdvanceRoundHandler handler = new(store);
            AdvanceRoundResponse response = await handler.HandleAsync(saveId, leagueId);

            response.StageNumber.ShouldBe(1);
            response.RoundNumber.ShouldBe(1);
            response.Placements.Count.ShouldBe(32);
            response.RngBeforeState.ShouldBe(created.Detail.RngState);
            response.RngAfterState.ShouldNotBe(response.RngBeforeState);

            using SaveDbContext verify = store.OpenDbContext(saveId);
            (await verify.Stages.CountAsync(e => e.LeagueId == leagueId)).ShouldBe(1);
            (await verify.Rounds.CountAsync(e => e.LeagueId == leagueId)).ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Invariants_RejectCorruptedPayload()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        RoundPayloadDocument valid = BuildValidPayload(rules);
        RoundInvariants.ValidateSimulation(valid, rules, valid.RngBeforeState, valid.RngBeforeStream);

        RoundPayloadEntry victim = valid.Placements[0];
        List<RoundPayloadEntry> tampered = valid.Placements
            .Select(e => string.Equals(e.Name, victim.Name, StringComparison.Ordinal) ? e with { BaseThousandths = e.BaseThousandths + 1000 } : e)
            .ToList();
        RoundPayloadDocument corrupted = valid with { Placements = tampered };
        Should.Throw<InvalidOperationException>(() => RoundInvariants.ValidateSimulation(corrupted, rules, valid.RngBeforeState, valid.RngBeforeStream));

        RoundPayloadDocument badRng = valid with { RngBeforeState = valid.RngBeforeState + 1UL };
        Should.Throw<InvalidOperationException>(() => RoundInvariants.ValidateSimulation(badRng, rules, valid.RngBeforeState, valid.RngBeforeStream));
    }

    private static void AssertRoundOnePayload(AdvanceRoundResponse response)
    {
        int[] expectedBase =
        [
            77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
        ];
        HashSet<int> positions = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AdvanceRoundPlacement placement in response.Placements)
        {
            positions.Add(placement.Position).ShouldBeTrue();
            names.Add(placement.Name).ShouldBeTrue();
            placement.BaseThousandths.ShouldBe(expectedBase[placement.Position - 1] * 1000);
            placement.ActiveBonusThousandths.ShouldBe(0);
            placement.FinalThousandths.ShouldBe(placement.BaseThousandths);
            placement.CumulativeBeforeThousandths.ShouldBe(0);
            placement.CumulativeAfterThousandths.ShouldBe(placement.FinalThousandths);
            placement.RankAfter.ShouldBe(placement.Position);
        }

        positions.SetEquals(Enumerable.Range(1, 32)).ShouldBeTrue();
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, int leagueId, AdvanceRoundResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<RoundEntity> rounds = await context.Rounds.AsNoTracking().Where(e => e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(1);
        rounds[0].StageNumber.ShouldBe(1);
        rounds[0].RoundNumber.ShouldBe(1);
        rounds[0].PayloadChecksum.ShouldBe(response.PayloadChecksum);

        List<StageEntity> stages = await context.Stages.AsNoTracking().Where(e => e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        stages.Count.ShouldBe(1);
        stages[0].StageNumber.ShouldBe(1);
        stages[0].CompletedRounds.ShouldBe(1);

        int placementRows = await context.Rounds.CountAsync().ConfigureAwait(false);
        placementRows.ShouldBe(1);
    }

    private static RoundPayloadDocument BuildValidPayload(RulesV1 rules)
    {
        List<RoundPayloadEntry> entries = new(rules.LeagueSize);
        for (int i = 0; i < rules.LeagueSize; i++)
        {
            int basePoints = rules.ScoringTable[i] * 1000;
            string name = $"Athlete {i:D2}";
            entries.Add(new RoundPayloadEntry(
                i + 100,
                name,
                i + 1,
                basePoints,
                0,
                basePoints,
                0,
                basePoints,
                0,
                0,
                0));
        }

        List<RoundPayloadEntry> byName = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Dictionary<string, int> rankBefore = new(StringComparer.Ordinal);
        for (int i = 0; i < byName.Count; i++)
        {
            rankBefore[byName[i].Name] = i + 1;
        }

        List<RoundPayloadEntry> byAfter = entries.OrderByDescending(e => e.CumulativeAfterThousandths).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        Dictionary<string, int> rankAfter = new(StringComparer.Ordinal);
        for (int i = 0; i < byAfter.Count; i++)
        {
            rankAfter[byAfter[i].Name] = i + 1;
        }

        List<RoundPayloadEntry> ranked = entries
            .Select(e => e with
            {
                RankBefore = rankBefore[e.Name],
                RankAfter = rankAfter[e.Name],
                RankMovement = rankBefore[e.Name] - rankAfter[e.Name],
            })
            .OrderBy(e => e.Position)
            .ToList();

        return new RoundPayloadDocument(
            RoundPayloadDocument.PayloadVersion,
            rules.Version,
            1,
            7,
            1,
            1,
            111UL,
            222UL,
            333UL,
            222UL,
            "placeholder",
            ranked);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static async Task<int> LeagueIdByColorAsync(SaveStore store, Guid saveId, string color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().ToListAsync().ConfigureAwait(false);
        LeagueEntity league = leagues.Single(l => string.Equals(l.Name, $"{color} League", StringComparison.Ordinal));
        return league.Id;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-round-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
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
