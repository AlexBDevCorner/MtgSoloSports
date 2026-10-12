using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.FinishTypeCupTeamStage;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.History.GetEventTeamStandings;
using MtgSoloSports.Features.History.ListEventRounds;
using MtgSoloSports.Features.History.ListEvents;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-078 correction: the stage-scoped finish must stop at the active stage
/// boundary, reject stale stages without persisting, and complete the Final as
/// a distinct stage. Covers the P1 where a client-side snapshot plus unscoped
/// mutations could play the next group/Final.
/// </summary>
public sealed class FinishTypeCupTeamStageTests
{
    private const int TeamCount = 65;

    [Fact]
    public async Task Finish_FirstGroup_StopsBeforeNextGroup()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 80001UL, 80002UL);

            FinishTypeCupTeamStageResponse first =
                await new FinishTypeCupTeamStageHandler(store).HandleAsync(saveId, 1, 1, sourceSeasonNumber: 2);

            first.TournamentPhase.ShouldBe(1);
            first.QualificationGroup.ShouldBe(1);
            first.StageRoundsPlayed.ShouldBe(32);
            first.StageTotalRounds.ShouldBe(32);
            first.RoundsPlayedThisCall.ShouldBe(32);
            first.RoundsPlayed.ShouldBe(32);
            first.TotalRounds.ShouldBe(128);
            first.IsComplete.ShouldBeFalse();

            ListHistoryEventRoundsResponse listed =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            listed.Rounds.Count.ShouldBe(32);
            listed.Rounds.ShouldAllBe(r => r.TournamentPhase == 1 && r.QualificationGroup == 1);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
                int qual1 = await context.TypeCupTeamStandings.AsNoTracking()
                    .CountAsync(e => e.SourceSeasonId == season.Id && e.TournamentPhase == 1 && e.QualificationGroup == 1);
                qual1.ShouldBeGreaterThan(0);
                int qual2 = await context.TypeCupTeamStandings.AsNoTracking()
                    .CountAsync(e => e.SourceSeasonId == season.Id && e.TournamentPhase == 1 && e.QualificationGroup == 2);
                qual2.ShouldBe(0);
                int finalRows = await context.TypeCupTeamRounds.AsNoTracking()
                    .CountAsync(e => e.SourceSeasonId == season.Id && e.TournamentPhase == 2);
                finalRows.ShouldBe(0);
            }

            // Stale expected stage must not persist the next group.
            RunTypeCupTeamConflictException stale = await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => new FinishTypeCupTeamStageHandler(store).HandleAsync(saveId, 1, 1, sourceSeasonNumber: 2));
            stale.Message.ShouldContain("stage changed");

            ListHistoryEventRoundsResponse afterStale =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            afterStale.Rounds.Count.ShouldBe(32);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Finish_AllQualGroups_ThenFinal_CompletesTournament()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 80101UL, 80102UL);
            FinishTypeCupTeamStageHandler finish = new(store);

            await finish.HandleAsync(saveId, 1, 1, sourceSeasonNumber: 2);
            await finish.HandleAsync(saveId, 1, 2, sourceSeasonNumber: 2);
            FinishTypeCupTeamStageResponse third =
                await finish.HandleAsync(saveId, 1, 3, sourceSeasonNumber: 2);
            third.RoundsPlayed.ShouldBe(96);
            third.IsComplete.ShouldBeFalse();

            // Pending Final exposes zeroed finalists once quals are complete.
            GetHistoryEventTeamStandingsHandler standings = new(store);
            HistoryEventTeamStandingsResponse pending =
                await standings.HandleAsync(saveId, 2, "type-cup-team", cancellationToken: CancellationToken.None, tournamentPhase: 2, qualificationGroup: 0);
            pending.Teams.Count.ShouldBe(32);
            pending.Teams.ShouldAllBe(t => t.ScoreThousandths == 0);

            FinishTypeCupTeamStageResponse final =
                await finish.HandleAsync(saveId, 2, 0, sourceSeasonNumber: 2);
            final.RoundsPlayed.ShouldBe(128);
            final.TotalRounds.ShouldBe(128);
            final.IsComplete.ShouldBeTrue();
            final.TournamentPhase.ShouldBe(2);

            ListHistoryEventRoundsResponse listed =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            listed.Rounds.Count.ShouldBe(128);

            ListHistoryEventsResponse events = await new ListHistoryEventsHandler(store).HandleAsync(saveId, 2);
            HistoryEventSummary? typeCup = null;
            foreach (HistoryEventSummary summary in events.Events)
            {
                if (string.Equals(summary.Event, "type-cup-team", StringComparison.Ordinal))
                {
                    typeCup = summary;
                }
            }

            typeCup.ShouldNotBeNull();
            typeCup!.RoundsPlayed.ShouldBe(128);
            typeCup.IsComplete.ShouldBeTrue();

            // Tournament already resolved: further stage finish conflicts.
            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => finish.HandleAsync(saveId, 2, 0, sourceSeasonNumber: 2));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Finish_WithWrongExpected_RejectsWithoutPersisting()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupTournamentAsync(store, TeamCount, 80201UL, 80202UL);
            await new PlayTypeCupTeamRoundHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);

            RunTypeCupTeamConflictException wrong = await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => new FinishTypeCupTeamStageHandler(store).HandleAsync(saveId, 1, 2, sourceSeasonNumber: 2));
            wrong.Message.ShouldContain("stage changed");

            ListHistoryEventRoundsResponse listed =
                await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 2, "type-cup-team");
            listed.Rounds.Count.ShouldBe(1);
            listed.Rounds[0].TournamentPhase.ShouldBe(1);
            listed.Rounds[0].QualificationGroup.ShouldBe(1);

            // Null expected finishes the canonical stage (Group 1) to its boundary.
            FinishTypeCupTeamStageResponse finished =
                await new FinishTypeCupTeamStageHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            finished.RoundsPlayed.ShouldBe(32);
            finished.RoundsPlayedThisCall.ShouldBe(31);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<Guid> SetupTournamentAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Type Finish {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
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
            string typeName = $"MSS078-Type-{team:D3}";
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-type078-" + Guid.NewGuid().ToString("N"));
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
