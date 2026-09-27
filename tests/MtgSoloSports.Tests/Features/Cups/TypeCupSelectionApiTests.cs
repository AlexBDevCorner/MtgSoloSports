using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetTypeCupSelection;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class TypeCupSelectionApiTests
{
    [Fact]
    public async Task Select_EvenSeason_PersistsTeams_OnlyActive_WithoutCapping()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Active", 1111UL, 2222UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            (List<int> activeIds, List<int> poolIds, int seasonTwoId) =
                await SetupActivePoolSeasonAsync(store, saveId);

            SelectTypeCupTeamsHandler handler = new(store);
            SelectTypeCupTeamsResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            AssertActiveAllocation(response, activeIds, poolIds, seasonTwoId);
            await AssertScoresAndRatingsAsync(response);
            await AssertNotCappedAsync(store, saveId, response, seasonTwoId);
            await AssertQueryMatchesAsync(store, saveId, response);

            await Should.ThrowAsync<SelectTypeCupTeamsConflictException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_SameFourHumanWizards_FormsExactlyOneTeam()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Overlap", 3333UL, 4444UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            List<int> activeIds = await TakeAthletesAsync(store, saveId, 4);
            await SetTypesAsync(store, saveId, activeIds, ["Human", "Wizard"]);
            await CreateEvenSeasonAsync(store, saveId, activeIds, []);

            SelectTypeCupTeamsHandler handler = new(store);
            SelectTypeCupTeamsResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            // The same four athletes are the only candidates for both types:
            // only one four-distinct-athlete team can participate.
            response.TeamCount.ShouldBe(1);
            response.TotalSelected.ShouldBe(4);
            response.Teams.Count.ShouldBe(1);
            response.Teams[0].Members.Count.ShouldBe(4);
            response.Teams[0].CreatureType.ShouldBe("Human");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_DoesNotPersist_AndDoesNotCap()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Preview", 5555UL, 6666UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            int seasonTwoId = await CreateEvenSeasonAsync(store, saveId, activeIds, []);

            SelectTypeCupTeamsHandler handler = new(store);
            SelectTypeCupTeamsResponse preview = await handler.PreviewAsync(saveId, sourceSeasonNumber: 2);

            preview.TeamCount.ShouldBe(2);
            preview.TotalSelected.ShouldBe(8);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                bool any = await context.TypeCupSelections.AnyAsync(e => e.SourceSeasonId == seasonTwoId);
                any.ShouldBeFalse();
                List<SaveAthleteEntity> athletes = await context.SaveAthletes
                    .Where(e => activeIds.Contains(e.Id))
                    .ToListAsync();
                foreach (SaveAthleteEntity athlete in athletes)
                {
                    athlete.TypeCupNationality.ShouldBeNull();
                }
            }

            // Preview leaves no rows, so the real selection still runs.
            SelectTypeCupTeamsResponse selected = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            selected.TotalSelected.ShouldBe(preview.TotalSelected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_CappedAthlete_OnlyRepresentsNationality()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Capped", 7777UL, 8888UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..1], ["Human", "Wizard"]);
            await SetTypesAsync(store, saveId, activeIds[1..4], ["Human"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Wizard"]);
            await SetNationalityAsync(store, saveId, activeIds[0], "Human");
            await CreateEvenSeasonAsync(store, saveId, activeIds, []);

            SelectTypeCupTeamsHandler handler = new(store);
            SelectTypeCupTeamsResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            response.TeamCount.ShouldBe(2);
            TypeCupTeamResult human = response.Teams.Single(t => string.Equals(t.CreatureType, "Human", StringComparison.Ordinal));
            TypeCupTeamResult wizard = response.Teams.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
            human.Members.Select(m => m.SaveAthleteId).Contains(activeIds[0]).ShouldBeTrue();
            wizard.Members.Select(m => m.SaveAthleteId).Contains(activeIds[0]).ShouldBeFalse();

            // Selection respects but never overwrites nationality.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SaveAthleteEntity capped = await context.SaveAthletes.SingleAsync(e => e.Id == activeIds[0]);
                capped.TypeCupNationality.ShouldBe("Human");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_OddSeason_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Odd", 9999UL, 1010UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await MarkSeasonCompleteAsync(store, saveId, 1);

            SelectTypeCupTeamsHandler handler = new(store);
            await Should.ThrowAsync<SelectTypeCupTeamsConflictException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_BeforeSeasonComplete_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Early", 1112UL, 1314UL, UniverseTestCatalog.Build());
            SelectTypeCupTeamsHandler handler = new(store);
            await Should.ThrowAsync<SelectTypeCupTeamsConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Get_BeforeSelected_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Cup Missing", 1516UL, 1718UL, UniverseTestCatalog.Build());
            GetTypeCupSelectionHandler query = new(store);
            await Should.ThrowAsync<TypeCupSelectionNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(List<int> Active, List<int> Pool, int SeasonId)> SetupActivePoolSeasonAsync(
        SaveStore store, Guid saveId)
    {
        List<int> activeIds = await TakeAthletesAsync(store, saveId, 8).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[..4], ["Human"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[4..8], ["Wizard"]).ConfigureAwait(false);
        List<int> poolIds = await TakeAthletesAsync(store, saveId, 5, skip: 8).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, poolIds[..1], ["Human"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, poolIds[1..5], ["PoolOnly"]).ConfigureAwait(false);
        int seasonTwoId = await CreateEvenSeasonAsync(store, saveId, activeIds, poolIds).ConfigureAwait(false);
        return (activeIds, poolIds, seasonTwoId);
    }

    private static void AssertActiveAllocation(
        SelectTypeCupTeamsResponse response, List<int> activeIds, List<int> poolIds, int seasonTwoId)
    {
        response.SourceSeasonNumber.ShouldBe(2);
        response.SourceSeasonId.ShouldBe(seasonTwoId);
        response.TeamCount.ShouldBe(2);
        response.TotalSelected.ShouldBe(8);
        response.Teams.Count.ShouldBe(2);

        HashSet<int> selected = response.Teams.SelectMany(t => t.Members).Select(m => m.SaveAthleteId).ToHashSet();
        selected.Count.ShouldBe(8);
        foreach (int id in activeIds)
        {
            selected.Contains(id).ShouldBeTrue();
        }

        foreach (int id in poolIds)
        {
            selected.Contains(id).ShouldBeFalse();
        }
    }

    private static Task AssertScoresAndRatingsAsync(SelectTypeCupTeamsResponse response)
    {
        foreach (TypeCupTeamResult team in response.Teams)
        {
            team.Members.Count.ShouldBe(4);
            team.Members.Select(m => m.SelectionRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
            for (int i = 1; i < team.Members.Count; i++)
            {
                (team.Members[i].FinalRatingThousandths <= team.Members[i - 1].FinalRatingThousandths).ShouldBeTrue();
            }

            foreach (TypeCupTeamMember member in team.Members)
            {
                member.CreatureType.ShouldBe(team.CreatureType);
                member.TypeRank.ShouldBeGreaterThanOrEqualTo(1);
                member.BonusNormThousandths.ShouldBeInRange(0, 1000);
                int expected = SelectionScore.Combine(
                    member.BonusNormThousandths,
                    member.PerformanceNormThousandths,
                    member.FormNormThousandths,
                    member.PrestigeNormThousandths,
                    response.CupBonusWeightPermille,
                    response.CupPerformanceWeightPermille,
                    response.CupFormWeightPermille,
                    response.CupPrestigeWeightPermille).Thousandths;
                member.FinalRatingThousandths.ShouldBe(expected);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task AssertNotCappedAsync(
        SaveStore store, Guid saveId, SelectTypeCupTeamsResponse response, int seasonTwoId)
    {
        HashSet<int> selected = response.Teams.SelectMany(t => t.Members).Select(m => m.SaveAthleteId).ToHashSet();
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> selectedAthletes = await context.SaveAthletes
            .Where(e => selected.Contains(e.Id))
            .ToListAsync().ConfigureAwait(false);
        foreach (SaveAthleteEntity athlete in selectedAthletes)
        {
            athlete.TypeCupNationality.ShouldBeNull();
        }

        List<TypeCupSelectionEntity> rows = await context.TypeCupSelections
            .Where(e => e.SourceSeasonId == seasonTwoId)
            .ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(8);
    }

    private static async Task AssertQueryMatchesAsync(
        SaveStore store,
        Guid saveId,
        SelectTypeCupTeamsResponse response)
    {
        GetTypeCupSelectionHandler query = new(store);
        GetTypeCupSelectionResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        summary.TotalSelected.ShouldBe(response.TotalSelected);
        summary.TeamCount.ShouldBe(response.TeamCount);
        foreach (GetTypeCupTeam team in summary.Teams)
        {
            TypeCupTeamResult expected = response.Teams.Single(t => string.Equals(t.CreatureType, team.CreatureType, StringComparison.Ordinal));
            team.Members.Count.ShouldBe(4);
            foreach (GetTypeCupMember member in team.Members)
            {
                TypeCupTeamMember match = expected.Members.Single(m => m.SaveAthleteId == member.SaveAthleteId);
                member.SelectionRank.ShouldBe(match.SelectionRank);
                member.FinalRatingThousandths.ShouldBe(match.FinalRatingThousandths);
                member.TypeRank.ShouldBe(match.TypeRank);
            }
        }

        GetTypeCupSelectionResponse again = await query.HandleAsync(saveId, sourceSeasonNumber: response.SourceSeasonNumber).ConfigureAwait(false);
        again.TotalSelected.ShouldBe(response.TotalSelected);
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count, int skip = 0)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Skip(skip)
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

    private static async Task SetNationalityAsync(SaveStore store, Guid saveId, int athleteId, string nationality)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == athleteId).ConfigureAwait(false);
        athlete.TypeCupNationality = nationality;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<int> CreateEvenSeasonAsync(
        SaveStore store, Guid saveId, List<int> activeIds, List<int> poolIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = new() { SeasonNumber = 2, HasSuperleague = false, IsComplete = true };
        context.Seasons.Add(season);
        await context.SaveChangesAsync().ConfigureAwait(false);
        LeagueEntity league = new()
        {
            SeasonId = season.Id,
            SportingColor = 0,
            Kind = (int)LeagueKind.Feeder,
            Name = "White League",
        };
        context.Leagues.Add(league);
        await context.SaveChangesAsync().ConfigureAwait(false);
        foreach (int id in activeIds)
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

        foreach (int id in poolIds)
        {
            context.SeasonMemberships.Add(new SeasonMembershipEntity
            {
                SeasonId = season.Id,
                LeagueId = null,
                SaveAthleteId = id,
                SportingColor = 0,
                DrawIndex = 0,
            });
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        return season.Id;
    }

    private static async Task MarkSeasonCompleteAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        season.IsComplete = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-" + Guid.NewGuid().ToString("N"));
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
