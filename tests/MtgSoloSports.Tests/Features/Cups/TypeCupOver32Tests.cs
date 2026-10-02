using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-044: the Type Cup team event runs with every valid creature-type team,
/// including fields larger than the ordinary 32-athlete league size. Covers
/// 32, 33, 35 and a larger field end to end (selection, validation, full
/// 4x8 simulation, persistence, reopen, query and replay) plus the explicit
/// deterministic scoring rule beyond position 32.
/// </summary>
public sealed class TypeCupOver32Tests
{
    public static TheoryData<int> FieldSizes()
    {
        var data = new TheoryData<int>();
        data.Add(32);
        data.Add(33);
        data.Add(35);
        data.Add(40);
        return data;
    }

    [Fact]
    public void TypeCupScoring_MatchesLeagueTable_ForPositions1To32()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        for (int position = 1; position <= 32; position++)
        {
            ScoringCalculator.TypeCupBasePointsForPosition(position, rules).Thousandths
                .ShouldBe(ScoringCalculator.BaseRoundPointsForPosition(position, rules).Thousandths);
        }
    }

    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(40)]
    [InlineData(64)]
    [InlineData(100)]
    public void TypeCupScoring_Beyond32_ScoresTableMinimum(int position)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        ScoringCalculator.TypeCupBasePointsForPosition(position, rules).Thousandths.ShouldBe(1000);
        ScoringCalculator.TypeCupFinalPointsForPosition(position, Bonus.FromThousandths(10), rules)
            .Thousandths.ShouldBe(1010);
        ScoringCalculator.TypeCupFinalPointsForPosition(position, Bonus.Zero, rules)
            .Thousandths.ShouldBe(1000);
    }

    [Fact]
    public void TypeCupScoring_PositionZero_IsRejected()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.TypeCupBasePointsForPosition(0, rules));
    }

    [Theory]
    [MemberData(nameof(FieldSizes))]
    public void TeamEvent_SimulateGroupRound_SupportsNTeams(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RoundAthleteInput> roster = new(teamCount);
        for (int i = 0; i < teamCount; i++)
        {
            roster.Add(new RoundAthleteInput(i + 1, $"Athlete {i:D4}", Bonus.Zero, Points.Zero));
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        Pcg32V1 rng = new(1234UL, 5678UL);
        RoundSimulationResult result = TeamEvent.SimulateGroupRound(roster, rng, rules, teamCount);

        result.Placements.Count.ShouldBe(teamCount);
        result.Placements.Select(p => p.Position).OrderBy(p => p).ShouldBe(Enumerable.Range(1, teamCount).ToList());
        foreach (RoundPlacement placement in result.Placements)
        {
            int expectedBase = placement.Position <= 32
                ? rules.ScoringTable[placement.Position - 1] * RulesV1.FixedScale
                : 1000;
            placement.BasePoints.Thousandths.ShouldBe(expectedBase);
        }
    }

    [Theory]
    [MemberData(nameof(FieldSizes))]
    public async Task Run_FieldSize_CompletesPersistsReopensAndReplays(int teamCount)
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, teamCount);
            await AssertSelectionAsync(store, saveId, teamCount);
            RunTypeCupTeamResponse response = await RunFieldAsync(store, saveId);
            AssertResponseStructure(response, teamCount);
            await AssertPersistedAsync(store, saveId, response, teamCount);
            await AssertQueryAndReopenAsync(store, root, saveId, response, teamCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ThirtyFiveTeams_DeterministicAcrossSaves()
    {
        (RunTypeCupTeamResponse first, string rootFirst) = await RunTeamForFieldAsync(35, 42421UL, 7771UL);
        (RunTypeCupTeamResponse second, string rootSecond) = await RunTeamForFieldAsync(35, 42421UL, 7771UL);
        try
        {
            first.TeamCount.ShouldBe(35);
            second.TeamCount.ShouldBe(35);
            first.Checksum.ShouldBe(second.Checksum);
            first.Teams.Select(t => t.CreatureType).ShouldBe(second.Teams.Select(t => t.CreatureType).ToList());
            first.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(second.Teams.Select(t => t.TeamScoreThousandths).ToList());
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
        }
        finally
        {
            Directory.Delete(rootFirst, recursive: true);
            Directory.Delete(rootSecond, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ThirtyTwoTeams_ScoringMatchesLeagueTable()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 32);
            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(32);

            RunTypeCupTeamHandler handler = new(store);
            RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            response.TeamCount.ShouldBe(32);
            await AssertBasesMatchTableAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> SetupFieldAsync(SaveStore store, int teamCount)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Type Over32 {teamCount}", 9000UL + (ulong)teamCount, 9100UL + (ulong)teamCount, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount * 4).ConfigureAwait(false);
        await SetDistinctTypesAsync(store, saveId, activeIds, teamCount).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        return saveId;
    }

    private static async Task AssertSelectionAsync(SaveStore store, Guid saveId, int teamCount)
    {
        SelectTypeCupTeamsHandler select = new(store);
        SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        allocation.TeamCount.ShouldBe(teamCount);
        allocation.TotalSelected.ShouldBe(teamCount * 4);

        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<TypeCupSelectionEntity> selection = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        RulesV1 rules = RulesV1.CreateDefault();
        int validated = TypeCupTeamInvariants.ValidateField(selection, rules);
        validated.ShouldBe(teamCount);
        Dictionary<int, List<TypeCupSelectionEntity>> groups = PartitionByRank(selection);
        TypeCupTeamInvariants.ValidateGroups(groups, rules, teamCount);
        foreach (int groupNumber in Enumerable.Range(1, 4))
        {
            groups[groupNumber].Count.ShouldBe(teamCount);
        }
    }

    private static Dictionary<int, List<TypeCupSelectionEntity>> PartitionByRank(List<TypeCupSelectionEntity> selection)
    {
        Dictionary<int, List<TypeCupSelectionEntity>> groups = new()
        {
            [1] = selection.Where(e => e.SelectionRank == 1).ToList(),
            [2] = selection.Where(e => e.SelectionRank == 2).ToList(),
            [3] = selection.Where(e => e.SelectionRank == 3).ToList(),
            [4] = selection.Where(e => e.SelectionRank == 4).ToList(),
        };
        return groups;
    }

    private static async Task<RunTypeCupTeamResponse> RunFieldAsync(SaveStore store, Guid saveId)
    {
        RunTypeCupTeamHandler handler = new(store);
        return await handler.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static void AssertResponseStructure(RunTypeCupTeamResponse response, int teamCount)
    {
        response.TeamCount.ShouldBe(teamCount);
        response.GroupCount.ShouldBe(4);
        response.GroupRounds.ShouldBe(8);
        response.Teams.Count.ShouldBe(teamCount);
        response.Legs.Count.ShouldBe(teamCount * 4);
        response.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, teamCount).ToList());
        AssertLegGroups(response, teamCount);
    }

    private static void AssertLegGroups(RunTypeCupTeamResponse response, int teamCount)
    {
        foreach (int groupNumber in Enumerable.Range(1, 4))
        {
            var legs = response.Legs.Where(l => l.GroupNumber == groupNumber).ToList();
            legs.Count.ShouldBe(teamCount);
            legs.Select(l => l.GroupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, teamCount).ToList());
            legs.All(l => l.SelectionRank == groupNumber).ShouldBeTrue();
            legs.Select(l => l.CreatureType).Distinct(StringComparer.Ordinal).Count().ShouldBe(teamCount);
        }
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response, int teamCount)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        await AssertRoundsAsync(context, source, teamCount).ConfigureAwait(false);
        await AssertLegsAndTeamsAsync(context, source, teamCount).ConfigureAwait(false);
        await AssertHonoursAsync(context, source).ConfigureAwait(false);
    }

    private static async Task AssertRoundsAsync(SaveDbContext context, SeasonEntity source, int teamCount)
    {
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        RulesV1 rules = RulesV1.CreateDefault();
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Placements.Count.ShouldBe(teamCount);
            document.Placements.Select(p => p.Position).OrderBy(p => p)
                .ShouldBe(Enumerable.Range(1, teamCount).ToList());
            foreach (var placement in document.Placements)
            {
                int expectedBase = placement.Position <= 32
                    ? rules.ScoringTable[placement.Position - 1] * RulesV1.FixedScale
                    : 1000;
                placement.BaseThousandths.ShouldBe(expectedBase);
            }

            document.Checksum.ShouldBe(round.PayloadChecksum);
        }
    }

    private static async Task AssertLegsAndTeamsAsync(SaveDbContext context, SeasonEntity source, int teamCount)
    {
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        legs.Count.ShouldBe(teamCount * 4);
        foreach (int groupNumber in Enumerable.Range(1, 4))
        {
            legs.Where(l => l.GroupNumber == groupNumber).Select(l => l.GroupRank).OrderBy(r => r)
                .ShouldBe(Enumerable.Range(1, teamCount).ToList());
        }

        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.TeamRank).ToListAsync().ConfigureAwait(false);
        teams.Count.ShouldBe(teamCount);
        teams.Select(t => t.TeamRank).ShouldBe(Enumerable.Range(1, teamCount).ToList());
        if (teamCount >= 3)
        {
            teams.Single(t => t.TeamRank == 1).Medal.ShouldBe((int)TypeCupMedal.Gold);
            teams.Single(t => t.TeamRank == 2).Medal.ShouldBe((int)TypeCupMedal.Silver);
            teams.Single(t => t.TeamRank == 3).Medal.ShouldBe((int)TypeCupMedal.Bronze);
        }
    }

    private static async Task AssertHonoursAsync(SaveDbContext context, SeasonEntity source)
    {
        List<HonourEntity> honours = await context.Honours.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)HonourKind.TypeCupTeamChampion).ToListAsync().ConfigureAwait(false);
        honours.Count.ShouldBe(4);
    }

    private static async Task AssertQueryAndReopenAsync(SaveStore store, string root, Guid saveId, RunTypeCupTeamResponse response, int teamCount)
    {
        GetTypeCupTeamResultHandler query = new(store);
        GetTypeCupTeamResultResponse summary = await query.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        summary.TeamCount.ShouldBe(teamCount);
        summary.GroupCount.ShouldBe(4);
        summary.GroupRounds.ShouldBe(8);
        summary.Checksum.ShouldBe(response.Checksum);
        summary.Teams.Count.ShouldBe(teamCount);
        summary.Legs.Count.ShouldBe(teamCount * 4);

        (SaveStore reopened, _) = OpenStore(root);
        GetTypeCupTeamResultHandler reopenedQuery = new(reopened);
        GetTypeCupTeamResultResponse reopenedSummary = await reopenedQuery.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        reopenedSummary.TeamCount.ShouldBe(teamCount);
        reopenedSummary.Checksum.ShouldBe(response.Checksum);
        reopenedSummary.Teams.Select(t => t.CreatureType)
            .ShouldBe(summary.Teams.Select(t => t.CreatureType).ToList());
    }

    private static async Task AssertBasesMatchTableAsync(SaveStore store, Guid saveId)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            foreach (var placement in document.Placements)
            {
                int expected = rules.ScoringTable[placement.Position - 1] * RulesV1.FixedScale;
                placement.BaseThousandths.ShouldBe(expected);
            }
        }
    }

    private static async Task<(RunTypeCupTeamResponse Response, string Root)> RunTeamForFieldAsync(int teamCount, ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync($"Type Det {teamCount}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount * 4).ConfigureAwait(false);
        await SetDistinctTypesAsync(store, saveId, activeIds, teamCount).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        RunTypeCupTeamHandler handler = new(store);
        RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return (response, root);
    }

    private static async Task SetDistinctTypesAsync(SaveStore store, Guid saveId, List<int> activeIds, int teamCount)
    {
        activeIds.Count.ShouldBe(teamCount * 4);
        for (int team = 0; team < teamCount; team++)
        {
            List<int> slice = activeIds.Skip(team * 4).Take(4).ToList();
            string typeName = $"MSS044-Type-{team:D3}";
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-over32-" + Guid.NewGuid().ToString("N"));
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
