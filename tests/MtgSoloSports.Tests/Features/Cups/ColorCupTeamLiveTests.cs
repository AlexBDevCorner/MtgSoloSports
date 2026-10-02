using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.AdvanceColorCupTeamRound;
using MtgSoloSports.Features.Cups.GetColorCupTeamLive;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-045: the Color Cup team event refreshes its live team projection after
/// every persisted round, using the same round-by-round behavior as the Type
/// Cup team event. Covers zero/first/boundary/complete states, exact agreement
/// with official results and the full run, and conflict after completion. The
/// separate Color Cup individual event is untouched.
/// </summary>
public sealed class ColorCupTeamLiveTests
{
    [Fact]
    public async Task Live_BeforeAnyRound_ShowsZeroForEveryColorTeam()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupSelectedFieldAsync(store, 9091UL, 1011UL);
            GetColorCupTeamLiveHandler live = new(store);
            ColorCupTeamLiveResponse projection = await live.HandleAsync(saveId, sourceSeasonNumber: 1);

            projection.TeamCount.ShouldBe(8);
            projection.GroupCount.ShouldBe(4);
            projection.GroupRounds.ShouldBe(8);
            projection.CompletedRounds.ShouldBe(0);
            projection.TotalRounds.ShouldBe(32);
            projection.IsComplete.ShouldBeFalse();
            projection.IsProvisional.ShouldBeTrue();
            projection.CurrentGroupNumber.ShouldBe(1);
            projection.CurrentRoundNumber.ShouldBe(1);
            projection.Teams.Count.ShouldBe(8);
            projection.Teams.Select(t => t.TeamScoreThousandths).ShouldBe([0, 0, 0, 0, 0, 0, 0, 0]);
            projection.Teams.Select(t => t.Medal).ShouldBe(["None", "None", "None", "None", "None", "None", "None", "None"]);
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
            Guid saveId = await SetupSelectedFieldAsync(store, 9091UL, 1011UL);
            AdvanceColorCupTeamRoundHandler advance = new(store);
            ColorCupTeamLiveResponse projection = await advance.HandleAsync(saveId, sourceSeasonNumber: 1);

            projection.CompletedRounds.ShouldBe(1);
            projection.IsProvisional.ShouldBeTrue();
            projection.CurrentGroupNumber.ShouldBe(1);
            projection.CurrentRoundNumber.ShouldBe(2);
            projection.LastCompletedGroupNumber.ShouldBe(1);
            projection.LastCompletedRoundNumber.ShouldBe(1);
            projection.Teams.Count.ShouldBe(8);
            await AssertTotalsMatchPersistedAsync(store, saveId, projection);
            await AssertReloadAgreesAsync(store, saveId, projection);
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
            Guid saveId = await SetupSelectedFieldAsync(store, 9091UL, 1011UL);
            AdvanceColorCupTeamRoundHandler advance = new(store);
            ColorCupTeamLiveResponse afterEight = await AdvanceRoundsAsync(advance, saveId, 8);
            afterEight.CompletedRounds.ShouldBe(8);
            await AssertGroupLegsAsync(store, saveId, groupNumber: 1, expectedLegs: 8);
            await AssertGroupLegsAsync(store, saveId, groupNumber: 2, expectedLegs: 0);

            ColorCupTeamLiveResponse afterNine = await advance.HandleAsync(saveId, sourceSeasonNumber: 1);
            afterNine.CompletedRounds.ShouldBe(9);
            afterNine.CurrentGroupNumber.ShouldBe(2);
            afterNine.CurrentRoundNumber.ShouldBe(2);
            await AssertTotalsMatchPersistedAsync(store, saveId, afterNine);
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
            Guid saveId = await SetupSelectedFieldAsync(store, 9091UL, 1011UL);
            AdvanceColorCupTeamRoundHandler advance = new(store);
            ColorCupTeamLiveResponse projection = await AdvanceRoundsAsync(advance, saveId, 32);

            projection.CompletedRounds.ShouldBe(32);
            projection.IsComplete.ShouldBeTrue();
            projection.IsProvisional.ShouldBeFalse();
            projection.ChampionTeamName.ShouldNotBeNullOrWhiteSpace();
            projection.Checksum.ShouldNotBeNullOrWhiteSpace();
            projection.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 8).ToList());
            projection.Teams.Single(t => t.TeamRank == 1).Medal.ShouldBe("Gold");

            GetColorCupTeamResultHandler query = new(store);
            GetColorCupTeamResultResponse official = await query.HandleAsync(saveId);
            official.Checksum.ShouldBe(projection.Checksum);
            official.ChampionTeamName.ShouldBe(projection.ChampionTeamName);
            foreach (GetColorCupTeamMember team in official.Teams)
            {
                ColorCupTeamLiveMember member = projection.Teams.Single(t => t.SportingColor == team.SportingColor);
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
            Guid atomicSave = await SetupSelectedFieldAsync(atomicStore, 42421UL, 7771UL);
            Guid liveSave = await SetupSelectedFieldAsync(liveStore, 42421UL, 7771UL);
            RunColorCupTeamHandler run = new(atomicStore);
            RunColorCupTeamResponse atomic = await run.HandleAsync(atomicSave);

            AdvanceColorCupTeamRoundHandler advance = new(liveStore);
            ColorCupTeamLiveResponse live = await AdvanceRoundsAsync(advance, liveSave, 32);

            // The atomic run and the live projection fingerprint team standings
            // with different (pre-existing) checksum formats, so equivalence is
            // asserted on standings content plus the official query checksum.
            live.ChampionTeamName.ShouldBe(atomic.ChampionTeamName);
            live.Teams.Select(t => t.SportingColor).ShouldBe(atomic.Teams.Select(t => t.SportingColor).ToList());
            live.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(atomic.Teams.Select(t => t.TeamScoreThousandths).ToList());
            live.Teams.Select(t => t.TeamRank).ShouldBe(atomic.Teams.Select(t => t.TeamRank).ToList());
            GetColorCupTeamResultHandler query = new(liveStore);
            GetColorCupTeamResultResponse official = await query.HandleAsync(liveSave);
            official.Checksum.ShouldBe(live.Checksum);
            ulong liveAfter = await LoadRngStateAsync(liveStore, liveSave);
            liveAfter.ShouldBe(atomic.RngAfterState);
        }
        finally
        {
            Directory.Delete(atomicRoot, recursive: true);
            Directory.Delete(liveRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Advance_AfterComplete_ConflictsAndKeepsTotals()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupSelectedFieldAsync(store, 9091UL, 1011UL);
            AdvanceColorCupTeamRoundHandler advance = new(store);
            ColorCupTeamLiveResponse complete = await AdvanceRoundsAsync(advance, saveId, 32);

            await Should.ThrowAsync<RunColorCupTeamConflictException>(
                () => advance.HandleAsync(saveId, sourceSeasonNumber: 1));

            GetColorCupTeamLiveHandler live = new(store);
            ColorCupTeamLiveResponse reloaded = await live.HandleAsync(saveId, sourceSeasonNumber: 1);
            reloaded.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(
                complete.Teams.Select(t => t.TeamScoreThousandths).ToList());
            reloaded.CompletedRounds.ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ColorCupTeamLiveResponse> AdvanceRoundsAsync(
        AdvanceColorCupTeamRoundHandler advance, Guid saveId, int count)
    {
        ColorCupTeamLiveResponse last = await advance.HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        for (int i = 1; i < count; i++)
        {
            last = await advance.HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        }

        return last;
    }

    private static async Task AssertTotalsMatchPersistedAsync(
        SaveStore store, Guid saveId, ColorCupTeamLiveResponse projection)
    {
        Dictionary<int, int> expected = await SumPersistedRoundsAsync(store, saveId).ConfigureAwait(false);
        projection.Teams.Count.ShouldBe(expected.Count);
        foreach (ColorCupTeamLiveMember member in projection.Teams)
        {
            member.TeamScoreThousandths.ShouldBe(expected[member.SportingColor]);
        }
    }

    private static async Task<Dictionary<int, int>> SumPersistedRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        Dictionary<int, int> teams = await context.ColorCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.SportingColor).ConfigureAwait(false);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        Dictionary<int, int> sums = teams.Values.Distinct().ToDictionary(t => t, _ => 0);
        foreach (ColorCupTeamRoundEntity round in rounds)
        {
            ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
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
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        int legs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.GroupNumber == groupNumber).ConfigureAwait(false);
        legs.ShouldBe(expectedLegs);
    }

    private static async Task AssertReloadAgreesAsync(SaveStore store, Guid saveId, ColorCupTeamLiveResponse projection)
    {
        GetColorCupTeamLiveHandler live = new(store);
        ColorCupTeamLiveResponse reloaded = await live.HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        reloaded.CompletedRounds.ShouldBe(projection.CompletedRounds);
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

    private static async Task<Guid> SetupSelectedFieldAsync(SaveStore store, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync("Color Team Live", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        CompleteStageForAllLeaguesHandler bulk = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            _ = await bulk.HandleAsync(saveId).ConfigureAwait(false);
        }

        SelectColorCupTeamsHandler select = new(store);
        SelectColorCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        allocation.TotalSelected.ShouldBe(32);
        return saveId;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-colorcup-live-" + Guid.NewGuid().ToString("N"));
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
