using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
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
/// MSS-071: qualification wildcards by performance, not group number.
/// </summary>
public sealed class TypeCupWildcardTournamentTests
{
    [Fact]
    public async Task EightyFour_TopTenPlusTwoBestElevenths()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 84, 84000UL, 84100UL);
            await SelectAsync(store, saveId);
            DrawTypeCupQualificationGroupsResponse draw = await DrawAsync(store, saveId);
            draw.GroupSizes.ShouldBe([28, 28, 28]);
            draw.FinalPlacesPerGroup.ShouldBe([10, 10, 10]);
            draw.QualificationPolicyVersion.ShouldBe(RulesV1.WildcardTypeCupTournamentFormatVersion);
            draw.WildcardCount.ShouldBe(2);

            RunTypeCupTeamResponse run = await RunAsync(store, saveId);
            run.TeamCount.ShouldBe(32);

            GetTypeCupTournamentHandler handler = new(store);
            GetTypeCupTournamentResponse summary = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.QualificationPolicyVersion.ShouldBe(2);
            summary.WildcardCount.ShouldBe(2);
            summary.GuaranteedPlacesPerGroup.ShouldBe([10, 10, 10]);
            summary.Finalists.Count.ShouldBe(32);
            summary.Wildcards.ShouldNotBeNull();
            summary.Wildcards!.Count.ShouldBe(2);

            List<string> candidates = summary.QualificationGroups
                .Select(g => g.WildcardCandidate)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
            candidates.Count.ShouldBe(3);
            List<string> winners = summary.Wildcards!.Select(w => w.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
            winners.Count.ShouldBe(2);
            foreach (string winner in winners)
            {
                candidates.Contains(winner, StringComparer.Ordinal).ShouldBeTrue();
                summary.Finalists.Contains(winner, StringComparer.Ordinal).ShouldBeTrue();
            }

            summary.Finalists.Distinct(StringComparer.Ordinal).Count().ShouldBe(32);
            foreach (var group in summary.QualificationGroups)
            {
                group.GuaranteedPlaces.ShouldBe(10);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(65)]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(85)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(150)]
    public async Task ScalableFields_GroupSizesBounded_AndThirtyTwoFinalists(int teamCount)
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, teamCount, 86000UL + (ulong)teamCount, 86100UL + (ulong)teamCount);
            await SelectAsync(store, saveId);
            DrawTypeCupQualificationGroupsResponse draw = await DrawAsync(store, saveId);
            foreach (int size in draw.GroupSizes)
            {
                size.ShouldBeLessThanOrEqualTo(32);
            }

            (draw.GroupSizes.Max() - draw.GroupSizes.Min()).ShouldBeLessThanOrEqualTo(1);
            (draw.GuaranteedPlacesPerGroup!.Sum() + draw.WildcardCount).ShouldBe(32);

            RunTypeCupTeamResponse run = await RunAsync(store, saveId);
            run.TeamCount.ShouldBe(32);

            GetTypeCupTournamentHandler handler = new(store);
            GetTypeCupTournamentResponse summary = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.Finalists.Count.ShouldBe(32);
            summary.Finalists.Distinct(StringComparer.Ordinal).Count().ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OneShot_EqualsStepByStep_For84Teams()
    {
        const int teamCount = 84;
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareTournamentAsync(teamCount, 88000UL, 88100UL);
        var (stepStore, stepRoot, stepId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-wild-");
        try
        {
            await new RunTypeCupTeamHandler(oneShotStore).HandleAsync(oneShotId, sourceSeasonNumber: 2);
            PlayTypeCupTeamRoundHandler step = new(stepStore);
            int total = (3 * 32) + 32;
            for (int index = 0; index < total; index++)
            {
                PlayTypeCupTeamRoundResponse played = await step.HandleAsync(stepId, sourceSeasonNumber: 2);
                played.RoundsPlayed.ShouldBe(index + 1);
                played.TotalRounds.ShouldBe(total);
            }

            List<string> stepSnapshot = await SnapshotAsync(stepStore, stepId);
            List<string> oneShotSnapshot = await SnapshotAsync(oneShotStore, oneShotId);
            stepSnapshot.ShouldBe(oneShotSnapshot);
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(stepRoot);
        }
    }

    [Fact]
    public async Task OldFixedQuotaDraw_StillUsesElevenElevenTen()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 65, 89000UL, 89100UL);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);
            await ReplaceDrawWithFixedQuotasAsync(store, saveId, [11, 11, 10]);

            RunTypeCupTeamResponse run = await RunAsync(store, saveId);
            run.TeamCount.ShouldBe(32);

            GetTypeCupTournamentHandler handler = new(store);
            GetTypeCupTournamentResponse summary = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.QualificationPolicyVersion.ShouldBe(1);
            summary.FinalPlacesPerGroup.ShouldBe([11, 11, 10]);
            summary.Finalists.Count.ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTournamentAsync(int teamCount, ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        Guid saveId = await SetupFieldAsync(store, teamCount, seed, stream).ConfigureAwait(false);
        await SelectAsync(store, saveId).ConfigureAwait(false);
        await DrawAsync(store, saveId).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task<SelectTypeCupTeamsResponse> SelectAsync(SaveStore store, Guid saveId)
    {
        SelectTypeCupTeamsHandler select = new(store);
        return await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task<DrawTypeCupQualificationGroupsResponse> DrawAsync(SaveStore store, Guid saveId)
    {
        DrawTypeCupQualificationGroupsHandler draw = new(store);
        return await draw.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task<RunTypeCupTeamResponse> RunAsync(SaveStore store, Guid saveId)
    {
        RunTypeCupTeamHandler handler = new(store);
        return await handler.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task ReplaceDrawWithFixedQuotasAsync(SaveStore store, Guid saveId, int[] quotas)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<TypeCupTournamentDrawEntity> existing = await context.TypeCupTournamentDraws
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        RulesV1 rules = RulesV1.CreateDefault();
        List<string> types = existing.Select(e => e.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(types.Count, rules);
        TypeCupTournamentDrawEntity first = existing.First();
        context.TypeCupTournamentDraws.RemoveRange(existing);
        await context.SaveChangesAsync().ConfigureAwait(false);
        List<TypeCupTournamentFormat.QualificationAssignment> assignments = BuildAssignments(types, sizes);
        string checksum = TypeCupTournamentFormat.ComputeDrawChecksum(assignments, types.Count, sizes);
        List<TypeCupTournamentDrawEntity> rows = BuildFixedRows(source, types, sizes, quotas, first, assignments, checksum);
        context.TypeCupTournamentDraws.AddRange(rows);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static List<TypeCupTournamentFormat.QualificationAssignment> BuildAssignments(List<string> types, IReadOnlyList<int> sizes)
    {
        List<TypeCupTournamentFormat.QualificationAssignment> assignments = new(types.Count);
        int index = 0;
        for (int group = 1; group <= sizes.Count; group++)
        {
            for (int i = 0; i < sizes[group - 1]; i++)
            {
                assignments.Add(new TypeCupTournamentFormat.QualificationAssignment(types[index++], group));
            }
        }

        return assignments;
    }

    private static List<TypeCupTournamentDrawEntity> BuildFixedRows(
        SeasonEntity source,
        List<string> types,
        IReadOnlyList<int> sizes,
        int[] quotas,
        TypeCupTournamentDrawEntity first,
        List<TypeCupTournamentFormat.QualificationAssignment> assignments,
        string checksum)
    {
        Dictionary<string, int> groupByType = assignments.ToDictionary(a => a.CreatureType, a => a.QualificationGroup, StringComparer.Ordinal);
        List<TypeCupTournamentDrawEntity> rows = new(types.Count);
        foreach (string type in types.OrderBy(t => t, StringComparer.Ordinal))
        {
            int group = groupByType[type];
            rows.Add(new TypeCupTournamentDrawEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                CreatureType = type,
                QualificationGroup = group,
                GroupSize = sizes[group - 1],
                FieldTeamCount = types.Count,
                QualificationGroupCount = sizes.Count,
                FinalPlacesForGroup = quotas[group - 1],
                RulesVersion = first.RulesVersion,
                TournamentFormatVersion = RulesV1.FixedQuotaTypeCupTournamentFormatVersion,
                RngBeforeState = first.RngBeforeState,
                RngBeforeStream = first.RngBeforeStream,
                RngAfterState = first.RngAfterState,
                RngAfterStream = first.RngAfterStream,
                DrawChecksum = checksum,
            });
        }

        return rows;
    }

    private static async Task<List<string>> SnapshotAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> lines = [];
        lines.AddRange(await context.TypeCupTeamRounds.OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .Select(e => "round:" + e.TournamentPhase + ":" + e.QualificationGroup + ":" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.TypeCupTeamStandings.OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.TeamRank)
            .Select(e => $"team:{e.TournamentPhase}:{e.QualificationGroup}:{e.CreatureType}:{e.TeamRank}:{e.TeamScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        lines.Add($"rng:{rng.State}:{rng.Stream}");
        return lines;
    }

    private static async Task<Guid> SetupFieldAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Wild {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
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
            string typeName = $"MSS071-Type-{team:D3}";
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

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-wild-" + Guid.NewGuid().ToString("N"));
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
