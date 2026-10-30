using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class StandingsStagePlacementsTests
{
    [Fact]
    public async Task StandingsReport_NoCompletedStage_ExplicitEmpty()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Empty");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            var handler = new GetStandingsReportHandler(store);

            GetStandingsReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, leagueId, 1, "json");
            using JsonDocument json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
            json.RootElement.GetProperty("standings").GetArrayLength().ShouldBe(0);
            JsonElement placements = json.RootElement.GetProperty("stagePlacements");
            placements.GetProperty("completedStages").GetInt32().ShouldBe(0);
            placements.GetProperty("stageNumbers").GetArrayLength().ShouldBe(0);
            placements.GetProperty("placements").GetArrayLength().ShouldBe(0);

            GetStandingsReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, leagueId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            md.ShouldContain("## Stage placements (0 completed stages)");
            md.ShouldContain("No completed stages yet");
            md.ShouldNotContain("P1");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_ExactlyOneStage_MatrixAndExactness()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements One");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteOneStageForAllLeaguesAsync(store, saveId);

            var handler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, leagueId, 1, "json");
            using JsonDocument json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
            json.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("provisional");
            JsonElement stagePlacements = json.RootElement.GetProperty("stagePlacements");
            stagePlacements.GetProperty("completedStages").GetInt32().ShouldBe(1);
            stagePlacements.GetProperty("stageNumbers").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([1]);
            JsonElement cells = stagePlacements.GetProperty("placements");
            AssertSingleStageCells(cells);
            await AssertCellsMatchPersistedAsync(store, saveId, leagueId, cells);

            // Markdown alignment with JSON ranks.
            GetStandingsReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, leagueId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            AssertSingleStageMarkdown(md, cells, jsonFile);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_PartialStage_NeverFabricatesNextStage()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Partial");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteOneStageForAllLeaguesAsync(store, saveId);

            var advance = new AdvanceRoundHandler(store);
            AdvanceRoundResponse round = await advance.HandleAsync(saveId, leagueId);
            round.StageNumber.ShouldBe(2);

            var handler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, leagueId, 1, "json");
            using JsonDocument json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
            JsonElement stagePlacements = json.RootElement.GetProperty("stagePlacements");
            stagePlacements.GetProperty("completedStages").GetInt32().ShouldBe(1);
            stagePlacements.GetProperty("placements").EnumerateArray().All(e => e.GetProperty("stageNumber").GetInt32() == 1).ShouldBeTrue();

            GetStandingsReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, leagueId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            md.ShouldContain("## Stage placements (1 completed stages)");
            md.ShouldNotContain("### Stage 2");
            md.ShouldNotContain(" Stage 2 |");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_TwoStages_32AthletesPerStageAndBlocks()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Two");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteStagesForAllLeaguesAsync(store, saveId, 2);

            var handler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, leagueId, 1, "json");
            using JsonDocument json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
            json.RootElement.GetProperty("standings").GetArrayLength().ShouldBe(32);
            JsonElement cells = json.RootElement.GetProperty("stagePlacements").GetProperty("placements");
            cells.GetArrayLength().ShouldBe(64);
            foreach (int stage in new[] { 1, 2 })
            {
                cells.EnumerateArray().Where(e => e.GetProperty("stageNumber").GetInt32() == stage)
                    .Select(e => e.GetProperty("stageRank").GetInt32()).OrderBy(r => r)
                    .ShouldBe(Enumerable.Range(1, 32).ToList());
                cells.EnumerateArray().Where(e => e.GetProperty("stageNumber").GetInt32() == stage)
                    .Select(e => e.GetProperty("athleteId").GetInt32()).Distinct().Count().ShouldBe(32);
            }

            // No duplicate (athlete, stage).
            cells.EnumerateArray().Select(e => (e.GetProperty("athleteId").GetInt32(), e.GetProperty("stageNumber").GetInt32()))
                .Distinct().Count().ShouldBe(64);

            GetStandingsReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, leagueId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            md.ShouldContain("## Stage placements (2 completed stages)");
            md.ShouldContain("P1");
            md.ShouldContain("Stage 1");
            md.ShouldContain("Stage 2");

            // Existing fields intact.
            json.RootElement.GetProperty("reportType").GetString().ShouldBe("standings-table");
            json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_SpecialCharacterNames_EscapedInMarkdownPreservedInJson()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Special");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteOneStageForAllLeaguesAsync(store, saveId);

            int targetAthlete = await RenameOneRosterAthleteAsync(store, saveId, leagueId, "A|B\nC");

            var handler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, leagueId, 1, "json");
            using JsonDocument json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
            JsonElement row = json.RootElement.GetProperty("standings").EnumerateArray().Single(e => e.GetProperty("athleteId").GetInt32() == targetAthlete);
            row.GetProperty("name").GetString().ShouldBe("A|B\nC");

            GetStandingsReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, leagueId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            md.ShouldContain("A\\|B C");
            md.ShouldNotContain("A|B");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_FeederScoping_NeverDefaultsToSuperleague()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Feeders");
        try
        {
            await CompleteOneStageForAllLeaguesAsync(store, saveId);
            List<int> leagues = await AllLeagueIdsAsync(store, saveId);
            leagues.Count.ShouldBeGreaterThanOrEqualTo(2);
            int first = leagues[0];
            int second = leagues[1];

            var handler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile firstJson = await handler.HandleAsync(saveId, first, 1, "json");
            GetStandingsReportHandler.ReportFile secondJson = await handler.HandleAsync(saveId, second, 1, "json");
            HashSet<int> firstAthletes = PlacementsAthletes(firstJson);
            HashSet<int> secondAthletes = PlacementsAthletes(secondJson);
            firstAthletes.Count.ShouldBe(32);
            secondAthletes.Count.ShouldBe(32);
            firstAthletes.Overlaps(secondAthletes).ShouldBeFalse();

            // Each matches its own GetSeasonPlacements export.
            var placementsHandler = new GetSeasonPlacementsHandler(store);
            GetSeasonPlacementsResponse expected = await placementsHandler.HandleAsync(saveId, 1, first);
            HashSet<int> expectedAthletes = expected.Placements.Select(c => c.AthleteId).ToHashSet();
            firstAthletes.SetEquals(expectedAthletes).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_CorruptStageData_FailsActionably()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Corrupt");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteOneStageForAllLeaguesAsync(store, saveId);
            await CorruptOneStageRowAsync(store, saveId, leagueId);

            var handler = new GetStandingsReportHandler(store);
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "json"));
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "md"));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_IncompleteStageStandings_FailsActionably()
    {
        var (store, root, saveId) = await CreateSaveAsync("Placements Incomplete Cursor");
        try
        {
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            await CompleteOneStageForAllLeaguesAsync(store, saveId);
            await InsertWellFormedStandingsForIncompleteStageAsync(store, saveId, leagueId);

            var handler = new GetStandingsReportHandler(store);
            InvalidOperationException jsonEx = await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "json"));
            jsonEx.Message.ShouldContain("not marked complete");
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "md"));

            // Same orphan rows must also abort the dossier's Superleague trajectory (season 1 fallback is the first league).
            var dossierHandler = new GetSeasonDossierHandler(store);
            await Should.ThrowAsync<InvalidOperationException>(() => dossierHandler.HandleAsync(saveId, 1, "json"));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StandingsReport_CorruptFinalRoster_FailsActionably()
    {
        var (store, root, saveId) = await CreateSeason1CompleteAsync("Placements Corrupt Final");
        try
        {
            int leagueId = await FirstLeagueIdForSeasonAsync(store, saveId, 1);
            await CorruptOneFinalRowAsync(store, saveId, leagueId);

            var handler = new GetStandingsReportHandler(store);
            InvalidOperationException jsonEx = await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "json"));
            jsonEx.Message.ShouldContain("does not match");
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(saveId, leagueId, 1, "md"));

            // Same corrupt final roster must also abort the dossier's Superleague trajectory (season 1 fallback is the first league).
            var dossierHandler = new GetSeasonDossierHandler(store);
            await Should.ThrowAsync<InvalidOperationException>(() => dossierHandler.HandleAsync(saveId, 1, "json"));
            await Should.ThrowAsync<InvalidOperationException>(() => dossierHandler.HandleAsync(saveId, 1, "md"));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Dossier_SuperleagueTrajectory_MatchesStandingsExport()
    {
        var (store, root, saveId) = await CreateSeason1CompleteAsync("Dossier Trajectory");
        try
        {
            int season1League = await FirstLeagueIdForSeasonAsync(store, saveId, 1);
            var standingsHandler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile standingsJson = await standingsHandler.HandleAsync(saveId, season1League, 1, "json");

            var dossierHandler = new GetSeasonDossierHandler(store);
            GetSeasonDossierHandler.ReportFile dossierJsonFile = await dossierHandler.HandleAsync(saveId, 1, "json");
            using JsonDocument dossier = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(dossierJsonFile.Content));
            JsonElement superleague = dossier.RootElement.GetProperty("superleague");
            JsonElement trajectory = superleague.GetProperty("stagePlacements");
            trajectory.GetProperty("completedStages").GetInt32().ShouldBe(32);
            trajectory.GetProperty("placements").GetArrayLength().ShouldBe(32 * 32);

            using JsonDocument standingsDoc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(standingsJson.Content));
            string Normalize(JsonElement e)
            {
                return string.Join(";", e.EnumerateArray().OrderBy(x => x.GetProperty("stageNumber").GetInt32()).ThenBy(x => x.GetProperty("stageRank").GetInt32())
                    .Select(x => $"{x.GetProperty("stageNumber").GetInt32()}:{x.GetProperty("athleteId").GetInt32()}:{x.GetProperty("stageRank").GetInt32()}:{x.GetProperty("championshipPointsThousandths").GetInt32()}:{x.GetProperty("earnedBonusThousandths").GetInt32()}:{x.GetProperty("stageScoreThousandths").GetInt32()}:{x.GetProperty("roundWins").GetInt32()}"));
            }

            Normalize(standingsDoc.RootElement.GetProperty("stagePlacements").GetProperty("placements"))
                .ShouldBe(Normalize(trajectory.GetProperty("placements")));

            // Winners preserved and derived from same cells.
            dossier.RootElement.GetProperty("stageProgression").GetArrayLength().ShouldBe(32);
            foreach (JsonElement winner in dossier.RootElement.GetProperty("stageProgression").EnumerateArray())
            {
                int stage = winner.GetProperty("stageNumber").GetInt32();
                JsonElement cell = trajectory.GetProperty("placements").EnumerateArray()
                    .Single(e => e.GetProperty("stageNumber").GetInt32() == stage && e.GetProperty("stageRank").GetInt32() == 1);
                winner.GetProperty("winnerAthleteId").GetInt32().ShouldBe(cell.GetProperty("athleteId").GetInt32());
                winner.GetProperty("winnerPointsThousandths").GetInt32().ShouldBe(cell.GetProperty("championshipPointsThousandths").GetInt32());
            }

            // Markdown parity.
            GetSeasonDossierHandler.ReportFile dossierMd = await dossierHandler.HandleAsync(saveId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(dossierMd.Content);
            md.ShouldContain("Superleague stage placements");
            md.ShouldContain("(32 completed stages)");
            md.ShouldContain("legacy fallback");
            md.ShouldContain("### Stages 1-8");
            md.ShouldContain("### Stages 9-16");
            md.ShouldContain("### Stages 17-24");
            md.ShouldContain("### Stages 25-32");
            md.ShouldContain("P1");
            md.ShouldContain("## Stage progression (32 completed stages)");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Dossier_Season1LegacyFallback_LabelledAndScoped()
    {
        var (store, root, saveId) = await CreateSeason1CompleteAsync("Dossier Legacy");
        try
        {
            var dossierHandler = new GetSeasonDossierHandler(store);
            GetSeasonDossierHandler.ReportFile dossierJsonFile = await dossierHandler.HandleAsync(saveId, 1, "json");
            using JsonDocument dossier = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(dossierJsonFile.Content));
            JsonElement superleague = dossier.RootElement.GetProperty("superleague");
            superleague.GetProperty("isOfficialSuperleague").GetBoolean().ShouldBeFalse();

            GetSeasonDossierHandler.ReportFile dossierMd = await dossierHandler.HandleAsync(saveId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(dossierMd.Content);
            md.ShouldContain("legacy fallback");
            md.ShouldContain("Superleague stage placements");
            md.ShouldNotContain("Superleague stage placements (0 completed stages)");

            // Feeder standings export for the fallback league matches dossier trajectory league.
            int fallbackLeague = superleague.GetProperty("leagueId").GetInt32();
            var standingsHandler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile feederJson = await standingsHandler.HandleAsync(saveId, fallbackLeague, 1, "json");
            using JsonDocument feeder = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(feederJson.Content));
            feeder.RootElement.GetProperty("stagePlacements").GetProperty("placements").GetArrayLength()
                .ShouldBe(superleague.GetProperty("stagePlacements").GetProperty("placements").GetArrayLength());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Dossier_Provisional_OnlyCompletedStages()
    {
        var (store, root, saveId) = await CreateSaveAsync("Dossier Provisional");
        try
        {
            await CompleteOneStageForAllLeaguesAsync(store, saveId);
            int leagueId = await FirstLeagueIdAsync(store, saveId);
            var advance = new AdvanceRoundHandler(store);
            await advance.HandleAsync(saveId, leagueId);

            var dossierHandler = new GetSeasonDossierHandler(store);
            GetSeasonDossierHandler.ReportFile dossierJsonFile = await dossierHandler.HandleAsync(saveId, 1, "json");
            using JsonDocument dossier = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(dossierJsonFile.Content));
            dossier.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("provisional");
            dossier.RootElement.GetProperty("superleague").GetProperty("stagePlacements").GetProperty("completedStages").GetInt32().ShouldBe(1);
            dossier.RootElement.GetProperty("superleague").GetProperty("stagePlacements").GetProperty("placements")
                .EnumerateArray().All(e => e.GetProperty("stageNumber").GetInt32() == 1).ShouldBeTrue();

            GetSeasonDossierHandler.ReportFile dossierMd = await dossierHandler.HandleAsync(saveId, 1, "md");
            string md = System.Text.Encoding.UTF8.GetString(dossierMd.Content);
            md.ShouldContain("Superleague stage placements");
            md.ShouldContain("(1 completed stages)");
            md.ShouldNotContain("### Stage 2");
            md.ShouldNotContain(" Stage 2 |");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Reports_HistoricalSeason_KeepsIdentity()
    {
        var (store, root, saveId) = await CreateSaveAsync("Reports History");
        try
        {
            int season1League = await FirstLeagueIdAsync(store, saveId);
            var bulk = new SimulateSeasonsHandler(store);
            await bulk.HandleAsync(saveId, new SimulateSeasonsRequest(1));

            // Season 1 historical export still complete with 32 stages.
            var standingsHandler = new GetStandingsReportHandler(store);
            GetStandingsReportHandler.ReportFile histJson = await standingsHandler.HandleAsync(saveId, season1League, 1, "json");
            using JsonDocument hist = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(histJson.Content));
            hist.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("final");
            hist.RootElement.GetProperty("stagePlacements").GetProperty("completedStages").GetInt32().ShouldBe(32);

            // Cross-season league rejected.
            int season2League = await FirstLeagueIdForSeasonAsync(store, saveId, 2);
            season2League.ShouldNotBe(season1League);
            await Should.ThrowAsync<HistoryNotFoundException>(() => standingsHandler.HandleAsync(saveId, season1League, 2, "json"));
            await Should.ThrowAsync<HistoryNotFoundException>(() => standingsHandler.HandleAsync(saveId, season2League, 1, "json"));

            // Placements match authoritative matrix.
            var placementsHandler = new GetSeasonPlacementsHandler(store);
            GetSeasonPlacementsResponse matrix = await placementsHandler.HandleAsync(saveId, 1, season1League);
            matrix.Placements.Count.ShouldBe(32 * 32);
            HashSet<(int Athlete, int Stage)> expected = matrix.Placements.Select(c => (c.AthleteId, c.StageNumber)).ToHashSet();
            HashSet<(int Athlete, int Stage)> actual = hist.RootElement.GetProperty("stagePlacements").GetProperty("placements")
                .EnumerateArray().Select(e => (e.GetProperty("athleteId").GetInt32(), e.GetProperty("stageNumber").GetInt32())).ToHashSet();
            actual.SetEquals(expected).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static HashSet<int> PlacementsAthletes(GetStandingsReportHandler.ReportFile file)
    {
        using JsonDocument doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(file.Content));
        return doc.RootElement.GetProperty("stagePlacements").GetProperty("placements")
            .EnumerateArray().Select(e => e.GetProperty("athleteId").GetInt32()).ToHashSet();
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> CreateSaveAsync(string name)
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-stageplacements-");
        SaveStore.CreationRecord created = await store.CreateAsync(name, 4242UL, 777UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> CreateSeason1CompleteAsync(string name)
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-stageplacements-");
        SaveStore.CreationRecord created = await store.CreateAsync(name, 4242UL, 777UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await new CompleteSeasonHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static async Task<List<int>> AllLeagueIdsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static async Task<int> FirstLeagueIdForSeasonAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        LeagueEntity league = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == season.Id).OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static async Task CompleteOneStageForAllLeaguesAsync(SaveStore store, Guid saveId)
    {
        await CompleteStagesForAllLeaguesAsync(store, saveId, 1).ConfigureAwait(false);
    }

    private static async Task CompleteStagesForAllLeaguesAsync(SaveStore store, Guid saveId, int stages)
    {
        var complete = new CompleteStageHandler(store);
        for (int i = 0; i < stages; i++)
        {
            List<int> leagues = await AllLeagueIdsAsync(store, saveId).ConfigureAwait(false);
            foreach (int leagueId in leagues)
            {
                await complete.HandleAsync(saveId, leagueId).ConfigureAwait(false);
            }
        }
    }

    private static void AssertSingleStageCells(JsonElement cells)
    {
        cells.GetArrayLength().ShouldBe(32);
        cells.EnumerateArray().Select(e => e.GetProperty("stageRank").GetInt32()).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 32).ToList());
        foreach (JsonElement cell in cells.EnumerateArray())
        {
            cell.GetProperty("stageNumber").GetInt32().ShouldBe(1);
            cell.TryGetProperty("seasonRank", out _).ShouldBeFalse();
        }
    }

    private static async Task AssertCellsMatchPersistedAsync(SaveStore store, Guid saveId, int leagueId, JsonElement cells)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int seasonId = await context.Seasons.AsNoTracking().Where(e => e.SeasonNumber == 1).Select(e => e.Id).SingleAsync().ConfigureAwait(false);
        List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(32);
        foreach (StageStandingEntity row in rows)
        {
            JsonElement match = cells.EnumerateArray().Single(e => e.GetProperty("athleteId").GetInt32() == row.SaveAthleteId);
            match.GetProperty("stageRank").GetInt32().ShouldBe(row.StageRank);
            match.GetProperty("championshipPointsThousandths").GetInt32().ShouldBe(row.ChampionshipPointsThousandths);
            match.GetProperty("earnedBonusThousandths").GetInt32().ShouldBe(row.EarnedBonusThousandths);
            match.GetProperty("stageScoreThousandths").GetInt32().ShouldBe(row.StageScoreThousandths);
            match.GetProperty("roundWins").GetInt32().ShouldBe(row.RoundWins);
        }
    }

    private static void AssertSingleStageMarkdown(string md, JsonElement cells, GetStandingsReportHandler.ReportFile jsonFile)
    {
        md.ShouldContain("## Stage placements (1 completed stages)");
        md.ShouldContain("### Stage 1");
        md.ShouldContain("Stage 1");
        Dictionary<int, int> jsonRanks = cells.EnumerateArray().ToDictionary(e => e.GetProperty("athleteId").GetInt32(), e => e.GetProperty("stageRank").GetInt32());
        using JsonDocument standingsJson = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(jsonFile.Content));
        foreach (JsonElement row in standingsJson.RootElement.GetProperty("standings").EnumerateArray())
        {
            int athleteId = row.GetProperty("athleteId").GetInt32();
            md.ShouldContain($"(id {athleteId})");
            md.ShouldContain($"P{jsonRanks[athleteId]}");
        }
    }

    private static async Task<int> RenameOneRosterAthleteAsync(SaveStore store, Guid saveId, int leagueId, string newName)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().OrderBy(e => e.SeasonNumber).FirstAsync().ConfigureAwait(false);
        SeasonMembershipEntity membership = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .OrderBy(e => e.SaveAthleteId).FirstAsync().ConfigureAwait(false);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == membership.SaveAthleteId).ConfigureAwait(false);
        athlete.Name = newName;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return athlete.Id;
    }

    private static async Task CorruptOneStageRowAsync(SaveStore store, Guid saveId, int leagueId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().OrderBy(e => e.SeasonNumber).FirstAsync().ConfigureAwait(false);
        StageStandingEntity row = await context.StageStandings
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .OrderBy(e => e.StageNumber).ThenBy(e => e.StageRank).FirstAsync().ConfigureAwait(false);
        HashSet<int> roster = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .Select(e => e.SaveAthleteId).ToHashSetAsync().ConfigureAwait(false);
        int outside = await context.SaveAthletes.AsNoTracking()
            .Where(e => !roster.Contains(e.Id)).Select(e => e.Id).FirstAsync().ConfigureAwait(false);
        row.SaveAthleteId = outside;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task CorruptOneFinalRowAsync(SaveStore store, Guid saveId, int leagueId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        SeasonStandingEntity row = await context.SeasonStandings
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .OrderBy(e => e.SeasonRank).FirstAsync().ConfigureAwait(false);
        HashSet<int> roster = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .Select(e => e.SaveAthleteId).ToHashSetAsync().ConfigureAwait(false);
        int outside = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId != leagueId && !roster.Contains(e.SaveAthleteId))
            .Select(e => e.SaveAthleteId).FirstAsync().ConfigureAwait(false);
        row.SaveAthleteId = outside;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertWellFormedStandingsForIncompleteStageAsync(SaveStore store, Guid saveId, int leagueId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().OrderBy(e => e.SeasonNumber).FirstAsync().ConfigureAwait(false);
        StageEntity cursor = await context.Stages
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId && e.StageNumber == 2)
            .SingleAsync().ConfigureAwait(false);
        cursor.IsComplete.ShouldBeFalse();
        List<int> roster = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId)
            .OrderBy(e => e.SaveAthleteId).Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false);
        roster.Count.ShouldBe(32);
        for (int i = 0; i < roster.Count; i++)
        {
            context.StageStandings.Add(new StageStandingEntity
            {
                SeasonId = season.Id,
                LeagueId = leagueId,
                StageId = cursor.Id,
                StageNumber = 2,
                SaveAthleteId = roster[i],
                StageRank = i + 1,
                StageScoreThousandths = 1000,
                BaseScoreThousandths = 1000,
                ChampionshipPointsThousandths = 1000,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                EarnedBonusThousandths = 0,
            });
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
