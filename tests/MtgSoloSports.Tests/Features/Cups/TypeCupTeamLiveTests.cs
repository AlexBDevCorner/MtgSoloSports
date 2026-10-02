using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.AdvanceTypeCupTeamRound;
using MtgSoloSports.Features.Cups.GetTypeCupTeamLive;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-045: the Type Cup live team projection refreshes after every persisted
/// round. Covers zero/first/mid-group/group-boundary/complete states, exact
/// agreement between incremental advances and the (resume-capable) full run,
/// reload agreement, conflict after completion, and dynamically sized fields
/// with no row cap (including 35 teams).
/// </summary>
public sealed class TypeCupTeamLiveTests
{
    [Fact]
    public async Task Live_BeforeAnyRound_ShowsZeroForEveryTeam()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            GetTypeCupTeamLiveHandler live = new(store);
            TypeCupTeamLiveResponse projection = await live.HandleAsync(saveId, sourceSeasonNumber: 2);

            projection.TeamCount.ShouldBe(2);
            projection.GroupCount.ShouldBe(4);
            projection.GroupRounds.ShouldBe(8);
            projection.CompletedRounds.ShouldBe(0);
            projection.TotalRounds.ShouldBe(32);
            projection.IsComplete.ShouldBeFalse();
            projection.IsProvisional.ShouldBeTrue();
            projection.CurrentGroupNumber.ShouldBe(1);
            projection.CurrentRoundNumber.ShouldBe(1);
            projection.Teams.Count.ShouldBe(2);
            projection.Teams.Select(t => t.TeamScoreThousandths).ShouldBe([0, 0]);
            projection.Teams.Select(t => t.Medal).ShouldBe(["None", "None"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_FirstRound_IncludesGroup1Round1Points()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse projection = await advance.HandleAsync(saveId, sourceSeasonNumber: 2);

            projection.CompletedRounds.ShouldBe(1);
            projection.IsProvisional.ShouldBeTrue();
            projection.CurrentGroupNumber.ShouldBe(1);
            projection.CurrentRoundNumber.ShouldBe(2);
            projection.LastCompletedGroupNumber.ShouldBe(1);
            projection.LastCompletedRoundNumber.ShouldBe(1);
            projection.Teams.Count.ShouldBe(2);
            Dictionary<string, int> expected = await SumPersistedRoundsAsync(store, saveId);
            foreach (TypeCupTeamLiveMember member in projection.Teams)
            {
                member.TeamScoreThousandths.ShouldBe(expected[member.CreatureType]);
                (member.TeamScoreThousandths > 0).ShouldBeTrue();
            }

            await AssertReloadAgreesAsync(store, saveId, projection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_MidGroup_AccumulatesWithoutDoubleCounting()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse projection = await AdvanceRoundsAsync(advance, saveId, 3);

            projection.CompletedRounds.ShouldBe(3);
            projection.CurrentGroupNumber.ShouldBe(1);
            projection.CurrentRoundNumber.ShouldBe(4);
            await AssertTotalsMatchPersistedAsync(store, saveId, projection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_AcrossGroupBoundary_CountsEarlierGroupsOnce()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse afterEight = await AdvanceRoundsAsync(advance, saveId, 8);
            afterEight.CompletedRounds.ShouldBe(8);
            await AssertGroupLegsAsync(store, saveId, groupNumber: 1, expectedLegs: 2);
            await AssertGroupLegsAsync(store, saveId, groupNumber: 2, expectedLegs: 0);

            TypeCupTeamLiveResponse afterNine = await advance.HandleAsync(saveId, sourceSeasonNumber: 2);
            afterNine.CompletedRounds.ShouldBe(9);
            afterNine.CurrentGroupNumber.ShouldBe(2);
            afterNine.CurrentRoundNumber.ShouldBe(2);
            await AssertTotalsMatchPersistedAsync(store, saveId, afterNine);
            await AssertGroupLegsAsync(store, saveId, groupNumber: 2, expectedLegs: 0);
            afterNine.Teams.Select(t => t.TeamScoreThousandths).ShouldNotBe(
                afterEight.Teams.Select(t => t.TeamScoreThousandths).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_AllRounds_MatchesOfficialResults()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse projection = await AdvanceRoundsAsync(advance, saveId, 32);

            projection.CompletedRounds.ShouldBe(32);
            projection.IsComplete.ShouldBeTrue();
            projection.IsProvisional.ShouldBeFalse();
            projection.ChampionCreatureType.ShouldNotBeNullOrWhiteSpace();
            projection.Checksum.ShouldNotBeNullOrWhiteSpace();
            projection.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe([1, 2]);
            projection.Teams.Single(t => t.TeamRank == 1).Medal.ShouldBe("Gold");

            GetTypeCupTeamResultHandler query = new(store);
            GetTypeCupTeamResultResponse official = await query.HandleAsync(saveId);
            official.Checksum.ShouldBe(projection.Checksum);
            official.ChampionCreatureType.ShouldBe(projection.ChampionCreatureType);
            foreach (GetTypeCupTeamMember team in official.Teams)
            {
                TypeCupTeamLiveMember member = projection.Teams.Single(t => string.Equals(t.CreatureType, team.CreatureType, StringComparison.Ordinal));
                member.TeamScoreThousandths.ShouldBe(team.TeamScoreThousandths);
                member.TeamRank.ShouldBe(team.TeamRank);
                member.Medal.ShouldBe(team.Medal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Incremental_MatchesAtomic_ForSameSeed()
    {
        var (atomicStore, atomicRoot) = CreateStore();
        var (liveStore, liveRoot) = CreateStore();
        try
        {
            Guid atomicSave = await SetupTwoTeamFieldAsync(atomicStore, 42421UL, 7771UL);
            Guid liveSave = await SetupTwoTeamFieldAsync(liveStore, 42421UL, 7771UL);
            RunTypeCupTeamHandler run = new(atomicStore);
            RunTypeCupTeamResponse atomic = await run.HandleAsync(atomicSave, sourceSeasonNumber: 2);

            AdvanceTypeCupTeamRoundHandler advance = new(liveStore);
            TypeCupTeamLiveResponse live = await AdvanceRoundsAsync(advance, liveSave, 32);

            live.Checksum.ShouldBe(atomic.Checksum);
            live.ChampionCreatureType.ShouldBe(atomic.ChampionCreatureType);
            live.Teams.Select(t => t.CreatureType).ShouldBe(atomic.Teams.Select(t => t.CreatureType).ToList());
            live.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(atomic.Teams.Select(t => t.TeamScoreThousandths).ToList());
            ulong atomicAfter = atomic.RngAfterState;
            ulong liveAfter = await LoadRngStateAsync(liveStore, liveSave);
            liveAfter.ShouldBe(atomicAfter);
        }
        finally
        {
            Directory.Delete(atomicRoot, recursive: true);
            Directory.Delete(liveRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Run_AfterPartialAdvances_ResumesToIdenticalResult()
    {
        var (atomicStore, atomicRoot) = CreateStore();
        var (resumeStore, resumeRoot) = CreateStore();
        try
        {
            Guid atomicSave = await SetupTwoTeamFieldAsync(atomicStore, 42421UL, 7771UL);
            Guid resumeSave = await SetupTwoTeamFieldAsync(resumeStore, 42421UL, 7771UL);
            RunTypeCupTeamHandler atomicRun = new(atomicStore);
            RunTypeCupTeamResponse atomic = await atomicRun.HandleAsync(atomicSave, sourceSeasonNumber: 2);

            AdvanceTypeCupTeamRoundHandler advance = new(resumeStore);
            _ = await AdvanceRoundsAsync(advance, resumeSave, 5);
            RunTypeCupTeamHandler resumeRun = new(resumeStore);
            RunTypeCupTeamResponse resumed = await resumeRun.HandleAsync(resumeSave, sourceSeasonNumber: 2);

            resumed.Checksum.ShouldBe(atomic.Checksum);
            resumed.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(atomic.Teams.Select(t => t.TeamScoreThousandths).ToList());
        }
        finally
        {
            Directory.Delete(atomicRoot, recursive: true);
            Directory.Delete(resumeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_AfterComplete_ConflictsAndKeepsTotals()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTwoTeamFieldAsync(store, 1213UL, 1415UL);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse complete = await AdvanceRoundsAsync(advance, saveId, 32);

            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => advance.HandleAsync(saveId, sourceSeasonNumber: 2));

            GetTypeCupTeamLiveHandler live = new(store);
            TypeCupTeamLiveResponse reloaded = await live.HandleAsync(saveId, sourceSeasonNumber: 2);
            reloaded.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(
                complete.Teams.Select(t => t.TeamScoreThousandths).ToList());
            reloaded.CompletedRounds.ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_ThirtyFiveTeams_ListsEveryTeamWithoutCap()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupMultiTeamFieldAsync(store, 35, 9000UL + 35, 9100UL + 35);
            AdvanceTypeCupTeamRoundHandler advance = new(store);
            TypeCupTeamLiveResponse first = await advance.HandleAsync(saveId, sourceSeasonNumber: 2);
            first.TeamCount.ShouldBe(35);
            first.Teams.Count.ShouldBe(35);
            await AssertTotalsMatchPersistedAsync(store, saveId, first);

            TypeCupTeamLiveResponse complete = await AdvanceRoundsAsync(advance, saveId, 31);
            complete.CompletedRounds.ShouldBe(32);
            complete.IsComplete.ShouldBeTrue();
            complete.Teams.Count.ShouldBe(35);
            complete.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 35).ToList());
            await AssertTotalsMatchPersistedAsync(store, saveId, complete);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<TypeCupTeamLiveResponse> AdvanceRoundsAsync(
        AdvanceTypeCupTeamRoundHandler advance, Guid saveId, int count)
    {
        TypeCupTeamLiveResponse last = await advance.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        for (int i = 1; i < count; i++)
        {
            last = await advance.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        }

        return last;
    }

    private static async Task AssertTotalsMatchPersistedAsync(
        SaveStore store, Guid saveId, TypeCupTeamLiveResponse projection)
    {
        Dictionary<string, int> expected = await SumPersistedRoundsAsync(store, saveId).ConfigureAwait(false);
        projection.Teams.Count.ShouldBe(expected.Count);
        int total = 0;
        foreach (TypeCupTeamLiveMember member in projection.Teams)
        {
            member.TeamScoreThousandths.ShouldBe(expected[member.CreatureType]);
            total = checked(total + member.TeamScoreThousandths);
        }

        total.ShouldBe(expected.Values.Sum());
    }

    private static async Task<Dictionary<string, int>> SumPersistedRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        Dictionary<int, string> teams = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.CreatureType).ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        Dictionary<string, int> sums = teams.Values.Distinct(StringComparer.Ordinal).ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            foreach (var placement in document.Placements)
            {
                sums[teams[placement.AthleteId]] = checked(sums[teams[placement.AthleteId]] + placement.FinalThousandths);
            }
        }

        return sums;
    }

    private static async Task AssertGroupLegsAsync(SaveStore store, Guid saveId, int groupNumber, int expectedLegs)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        int legs = await context.TypeCupTeamGroupStandings.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.GroupNumber == groupNumber).ConfigureAwait(false);
        legs.ShouldBe(expectedLegs);
    }

    private static async Task AssertReloadAgreesAsync(SaveStore store, Guid saveId, TypeCupTeamLiveResponse projection)
    {
        GetTypeCupTeamLiveHandler live = new(store);
        TypeCupTeamLiveResponse reloaded = await live.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        reloaded.CompletedRounds.ShouldBe(projection.CompletedRounds);
        reloaded.CurrentGroupNumber.ShouldBe(projection.CurrentGroupNumber);
        reloaded.CurrentRoundNumber.ShouldBe(projection.CurrentRoundNumber);
        reloaded.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(
            projection.Teams.Select(t => t.TeamScoreThousandths).ToList());
        reloaded.Teams.Select(t => t.TeamRank).ShouldBe(
            projection.Teams.Select(t => t.TeamRank).ToList());
    }

    private static async Task<ulong> LoadRngStateAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return unchecked((ulong)rng.State);
    }

    private static async Task<Guid> SetupTwoTeamFieldAsync(SaveStore store, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync("Type Team Live", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, 8).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return saveId;
    }

    private static async Task<Guid> SetupMultiTeamFieldAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync($"Type Team Live {teamCount}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount * 4).ConfigureAwait(false);
        for (int team = 0; team < teamCount; team++)
        {
            List<int> slice = activeIds.Skip(team * 4).Take(4).ToList();
            await SetTypesAsync(store, saveId, slice, [$"MSS045-Type-{team:D3}"]).ConfigureAwait(false);
        }

        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        allocation.TeamCount.ShouldBe(teamCount);
        return saveId;
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
        string json = System.Text.Json.JsonSerializer.Serialize(types);
        foreach (SaveAthleteEntity athlete in athletes)
        {
            athlete.CreatureTypesJson = json;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task CreateEvenSeasonAsync(SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = new() { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
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

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-live-" + Guid.NewGuid().ToString("N"));
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
