using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-061: persisted Type Cup qualification draw. Direct Finals persist no
/// draw; over-32 fields persist a deterministic balanced random draw with exact
/// 32-finalist quotas before any competition. Historical single-field Cups stay
/// readable.
/// </summary>
public sealed class TypeCupTournamentDrawTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(31)]
    [InlineData(32)]
    public async Task DirectFinal_PersistsNoDraw(int teamCount)
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, teamCount, 10000UL + (ulong)teamCount, 10100UL + (ulong)teamCount);
            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(teamCount);

            DrawTypeCupQualificationGroupsHandler draw = new(store);
            DrawTypeCupQualificationGroupsResponse response = await draw.HandleAsync(saveId, sourceSeasonNumber: 2);
            response.IsDirectFinal.ShouldBeTrue();
            response.TeamCount.ShouldBe(teamCount);
            response.QualificationGroupCount.ShouldBe(0);
            response.Groups.Count.ShouldBe(0);

            DrawTypeCupQualificationGroupsResponse reread = await draw.GetAsync(saveId, sourceSeasonNumber: 2);
            reread.IsDirectFinal.ShouldBeTrue();
            reread.TeamCount.ShouldBe(teamCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(33, new[] { 17, 16 }, new[] { 16, 16 })]
    [InlineData(35, new[] { 18, 17 }, new[] { 16, 16 })]
    [InlineData(64, new[] { 32, 32 }, new[] { 16, 16 })]
    [InlineData(65, new[] { 22, 22, 21 }, new[] { 11, 11, 10 })]
    [InlineData(96, new[] { 32, 32, 32 }, new[] { 11, 11, 10 })]
    [InlineData(97, new[] { 25, 24, 24, 24 }, new[] { 8, 8, 8, 8 })]
    public async Task QualificationDraw_PersistsBalancedGroups_AndExactQuotas(
        int teamCount, int[] expectedSizes, int[] expectedQuotas)
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, teamCount, 20000UL + (ulong)teamCount, 20100UL + (ulong)teamCount);
            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(teamCount);

            DrawTypeCupQualificationGroupsHandler draw = new(store);
            DrawTypeCupQualificationGroupsResponse response = await draw.HandleAsync(saveId, sourceSeasonNumber: 2);
            response.IsDirectFinal.ShouldBeFalse();
            response.TeamCount.ShouldBe(teamCount);
            response.QualificationGroupCount.ShouldBe(expectedSizes.Length);
            response.GroupSizes.ShouldBe(expectedSizes);
            response.FinalPlacesPerGroup.ShouldBe(expectedQuotas);
            response.FinalPlacesPerGroup.Sum().ShouldBe(32);
            response.Groups.Count.ShouldBe(expectedSizes.Length);
            response.DrawChecksum.Length.ShouldBe(64);

            List<string> drawn = response.Groups.SelectMany(g => g.CreatureTypes).ToList();
            drawn.Count.ShouldBe(teamCount);
            drawn.Distinct(StringComparer.Ordinal).Count().ShouldBe(teamCount);

            for (int i = 0; i < expectedSizes.Length; i++)
            {
                response.Groups[i].QualificationGroup.ShouldBe(i + 1);
                response.Groups[i].GroupSize.ShouldBe(expectedSizes[i]);
                response.Groups[i].CreatureTypes.Count.ShouldBe(expectedSizes[i]);
                response.Groups[i].GroupSize.ShouldBeLessThanOrEqualTo(32);
                response.Groups[i].FinalPlaces.ShouldBe(expectedQuotas[i]);
            }

            DrawTypeCupQualificationGroupsResponse reread = await draw.GetAsync(saveId, sourceSeasonNumber: 2);
            reread.DrawChecksum.ShouldBe(response.DrawChecksum);
            reread.Groups.SelectMany(g => g.CreatureTypes).OrderBy(t => t, StringComparer.Ordinal)
                .ShouldBe(drawn.OrderBy(t => t, StringComparer.Ordinal).ToList());

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupSelectionEntity> selection = await context.TypeCupSelections.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();
            selection.Count.ShouldBe(teamCount * 4);
            HashSet<string> selectedTypes = selection.Select(e => e.CreatureType).ToHashSet(StringComparer.Ordinal);
            selectedTypes.SetEquals(drawn).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Draw_IsDeterministic_UnderIdenticalSeed_AndDiffersOtherwise()
    {
        (DrawTypeCupQualificationGroupsResponse first, string rootFirst) = await RunDrawAsync(35, 42421UL, 7771UL);
        (DrawTypeCupQualificationGroupsResponse second, string rootSecond) = await RunDrawAsync(35, 42421UL, 7771UL);
        (DrawTypeCupQualificationGroupsResponse third, string rootThird) = await RunDrawAsync(35, 99991UL, 12341UL);
        try
        {
            first.DrawChecksum.ShouldBe(second.DrawChecksum);
            first.Groups.SelectMany(g => g.CreatureTypes)
                .ShouldBe(second.Groups.SelectMany(g => g.CreatureTypes).ToList());
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);

            first.DrawChecksum.Equals(third.DrawChecksum, StringComparison.Ordinal).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(rootFirst, recursive: true);
            Directory.Delete(rootSecond, recursive: true);
            Directory.Delete(rootThird, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateDraw_IsRejected()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 30000UL, 30100UL);
            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            DrawTypeCupQualificationGroupsHandler draw = new(store);
            await draw.HandleAsync(saveId, sourceSeasonNumber: 2);
            await Should.ThrowAsync<DrawTypeCupQualificationGroupsConflictException>(
                () => draw.HandleAsync(saveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoricalSingleFieldCup_RemainsReadable()
    {
        // MSS-062: new 35-team fields run qualification plus a 32-team Final
        // (no manufactured single-field history). Old single-field saves
        // (phase 0, even 33-35 teams) remain readable via the legacy path;
        // here a new tournament stays readable through both the Final result
        // and the tournament summary.
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 40000UL, 40100UL);
            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            RunTypeCupTeamHandler run = new(store);
            RunTypeCupTeamResponse response = await run.HandleAsync(saveId, sourceSeasonNumber: 2);
            response.TeamCount.ShouldBe(32);

            GetTypeCupTeamResultHandler query = new(store);
            GetTypeCupTeamResultResponse summary = await query.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.TeamCount.ShouldBe(32);
            summary.Checksum.ShouldBe(response.Checksum);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();
            // Two qualification groups (32 rounds each) plus the Final (32).
            rounds.Count.ShouldBe(96);
            rounds.Any(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField).ShouldBeFalse();
            rounds.Count(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification).ShouldBe(64);
            rounds.Count(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final).ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OldRulesSnapshot_WithoutTournamentFields_DecodesAsLegacy()
    {
        string legacyJson = """{"version":1,"algorithm":"Pcg32V1","algorithmVersion":1,"sportingColorCount":8,"athletesPerSportingColor":256,"totalAthletesInSave":2048,"regularLeagueCount":8,"leagueSize":32,"superleagueSize":32,"stagesPerSeason":32,"roundsPerStage":16,"qualifierSize":32,"qualifierRounds":16,"qualifierWinners":8,"superleagueSafeCount":16,"superleagueRelegatedCount":8,"superleagueQualifierIncumbentCount":8,"feederAutoPromotedCount":8,"feederQualifierCount":24,"inauguralQualifiedPerLeague":4,"superleagueBonusMultiplier":2,"colorCupColorCount":8,"colorCupTeamSize":4,"colorCupIndividualRounds":16,"colorCupTeamGroupRounds":8,"typeCupMinTeamSize":4,"typeCupGroupRounds":8,"recentFormStageCount":10,"cupBonusWeightPermille":350,"cupPerformanceWeightPermille":300,"cupFormWeightPermille":250,"cupPrestigeWeightPermille":100,"cupPrestigeFeederTitlePoints":100,"cupPrestigeSuperleagueTitlePoints":300,"cupPrestigeSuperleagueAppearancePoints":20,"cupPrestigeStageWinPoints":10,"cupPrestigeStageSecondPoints":5,"cupPrestigeStageThirdPoints":2,"cupPrestigeOtherMajorHonourPoints":150,"scoringTable":[77,67,58,50,43,37,32,28,25,23,22,21,20,19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1],"roundBonusThousandths":[100,90,80,70,60,50,40,30,20,10],"stageBonusThousandths":[200,180,160,140,120,100,80,60,40,20],"bonusAgeWeightsThousandths":[1000,800,600,400,200,0],"recentFormWeights":[1,2,3,4,5,6,7,8,9,10]}""";
        RulesV1 decoded = MtgSoloSports.Persistence.Saves.RulesSnapshotCodec.Decode(legacyJson);
        decoded.TypeCupTournamentFormatVersion.ShouldBe(RulesV1.LegacyTypeCupTournamentFormatVersion);
        decoded.TypeCupMaxDirectFinalTeams.ShouldBe(32);
        decoded.TypeCupFinalTeamCount.ShouldBe(32);
        Should.NotThrow(() => decoded.Validate());

        RulesV1 current = RulesV1.CreateDefault();
        current.TypeCupTournamentFormatVersion.ShouldBe(RulesV1.DefaultTypeCupTournamentFormatVersion);
    }

    private static async Task<(DrawTypeCupQualificationGroupsResponse Response, string Root)> RunDrawAsync(
        int teamCount, ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        Guid saveId = await SetupFieldAsync(store, teamCount, seed, stream).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        DrawTypeCupQualificationGroupsHandler draw = new(store);
        DrawTypeCupQualificationGroupsResponse response = await draw.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return (response, root);
    }

    private static async Task<Guid> SetupFieldAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Type Draw {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount * 4).ConfigureAwait(false);
        await SetDistinctTypesAsync(store, saveId, activeIds, teamCount).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        return saveId;
    }

    private static async Task SetDistinctTypesAsync(SaveStore store, Guid saveId, List<int> activeIds, int teamCount)
    {
        activeIds.Count.ShouldBe(teamCount * 4);
        for (int team = 0; team < teamCount; team++)
        {
            List<int> slice = activeIds.Skip(team * 4).Take(4).ToList();
            string typeName = $"MSS061-Type-{team:D3}";
            await SetTypesAsync(store, saveId, slice, [typeName]).ConfigureAwait(false);
        }
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

    private static async Task CreateEvenSeasonAsync(SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity? existing = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        SeasonEntity season;
        if (existing is not null)
        {
            existing.IsComplete = true;
            await context.SaveChangesAsync().ConfigureAwait(false);
            season = existing;
        }
        else
        {
            season = new SeasonEntity { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
            context.Seasons.Add(season);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        if (league is null)
        {
            league = new LeagueEntity
            {
                SeasonId = season.Id,
                SportingColor = 0,
                Kind = (int)LeagueKind.Feeder,
                Name = "White League",
            };
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
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-draw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (OpenStore(root).Store, root);
    }

    private static (SaveStore Store, string Unused) OpenStore(string root)
    {
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
