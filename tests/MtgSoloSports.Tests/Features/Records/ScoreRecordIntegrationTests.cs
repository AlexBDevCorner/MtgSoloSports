using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class ScoreRecordIntegrationTests
{
    [Fact]
    public async Task LeagueRecords_AfterSeason_HavePerLeagueHoldersAndContext()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score League", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new CompleteSeasonHandler(store).HandleAsync(created.Detail.SaveId);

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);

            // Career records unchanged.
            response.Records.Count.ShouldBe(RecordKey.All.Count);

            // Scoring records cover all explicit keys.
            response.ScoringRecords.Count.ShouldBe(ScoreRecordKey.All.Count);

            // League single-round: every feeder scope has a holder with full context.
            foreach (string scope in new[] { "white", "blue", "black", "red", "green", "multicolor", "hybrid", "colorless" })
            {
                ScoringRecordEntry round = response.ScoringRecords.Single(r =>
                    string.Equals(r.RecordKey, ScoreRecordKey.LeagueSingleRound(scope), StringComparison.Ordinal));
                round.Category.ShouldBe("League");
                round.IsVacant.ShouldBeFalse();
                round.Value.ShouldBeGreaterThan(0);
                round.Holders.Count.ShouldBeGreaterThan(0);
                foreach (ScoringRecordHolderEntry holder in round.Holders)
                {
                    holder.AthleteId.ShouldNotBeNull();
                    holder.SeasonNumber.ShouldBe(1);
                    holder.StageNumber.ShouldNotBeNull();
                    holder.RoundNumber.ShouldNotBeNull();
                    holder.Competition.ShouldNotBeNullOrWhiteSpace();
                }

                ScoringRecordEntry stage = response.ScoringRecords.Single(r =>
                    string.Equals(r.RecordKey, ScoreRecordKey.LeagueStageBest(scope), StringComparison.Ordinal));
                stage.IsVacant.ShouldBeFalse();
                stage.Holders.Count.ShouldBeGreaterThan(0);
                stage.Holders[0].StageNumber.ShouldNotBeNull();

                ScoringRecordEntry points = response.ScoringRecords.Single(r =>
                    string.Equals(r.RecordKey, ScoreRecordKey.LeaguePointsBest(scope), StringComparison.Ordinal));
                points.IsVacant.ShouldBeFalse();
                points.Holders.Count.ShouldBeGreaterThan(0);
            }

            // Season 1 has no Superleague: those records stay vacant.
            response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.LeagueSingleRound("superleague"), StringComparison.Ordinal)).IsVacant.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScoringRecords_AfterSimulateSeasons_CoverColourCupAndTeams()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score Cups", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new SimulateSeasonsHandler(store).HandleAsync(created.Detail.SaveId, new SimulateSeasonsRequest(1));

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);

            ScoringRecordEntry colourRound = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupIndividualRoundBest, StringComparison.Ordinal));
            colourRound.IsVacant.ShouldBeFalse();
            colourRound.Value.ShouldBeGreaterThan(0);
            colourRound.Holders.Count.ShouldBeGreaterThan(0);
            colourRound.Holders[0].Competition.ShouldBe("Colour Cup individual");
            colourRound.Holders[0].RoundNumber.ShouldNotBeNull();
            colourRound.Holders[0].SeasonNumber.ShouldBe(1);

            ScoringRecordEntry colourStage = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupIndividualStageBest, StringComparison.Ordinal));
            colourStage.IsVacant.ShouldBeFalse();

            ScoringRecordEntry legRound = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamLegRoundBest, StringComparison.Ordinal));
            legRound.IsVacant.ShouldBeFalse();
            legRound.Holders[0].TeamKey.ShouldNotBeNullOrWhiteSpace();
            legRound.Holders[0].GroupNumber.ShouldNotBeNull();

            ScoringRecordEntry legStage = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamLegStageBest, StringComparison.Ordinal));
            legStage.IsVacant.ShouldBeFalse();
            legStage.Holders[0].TeamKey.ShouldNotBeNullOrWhiteSpace();

            ScoringRecordEntry teamTotal = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamTotalBest, StringComparison.Ordinal));
            teamTotal.IsVacant.ShouldBeFalse();
            teamTotal.Holders.Count.ShouldBeGreaterThan(0);
            teamTotal.Holders[0].AthleteId.ShouldBeNull();
            teamTotal.Holders[0].TeamKey.ShouldNotBeNullOrWhiteSpace();

            ScoringRecordEntry teamRound = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamRoundBest, StringComparison.Ordinal));
            teamRound.IsVacant.ShouldBeFalse();
            teamRound.Holders[0].RoundNumber.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Historical_OlderSeasonHoldsRecordAfterAdvancing()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score Historical", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new SimulateSeasonsHandler(store).HandleAsync(created.Detail.SaveId, new SimulateSeasonsRequest(2));

            int boostedAthlete;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity seasonOne = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                StageStandingEntity standing = await context.StageStandings
                    .Where(e => e.SeasonId == seasonOne.Id)
                    .OrderBy(e => e.Id)
                    .FirstAsync();
                boostedAthlete = standing.SaveAthleteId;
                standing.StageScoreThousandths = 9_999_999;
                await context.SaveChangesAsync();
            }

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);

            // Find the league scope for the boosted athlete's original league.
            string? scope = null;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity seasonOne = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
                StageStandingEntity standing = await context.StageStandings.AsNoTracking()
                    .Where(e => e.SeasonId == seasonOne.Id && e.SaveAthleteId == boostedAthlete)
                    .OrderBy(e => e.Id)
                    .FirstAsync();
                LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(e => e.Id == standing.LeagueId);
                scope = ScoreRecordKey.LeagueScopeFor(league.Kind, league.SportingColor);
            }

            scope.ShouldNotBeNull();
            ScoringRecordEntry stageRecord = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.LeagueStageBest(scope!), StringComparison.Ordinal));
            stageRecord.Value.ShouldBe(9_999_999);
            stageRecord.Holders.Any(h => h.AthleteId == boostedAthlete && h.SeasonNumber == 1).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Ties_TwoEqualBestValuesBothPreserved()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score Ties", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new CompleteSeasonHandler(store).HandleAsync(created.Detail.SaveId);

            int firstAthlete;
            int secondAthlete;
            string scope;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity seasonOne = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                List<StageStandingEntity> standings = await context.StageStandings
                    .Where(e => e.SeasonId == seasonOne.Id)
                    .OrderBy(e => e.Id)
                    .Take(2)
                    .ToListAsync();
                standings.Count.ShouldBe(2);
                firstAthlete = standings[0].SaveAthleteId;
                secondAthlete = standings[1].SaveAthleteId;
                // Force both into the same league scope for a deterministic tie.
                LeagueEntity firstLeague = await context.Leagues.SingleAsync(e => e.Id == standings[0].LeagueId);
                standings[1].LeagueId = standings[0].LeagueId;
                standings[0].StageScoreThousandths = 8_888_888;
                standings[1].StageScoreThousandths = 8_888_888;
                await context.SaveChangesAsync();
                scope = ScoreRecordKey.LeagueScopeFor(firstLeague.Kind, firstLeague.SportingColor);
            }

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);
            ScoringRecordEntry stageRecord = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.LeagueStageBest(scope), StringComparison.Ordinal));
            stageRecord.Value.ShouldBe(8_888_888);
            stageRecord.Holders.Select(h => h.AthleteId).ShouldContain(firstAthlete);
            stageRecord.Holders.Select(h => h.AthleteId).ShouldContain(secondAthlete);
            // Deterministic ordering: name ascending then id.
            List<string> orderedNames = stageRecord.Holders.Select(h =>
            {
                using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
                return context.SaveAthletes.AsNoTracking().Single(e => e.Id == h.AthleteId!.Value).Name;
            }).ToList();
            orderedNames.ShouldBe(orderedNames.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MembershipChanges_DoNotRewriteHistoricalOwnership()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score Membership", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new CompleteSeasonHandler(store).HandleAsync(created.Detail.SaveId);

            int holderId;
            string originalLeague;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity seasonOne = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                StageStandingEntity standing = await context.StageStandings
                    .Where(e => e.SeasonId == seasonOne.Id)
                    .OrderByDescending(e => e.StageScoreThousandths)
                    .FirstAsync();
                holderId = standing.SaveAthleteId;
                LeagueEntity league = await context.Leagues.SingleAsync(e => e.Id == standing.LeagueId);
                originalLeague = league.Name;
                // Boost to an unbeatable value so this athlete/occurrence is the record.
                standing.StageScoreThousandths = 9_999_999;
                // Simulate a later membership change: move the athlete to the pool (LeagueId null)
                // for season 1 is not valid (would break invariants), so instead move a
                // different season's membership. Create a synthetic season-2 pool entry.
                SeasonEntity seasonTwo = new() { SeasonNumber = 2, HasSuperleague = false, IsComplete = false };
                context.Seasons.Add(seasonTwo);
                await context.SaveChangesAsync();
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = seasonTwo.Id,
                    LeagueId = null,
                    SaveAthleteId = holderId,
                    SportingColor = 0,
                    DrawIndex = 0,
                });
                await context.SaveChangesAsync();
            }

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);
            List<ScoringRecordEntry> leagueStages = response.ScoringRecords
                .Where(r => string.Equals(r.Category, "League", StringComparison.Ordinal) && r.RecordKey.Contains("stage_best", StringComparison.Ordinal))
                .ToList();
            ScoringRecordEntry? holding = leagueStages.FirstOrDefault(r =>
                r.Holders.Any(h => h.AthleteId == holderId && h.SeasonNumber == 1));
            holding.ShouldNotBeNull();
            holding!.Holders.First(h => h.AthleteId == holderId).LeagueName.ShouldBe(originalLeague);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TeamTotals_TiesPreserved()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Score Team Ties", 424201UL, 848402UL, UniverseTestCatalog.Build());
            await new SimulateSeasonsHandler(store).HandleAsync(created.Detail.SaveId, new SimulateSeasonsRequest(1));

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                List<ColorCupTeamStandingEntity> standings = await context.ColorCupTeamStandings
                    .OrderBy(e => e.Id)
                    .Take(2)
                    .ToListAsync();
                standings.Count.ShouldBe(2);
                standings[0].TeamScoreThousandths = 7_777_777;
                standings[1].TeamScoreThousandths = 7_777_777;
                await context.SaveChangesAsync();
            }

            GetRecordsHandler handler = new(store);
            GetRecordsResponse response = await handler.HandleAsync(created.Detail.SaveId);
            ScoringRecordEntry total = response.ScoringRecords.Single(r =>
                string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamTotalBest, StringComparison.Ordinal));
            total.Value.ShouldBe(7_777_777);
            total.Holders.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-score-" + Guid.NewGuid().ToString("N"));
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
