using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

/// <summary>
/// MSS-048: athlete Cup history is projected from persisted Cup standings
/// (individual + team) with season/place/result and unambiguous event/team
/// context, newest first, without replaying competitions.
/// </summary>
public sealed class AthleteCupHistoryTests
{
    [Fact]
    public async Task IndividualHistory_ShowsSeasonPlaceAndResult()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Indiv", 101UL, 202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertIndividualAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, cupRank: 2, medal: 2, color: SportingColor.Red);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            AthleteCupHistoryDto entry = history.Single();
            entry.SourceSeasonNumber.ShouldBe(1);
            entry.Cup.ShouldBe("Color");
            entry.Event.ShouldBe("Individual");
            entry.EventName.ShouldBe("Color Cup");
            entry.TeamKey.ShouldBe("red");
            entry.TeamName.ShouldBe("Red");
            entry.Place.ShouldBe(2);
            entry.Medal.ShouldBe("Silver");
            entry.GroupRank.ShouldBeNull();
            entry.GroupNumber.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ColorTeamHistory_ShowsTeamPlace_AsTeamEvent()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Color Team", 303UL, 404UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertColorTeamAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, color: SportingColor.Blue, teamRank: 1, medal: 1, groupRank: 3);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            AthleteCupHistoryDto entry = history.Single();
            entry.SourceSeasonNumber.ShouldBe(1);
            entry.Cup.ShouldBe("Color");
            entry.Event.ShouldBe("Team");
            entry.EventName.ShouldBe("Color Cup Team");
            entry.TeamKey.ShouldBe("blue");
            entry.TeamName.ShouldBe("Blue");
            entry.Place.ShouldBe(1);
            entry.Medal.ShouldBe("Gold");
            entry.GroupRank.ShouldBe(3);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TypeTeamHistory_ShowsTeamPlace_AsTeamEvent()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Type Team", 505UL, 606UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertTypeTeamAsync(store, saveId, seasonId, seasonNumber: 2, athleteId, creatureType: "Elf", teamRank: 2, medal: 2, groupRank: 1);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            AthleteCupHistoryDto entry = history.Single();
            entry.SourceSeasonNumber.ShouldBe(2);
            entry.Cup.ShouldBe("Type");
            entry.Event.ShouldBe("Team");
            entry.EventName.ShouldBe("Type Cup Team");
            entry.TeamKey.ShouldBe("Elf");
            entry.TeamName.ShouldBe("Elf");
            entry.Place.ShouldBe(2);
            entry.Medal.ShouldBe("Silver");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SameSeasonIndividualAndTeam_AreIndependentRows_NewestFirst()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Both", 707UL, 808UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonOne = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            await InsertIndividualAsync(store, saveId, seasonOne, seasonNumber: 1, athleteId, cupRank: 5, medal: 0, color: SportingColor.Green);
            await InsertColorTeamAsync(store, saveId, seasonOne, seasonNumber: 1, athleteId, color: SportingColor.Green, teamRank: 4, medal: 0, groupRank: 2);

            // Second (newer) Type Cup appearance for the same athlete. Cup standings carry their own
            // SourceSeasonNumber; no Season row is needed and none is created so the profile keeps
            // its persisted League history path.
            int fakeSeasonTwo = checked(seasonOne + 1000000);
            await InsertTypeTeamAsync(store, saveId, fakeSeasonTwo, seasonNumber: 2, athleteId, creatureType: "Goblin", teamRank: 3, medal: 3, groupRank: 5);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            history.Count.ShouldBe(3);
            // Newest season first; same-season Color rows keep Individual before Team.
            history.Select(e => $"{e.SourceSeasonNumber}:{e.Cup}:{e.Event}:{e.Place}")
                .ShouldBe(["2:Type:Team:3", "1:Color:Individual:5", "1:Color:Team:4"]);
            history.Where(e => e.SourceSeasonNumber == 1).Select(e => e.Event).ShouldBe(["Individual", "Team"]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NoCupHistory_ReturnsEmpty()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Empty", 909UL, 1010UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int athleteId = await FirstAthleteAsync(store, saveId);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            history.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IncompleteTeamEvent_IsExcluded_UntilTeamStandingExists()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Incomplete", 1111UL, 1212UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            // Leg without a matching team standing: edition selected but team event unfinished.
            await InsertColorLegOnlyAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, color: SportingColor.White, groupRank: 1);

            List<AthleteCupHistoryDto> before = await LoadHistoryAsync(store, saveId, athleteId);
            before.ShouldBeEmpty();

            await InsertColorTeamStandingOnlyAsync(store, saveId, seasonId, seasonNumber: 1, color: SportingColor.White, teamRank: 2, medal: 2);

            List<AthleteCupHistoryDto> after = await LoadHistoryAsync(store, saveId, athleteId);
            AthleteCupHistoryDto entry = after.Single();
            entry.Place.ShouldBe(2);
            entry.Medal.ShouldBe("Silver");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task HistoryUsesPersistedTeam_NotCurrentMembership()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Frozen", 1313UL, 1414UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);

            // Persisted leg says Red even though the athlete's current snapshot says Blue.
            await InsertColorTeamAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, color: SportingColor.Red, teamRank: 2, medal: 2, groupRank: 4);
            await SetAthleteColorAsync(store, saveId, athleteId, SportingColor.Blue);

            List<AthleteCupHistoryDto> history = await LoadHistoryAsync(store, saveId, athleteId);
            AthleteCupHistoryDto entry = history.Single();
            entry.TeamKey.ShouldBe("red");
            entry.TeamName.ShouldBe("Red");
            entry.Place.ShouldBe(2);

            // Type Cup: persisted leg says Elf even though nationality now says Dwarf.
            int fakeSeasonTwo = checked(seasonId + 1000000);
            await InsertTypeTeamAsync(store, saveId, fakeSeasonTwo, seasonNumber: 2, athleteId, creatureType: "Elf", teamRank: 1, medal: 1, groupRank: 1);
            await SetAthleteNationalityAsync(store, saveId, athleteId, "Dwarf");

            List<AthleteCupHistoryDto> after = await LoadHistoryAsync(store, saveId, athleteId);
            after.Single(e => string.Equals(e.Cup, "Type", StringComparison.Ordinal)).TeamKey.ShouldBe("Elf");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProfileIntegration_IncludesCupHistory_AndKeepsLeagueHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Hist Profile", 1515UL, 1616UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonId = await SeasonIdAsync(store, saveId, 1);
            int athleteId = await FirstAthleteAsync(store, saveId);
            int seasonsBefore = await SeasonCountAsync(store, saveId);

            await InsertIndividualAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, cupRank: 1, medal: 1, color: SportingColor.Black);
            await InsertColorTeamAsync(store, saveId, seasonId, seasonNumber: 1, athleteId, color: SportingColor.Black, teamRank: 3, medal: 3, groupRank: 1);

            GetAthleteProfileHandler handler = new(store);
            GetAthleteProfileResponse profile = await handler.HandleAsync(saveId, athleteId);
            profile.AthleteId.ShouldBe(athleteId);
            profile.Seasons.Count.ShouldBe(seasonsBefore);
            profile.CupHistory.Count.ShouldBe(2);
            profile.CupHistory.Select(e => e.Event).ShouldBe(["Individual", "Team"]);
            profile.CupHistory.ShouldAllBe(e => e.SourceSeasonNumber == 1);
            profile.CupHistory.Single(e => string.Equals(e.Event, "Individual", StringComparison.Ordinal)).Place.ShouldBe(1);
            profile.CupHistory.Single(e => string.Equals(e.Event, "Team", StringComparison.Ordinal)).Place.ShouldBe(3);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<List<AthleteCupHistoryDto>> LoadHistoryAsync(SaveStore store, Guid saveId, int athleteId)
    {
        GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, athleteId).ConfigureAwait(false);
        return profile.CupHistory.ToList();
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

    private static async Task InsertIndividualAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, int cupRank, int medal, SportingColor color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.ColorCupIndividualStandings.Add(new ColorCupIndividualStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            SaveAthleteId = athleteId,
            CupRank = cupRank,
            CupScoreThousandths = 100000 + (cupRank * 1000),
            BaseScoreThousandths = 90000,
            RoundWins = 1,
            RoundPlaceCountsJson = "[]",
            Medal = medal,
            SportingColor = (int)color,
            SelectionRank = 1,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertColorTeamAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, SportingColor color, int teamRank, int medal, int groupRank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.ColorCupTeamGroupStandings.Add(new ColorCupTeamGroupStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            GroupNumber = 1,
            SaveAthleteId = athleteId,
            GroupRank = groupRank,
            GroupScoreThousandths = 50000,
            BaseScoreThousandths = 45000,
            RoundWins = 1,
            RoundPlaceCountsJson = "[]",
            SportingColor = (int)color,
            SelectionRank = 1,
        });
        context.ColorCupTeamStandings.Add(new ColorCupTeamStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            SportingColor = (int)color,
            TeamRank = teamRank,
            TeamScoreThousandths = 200000,
            TeamBaseThousandths = 180000,
            GroupWins = 1,
            RoundWins = 2,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = medal,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertColorLegOnlyAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, SportingColor color, int groupRank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.ColorCupTeamGroupStandings.Add(new ColorCupTeamGroupStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            GroupNumber = 1,
            SaveAthleteId = athleteId,
            GroupRank = groupRank,
            GroupScoreThousandths = 50000,
            BaseScoreThousandths = 45000,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            SportingColor = (int)color,
            SelectionRank = 1,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertColorTeamStandingOnlyAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, SportingColor color, int teamRank, int medal)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.ColorCupTeamStandings.Add(new ColorCupTeamStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            SportingColor = (int)color,
            TeamRank = teamRank,
            TeamScoreThousandths = 200000,
            TeamBaseThousandths = 180000,
            GroupWins = 0,
            RoundWins = 0,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = medal,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertTypeTeamAsync(
        SaveStore store, Guid saveId, int seasonId, int seasonNumber, int athleteId, string creatureType, int teamRank, int medal, int groupRank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            GroupNumber = 2,
            SaveAthleteId = athleteId,
            GroupRank = groupRank,
            GroupScoreThousandths = 60000,
            BaseScoreThousandths = 55000,
            RoundWins = 1,
            RoundPlaceCountsJson = "[]",
            CreatureType = creatureType,
            SelectionRank = 2,
        });
        context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = seasonNumber,
            CreatureType = creatureType,
            TeamRank = teamRank,
            TeamScoreThousandths = 210000,
            TeamBaseThousandths = 190000,
            GroupWins = 1,
            RoundWins = 3,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = medal,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SetAthleteColorAsync(SaveStore store, Guid saveId, int athleteId, SportingColor color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == athleteId).ConfigureAwait(false);
        athlete.SportingColor = (int)color;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SetAthleteNationalityAsync(SaveStore store, Guid saveId, int athleteId, string nationality)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == athleteId).ConfigureAwait(false);
        athlete.TypeCupNationality = nationality;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cup-history-" + Guid.NewGuid().ToString("N"));
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
