using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.History.GetEventRound;
using MtgSoloSports.Features.History.GetEventTeamStandings;
using MtgSoloSports.Features.History.ListEventRounds;
using MtgSoloSports.Features.History.ListEvents;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-079: multi-group Type Cup rounds share squad rank group/round numbers
/// across qualification groups and the Final. Stored identity must remain
/// stage-aware and replay lookups must resolve each stage distinctly.
/// </summary>
public sealed class TypeCupStageIdentityTests
{
    private const int TeamCount = 65;

    [Fact]
    public async Task List_IncludesStageIdentity_ForEachTournamentStage()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79001UL, 79002UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            ListHistoryEventRoundsResponse listed =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            listed.Rounds.Count.ShouldBe(128);
            Dictionary<(int? Phase, int? Qual), int> byStage = CountByStage(listed);
            byStage[(1, 1)].ShouldBe(32);
            byStage[(1, 2)].ShouldBe(32);
            byStage[(1, 3)].ShouldBe(32);
            byStage[(2, 0)].ShouldBe(32);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Get_StageAware_Lookup_ResolvesDistinctPersistedRounds()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79101UL, 79102UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            GetHistoryEventRoundHandler handler = new(store);
            EventRoundView qual1 = await handler.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 1, 1);
            EventRoundView qual2 = await handler.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 1, 2);
            EventRoundView qual3 = await handler.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 1, 3);
            EventRoundView final = await handler.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 2, 0);

            qual1.TournamentPhase.ShouldBe(1);
            qual1.QualificationGroup.ShouldBe(1);
            qual2.QualificationGroup.ShouldBe(2);
            qual3.QualificationGroup.ShouldBe(3);
            final.TournamentPhase.ShouldBe(2);

            await AssertChecksumsMatchStoredAsync(store, saveId, qual1, qual2, qual3, final);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Get_LegacyAmbiguousLookup_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79201UL, 79202UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            GetHistoryEventRoundHandler handler = new(store);
            HistoryNotFoundException missing = await Should.ThrowAsync<HistoryNotFoundException>(
                () => handler.HandleAsync(saveId, 2, "type-cup-team", 1, 1));
            missing.Message.ShouldContain("tournamentPhase");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Get_MissingStage_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79301UL, 79302UL);
            await new PlayTypeCupTeamRoundHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            GetHistoryEventRoundHandler handler = new(store);
            HistoryNotFoundException missing = await Should.ThrowAsync<HistoryNotFoundException>(
                () => handler.HandleAsync(saveId, 2, "type-cup-team", 8, 4, CancellationToken.None, 2, 0));
            missing.Message.ShouldContain("has not been played");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TeamStandings_BeforeFinal_StartsFresh()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79401UL, 79402UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            GetHistoryEventTeamStandingsHandler standings = new(store);
            HistoryEventTeamStandingsResponse finalBaseline =
                await standings.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 2, 0);
            finalBaseline.IsFinal.ShouldBeFalse();
            finalBaseline.Teams.ShouldAllBe(t => t.ScoreThousandths == 0);

            HistoryEventTeamStandingsResponse qual2Baseline =
                await standings.HandleAsync(saveId, 2, "type-cup-team", 1, 1, CancellationToken.None, 1, 2);
            qual2Baseline.Teams.ShouldAllBe(t => t.ScoreThousandths == 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TeamStandings_LegacyAmbiguousBefore_RequiresStage()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79501UL, 79502UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            GetHistoryEventTeamStandingsHandler standings = new(store);
            await Should.ThrowAsync<ArgumentException>(
                () => standings.HandleAsync(saveId, 2, "type-cup-team", 1, 1));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DirectFinal_LegacyLookup_StillWorks()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, 32, 79601UL, 79602UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            ListHistoryEventRoundsResponse listed =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            listed.Rounds.Count.ShouldBe(32);

            EventRoundView view = await new GetHistoryEventRoundHandler(store).HandleAsync(saveId, 2, "type-cup-team", 1, 1);
            view.TournamentPhase.ShouldBe(2);

            HistoryEventTeamStandingsResponse final =
                await new GetHistoryEventTeamStandingsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            final.IsFinal.ShouldBeTrue();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ListEvents_ReportsTournamentTotal()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 79701UL, 79702UL);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            ListHistoryEventsResponse events = await new ListHistoryEventsHandler(store).HandleAsync(saveId, 2);
            HistoryEventSummary? typeCup = FindTypeCup(events);
            typeCup.ShouldNotBeNull();
            typeCup!.RoundsPlayed.ShouldBe(128);
            typeCup.TotalRounds.ShouldBe(128);
            typeCup.IsComplete.ShouldBeTrue();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static Dictionary<(int? Phase, int? Qual), int> CountByStage(ListHistoryEventRoundsResponse listed)
    {
        return listed.Rounds
            .GroupBy(r => (r.TournamentPhase, r.QualificationGroup))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static HistoryEventSummary? FindTypeCup(ListHistoryEventsResponse events)
    {
        foreach (HistoryEventSummary summary in events.Events)
        {
            if (string.Equals(summary.Event, "type-cup-team", StringComparison.Ordinal))
            {
                return summary;
            }
        }

        return null;
    }

    private static async Task AssertChecksumsMatchStoredAsync(
        SaveStore store, Guid saveId, EventRoundView qual1, EventRoundView qual2, EventRoundView qual3, EventRoundView final)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rows = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id && e.GroupNumber == 1 && e.RoundNumber == 1)
            .OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup)
            .ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(4);
        qual1.PayloadChecksum.ShouldBe(rows[0].PayloadChecksum);
        qual2.PayloadChecksum.ShouldBe(rows[1].PayloadChecksum);
        qual3.PayloadChecksum.ShouldBe(rows[2].PayloadChecksum);
        final.PayloadChecksum.ShouldBe(rows[3].PayloadChecksum);
    }

    private static async Task<Guid> SetupTournamentAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Type Stage {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount).ConfigureAwait(false);
        await SetDistinctTypesAsync(store, saveId, activeIds, teamCount).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        await new SelectTypeCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        await new DrawTypeCupQualificationGroupsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return saveId;
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int teamCount)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes.AsNoTracking().OrderBy(e => e.Id).Take(teamCount * 4).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static async Task SetDistinctTypesAsync(SaveStore store, Guid saveId, List<int> activeIds, int teamCount)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.Where(e => activeIds.Contains(e.Id)).ToListAsync().ConfigureAwait(false);
        for (int team = 0; team < teamCount; team++)
        {
            List<int> slice = activeIds.Skip(team * 4).Take(4).ToList();
            string typeName = $"MSS079-Type-{team:D3}";
            string json = JsonSerializer.Serialize(new[] { typeName });
            foreach (SaveAthleteEntity athlete in athletes.Where(a => slice.Contains(a.Id)))
            {
                athlete.CreatureTypesJson = json;
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task CreateEvenSeasonAsync(SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await EnsureSeasonAsync(context, seasonNumber).ConfigureAwait(false);
        LeagueEntity league = await EnsureLeagueAsync(context, season).ConfigureAwait(false);
        await EnsureMembershipsAsync(context, season, league, activeIds).ConfigureAwait(false);
        AddSeasonInputs(context, season, league, activeIds);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<SeasonEntity> EnsureSeasonAsync(SaveDbContext context, int seasonNumber)
    {
        SeasonEntity? existing = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.IsComplete = true;
            await context.SaveChangesAsync().ConfigureAwait(false);
            return existing;
        }

        SeasonEntity season = new() { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
        context.Seasons.Add(season);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return season;
    }

    private static async Task<LeagueEntity> EnsureLeagueAsync(SaveDbContext context, SeasonEntity season)
    {
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        if (league is not null)
        {
            return league;
        }

        LeagueEntity created = new() { SeasonId = season.Id, SportingColor = 0, Kind = (int)LeagueKind.Feeder, Name = "White League" };
        context.Leagues.Add(created);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return created;
    }

    private static async Task EnsureMembershipsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
    {
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

    private static void AddSeasonInputs(SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-type079-" + Guid.NewGuid().ToString("N"));
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

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
