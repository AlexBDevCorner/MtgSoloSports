using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

/// <summary>
/// MSS-113: Chaos Cup achievements and stage paths on the athlete profile.
/// Synthetic Chaos rows are setup (the profile projection is the subject), so
/// no full 1024-athlete tournament is simulated; the named assertions (frozen
/// Pool attribution, six-stage path fidelity, provisional honesty) still
/// execute against persisted draws/standings.
/// </summary>
public sealed class AthleteChaosHistoryTests
{
    [Fact]
    public async Task PoolChampion_KeepsHistoricTitle_AfterPromotionToF3_WithoutNationality()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Pool Champ", 1101UL, 1102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertEntrantAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, isPoolInvite: true);
            int[] groups = [7, 3, 2, 1, 2, 1];
            int[] ranks = [4, 6, 2, 9, 3, 1];
            for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
            {
                await InsertDrawAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage, groups[stage]);
                await InsertStandingAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage, groups[stage], ranks[stage]);
            }

            await InsertChaosHonourAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, HonourKind.ChaosCupIndividualChampion);

            GetAthleteProfileResponse before = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            before.ChaosHistory.Count.ShouldBe(1);
            AthleteChaosHistoryDto edition = before.ChaosHistory.Single();
            edition.SourceSeasonNumber.ShouldBe(1);
            edition.IsPoolInvite.ShouldBeTrue();
            edition.SourceLabel.ShouldBe("Pool");
            edition.IsFinalist.ShouldBeTrue();
            edition.FinalPlace.ShouldBe(1);
            edition.Medal.ShouldBe("Gold");
            edition.ExitStage.ShouldBe("Finalist");
            edition.IsComplete.ShouldBeTrue();
            edition.Stages.Count.ShouldBe(6);
            edition.Stages.Select(s => s.GroupNumber).ShouldBe(groups);
            edition.Stages.Select(s => s.GroupRank).ShouldBe(ranks.Select(r => (int?)r));
            before.Honours.Any(h => string.Equals(h.HonourKind, nameof(HonourKind.ChaosCupIndividualChampion), StringComparison.Ordinal)).ShouldBeTrue();
            before.Card.TypeCupNationality.ShouldBeNull();

            // Later promotion to F3: current career moves, frozen Chaos stays Pool.
            await PromoteCareerToF3Async(store, saveId, athleteId);

            GetAthleteProfileResponse after = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            AthleteChaosHistoryDto retained = after.ChaosHistory.Single();
            retained.IsPoolInvite.ShouldBeTrue();
            retained.SourceLabel.ShouldBe("Pool");
            retained.FinalPlace.ShouldBe(1);
            retained.Medal.ShouldBe("Gold");
            after.Honours.Any(h => string.Equals(h.HonourKind, nameof(HonourKind.ChaosCupIndividualChampion), StringComparison.Ordinal)).ShouldBeTrue();
            after.Card.TypeCupNationality.ShouldBeNull();
            after.Career.CurrentLeagueName.ShouldBe("F3 White");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SixStagePath_MatchesPersistedDrawsAndStandings()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Six Path", 2101UL, 2102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertEntrantAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, isPoolInvite: false);
            int[] groups = [5, 3, 2, 1, 2, 1];
            int[] ranks = [5, 10, 16, 2, 1, 2];
            for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
            {
                await InsertDrawAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage, groups[stage]);
                await InsertStandingAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage, groups[stage], ranks[stage]);
            }

            GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            AthleteChaosHistoryDto edition = profile.ChaosHistory.Single();
            edition.StagesEntered.ShouldBe(6);
            edition.IsFinalist.ShouldBeTrue();
            edition.IsEliminated.ShouldBeFalse();
            edition.FinalPlace.ShouldBe(2);
            edition.Medal.ShouldBe("Silver");
            edition.ExitStage.ShouldBe("Finalist");
            edition.IsComplete.ShouldBeTrue();
            edition.Stages.Count.ShouldBe(6);
            for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
            {
                AthleteChaosStageDto row = edition.Stages.Single(s => s.StageIndex == stage);
                row.GroupNumber.ShouldBe(groups[stage]);
                row.GroupCount.ShouldBe(ChaosCupFormat.GroupCountForStage(stage));
                row.IsFinal.ShouldBe(ChaosCupFormat.IsFinalStage(stage));
                row.IsComplete.ShouldBeTrue();
                row.IsOfficial.ShouldBeTrue();
                row.GroupRank.ShouldBe(ranks[stage]);
                if (ChaosCupFormat.IsFinalStage(stage))
                {
                    row.IsAdvanced.ShouldBeNull();
                }
                else
                {
                    row.IsAdvanced.ShouldBe((bool?)true);
                }
            }

            // Existing sections stay intact alongside Chaos.
            profile.Seasons.Count.ShouldBeGreaterThan(0);
            profile.CupHistory.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EliminatedPath_StopsAtExitStage_WithoutFabricatedFinalPlace()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Eliminated", 2201UL, 2202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertEntrantAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, isPoolInvite: false);
            await InsertDrawAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage: 0, groupNumber: 4);
            await InsertStandingAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage: 0, groupNumber: 4, groupRank: 5);
            await InsertDrawAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage: 1, groupNumber: 2);
            await InsertStandingAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage: 1, groupNumber: 2, groupRank: 17);

            GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            AthleteChaosHistoryDto edition = profile.ChaosHistory.Single();
            edition.IsFinalist.ShouldBeFalse();
            edition.IsEliminated.ShouldBeTrue();
            edition.EliminatedStageIndex.ShouldBe(1);
            edition.ExitStage.ShouldBe(ChaosCupFormat.ChaosExitStage.EliminatedInStageWith16Groups.ToString());
            edition.FinalPlace.ShouldBeNull();
            edition.Medal.ShouldBe("None");
            edition.IsComplete.ShouldBeTrue();
            edition.Stages.Count.ShouldBe(2);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IncompleteEdition_StaysProvisional_WithoutRankOrMedal()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Provisional", 3101UL, 3102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertEntrantAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, isPoolInvite: false);
            await InsertDrawAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, stage: 0, groupNumber: 11);

            GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            AthleteChaosHistoryDto edition = profile.ChaosHistory.Single();
            edition.IsFinalist.ShouldBeFalse();
            edition.IsEliminated.ShouldBeFalse();
            edition.EliminatedStageIndex.ShouldBeNull();
            edition.ExitStage.ShouldBeNull();
            edition.FinalPlace.ShouldBeNull();
            edition.Medal.ShouldBe("None");
            edition.IsComplete.ShouldBeFalse();
            AthleteChaosStageDto stage = edition.Stages.Single();
            stage.StageIndex.ShouldBe(0);
            stage.GroupNumber.ShouldBe(11);
            stage.IsComplete.ShouldBeFalse();
            stage.IsOfficial.ShouldBeFalse();
            stage.GroupRank.ShouldBeNull();
            stage.IsAdvanced.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AbsentChaos_ReturnsEmpty_AndKeepsLeagueHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Absent", 4101UL, 4102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonCount = await SeasonCountAsync(store, saveId);
            int athleteId = await FirstAthleteAsync(store, saveId);

            GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId);
            profile.ChaosHistory.ShouldBeEmpty();
            profile.Honours.ShouldBeEmpty();
            profile.Seasons.Count.ShouldBe(seasonCount);
            profile.CupHistory.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<int> FirstAthleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).FirstAsync().ConfigureAwait(false);
    }

    private static async Task<int> SeasonIdAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Seasons.AsNoTracking().Where(e => e.SeasonNumber == seasonNumber).Select(e => e.Id).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> SeasonCountAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Seasons.AsNoTracking().CountAsync().ConfigureAwait(false);
    }

    private static async Task InsertEntrantAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, bool isPoolInvite)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int color = await context.SaveAthletes.AsNoTracking().Where(e => e.Id == athleteId).Select(e => e.SportingColor).SingleAsync().ConfigureAwait(false);
        context.ChaosCupEntrants.Add(new ChaosCupEntrantEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            SaveAthleteId = athleteId,
            SportingColor = color,
            SourceLeagueId = isPoolInvite ? null : 7,
            SourceLeagueKind = isPoolInvite ? 0 : (int)LeagueKind.Feeder,
            SourceFeederDivision = isPoolInvite ? 0 : (int)FeederDivision.First,
            IsPoolInvite = isPoolInvite,
            EffectiveBonusThousandths = 1200,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertDrawAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, int stage, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.ChaosCupDraws.Add(new ChaosCupDrawEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            StageIndex = stage,
            GroupNumber = groupNumber,
            SaveAthleteId = athleteId,
            DrawIndex = 0,
            GroupSize = ChaosCupFormat.GroupSize,
            StageGroupCount = ChaosCupFormat.GroupCountForStage(stage),
            RulesVersion = 1,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            RngBeforeState = 1,
            RngBeforeStream = 2,
            RngAfterState = 3,
            RngAfterStream = 4,
            DrawChecksum = $"draw-{stage}-{groupNumber}",
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertStandingAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, int stage, int groupNumber, int groupRank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int color = await context.SaveAthletes.AsNoTracking().Where(e => e.Id == athleteId).Select(e => e.SportingColor).SingleAsync().ConfigureAwait(false);
        bool isFinal = ChaosCupFormat.IsFinalStage(stage);
        bool isAdvanced = !isFinal && groupRank <= ChaosCupFormat.AdvancingPerGroup;
        context.ChaosCupGroupStandings.Add(new ChaosCupGroupStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            StageIndex = stage,
            GroupNumber = groupNumber,
            SaveAthleteId = athleteId,
            GroupRank = groupRank,
            GroupScoreThousandths = 100000 + (groupRank * 100),
            BaseScoreThousandths = 90000,
            RoundWins = 1,
            RoundPlaceCountsJson = "[]",
            SportingColor = color,
            IsAdvanced = isAdvanced,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertChaosHonourAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, HonourKind kind)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.Honours.Add(new HonourEntity
        {
            SeasonId = seasonId,
            SeasonNumber = seasonNumber,
            LeagueId = -4,
            LeagueName = "Chaos Cup",
            LeagueKind = -5,
            SaveAthleteId = athleteId,
            Kind = (int)kind,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task PromoteCareerToF3Async(SaveStore store, Guid saveId, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        AthleteCareerEntity career = await context.AthleteCareers.SingleAsync(e => e.SaveAthleteId == athleteId).ConfigureAwait(false);
        career.CurrentLeagueId = 999;
        career.CurrentLeagueName = "F3 White";
        career.CurrentLeagueKind = (int)LeagueKind.Feeder;
        career.IsActive = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-athlete-chaos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private static void DeleteRoot(string root)
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
