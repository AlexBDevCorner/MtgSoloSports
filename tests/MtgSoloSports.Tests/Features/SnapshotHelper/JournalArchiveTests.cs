using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

public sealed class JournalArchiveTests
{
    private static readonly DateTimeOffset FixedAcquiredUtc = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TrackedExample_WithArchiveDirectory_Loads()
    {
        string examplePath = FindExamplePath();
        string example = File.ReadAllText(examplePath);
        example.ShouldContain("archiveDirectory");
        Guid saveId = Guid.NewGuid();
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-config-");
        try
        {
            string configPath = Path.Combine(configDir, "snapshot.config.json");
            File.WriteAllText(configPath, example.Replace(
                "PASTE-EXACT-SAVE-ID-HERE",
                saveId.ToString("D"),
                StringComparison.Ordinal));
            SaveSnapshot.SnapshotConfig config = SaveSnapshot.SnapshotConfig.Load(configPath);
            config.SaveId.ShouldBe(saveId);
            config.ArchiveDirectory.ShouldNotBeNullOrWhiteSpace();
            config.ArchiveDirectory.ShouldEndWith("journalist-archive");
        }
        finally
        {
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Archive_Init_CreatesSaveScopedLayout_AndIsIdempotent()
    {
        Guid saveId = Guid.NewGuid();
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-init-");
        try
        {
            string configPath = SnapshotTestSupport.WriteConfig(configDir, saveId);
            (int exit, string stdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            exit.ShouldBe(0);
            using JsonDocument document = JsonDocument.Parse(stdout);
            document.RootElement.GetProperty("saveId").GetGuid().ShouldBe(saveId);
            string archiveRoot = document.RootElement.GetProperty("archiveRoot").GetString()!;
            string saveDir = Path.Combine(archiveRoot, $"save-{saveId:D}");
            File.Exists(Path.Combine(saveDir, "save.json")).ShouldBeTrue();
            File.Exists(Path.Combine(saveDir, "index.json")).ShouldBeTrue();

            (int secondExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            secondExit.ShouldBe(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Commit_RejectsMissingArchive_AndTargetMismatch()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-mismatch-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            string articlePath = WriteArticle(configDir, "review.md", "# Review\n\nFacts first.\n");

            (int missingExit, _, string missingStderr) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", articlePath, "--evidence", evidencePath],
                handler: null);
            missingExit.ShouldBe(1);
            missingStderr.ShouldContain("archive init");

            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);

            (int mismatchExit, _, string mismatchStderr) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "2",
                 "--article", articlePath, "--evidence", evidencePath],
                handler: null);
            mismatchExit.ShouldBe(1);
            mismatchStderr.ShouldContain("does not match the requested season");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Commit_RejectsForeignSaveEvidence()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-foreign-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            string tampered = TamperEvidenceSave(evidencePath, configDir, "tampered.json", Guid.NewGuid());
            string articlePath = WriteArticle(configDir, "review.md", "# Review\n\nFacts.\n");
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", articlePath, "--evidence", tampered],
                handler: null);
            exit.ShouldBe(1);
            stderr.ShouldContain("does not match the configured save");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Commit_RepeatRequiresExplicitRevision_WithRecovery()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-repeat-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);

            string firstArticle = WriteArticle(configDir, "rev1.md", "# Season 2 Stage 1\n\nFirst text.\n");
            (int firstExit, string firstStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", firstArticle, "--evidence", evidencePath, "--note", "First."],
                handler: null);
            firstExit.ShouldBe(0);
            using JsonDocument firstMeta = JsonDocument.Parse(firstStdout);
            firstMeta.RootElement.GetProperty("revision").GetInt32().ShouldBe(1);

            string secondArticle = WriteArticle(configDir, "rev2.md", "# Season 2 Stage 1\n\nSecond text.\n");
            (int repeatExit, _, string repeatStderr) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", secondArticle, "--evidence", evidencePath],
                handler: null);
            repeatExit.ShouldBe(1);
            repeatStderr.ShouldContain("'--revision'");

            (int revExit, string revStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", secondArticle, "--evidence", evidencePath, "--revision", "--note", "Revised."],
                handler: null);
            revExit.ShouldBe(0);
            using JsonDocument revMeta = JsonDocument.Parse(revStdout);
            revMeta.RootElement.GetProperty("revision").GetInt32().ShouldBe(2);

            string archiveRoot = ArchiveRootFor(configPath);
            string seasonDir = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02");
            string backup = Path.Combine(seasonDir, "stage-01.r1.md");
            File.Exists(backup).ShouldBeTrue();
            (await File.ReadAllTextAsync(backup)).ShouldContain("First text.");
            (await File.ReadAllTextAsync(Path.Combine(seasonDir, "stage-01.md"))).ShouldContain("Second text.");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Commit_PreservesManualEdits_InNextContext()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-edits-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);

            string article = WriteArticle(configDir, "review.md", "# Season 2 Stage 1\n\nCommitted text.\n");
            (int commitExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", article, "--evidence", evidencePath],
                handler: null);
            commitExit.ShouldBe(0);

            string archiveRoot = ArchiveRootFor(configPath);
            string stored = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02", "stage-01.md");
            await File.AppendAllTextAsync(stored, "\nHuman edit: keep this line.\n");

            (int contextExit, string contextStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "context", "--config", configPath], handler: null);
            contextExit.ShouldBe(0);
            contextStdout.ShouldContain("Human edit: keep this line.");

            string revised = WriteArticle(configDir, "rev2.md", "# Season 2 Stage 1\n\nRevised text.\n");
            (int revExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", revised, "--evidence", evidencePath, "--revision"],
                handler: null);
            revExit.ShouldBe(0);
            string backup = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02", "stage-01.r1.md");
            (await File.ReadAllTextAsync(backup)).ShouldContain("Human edit: keep this line.");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task SaveIsolation_SameNameStaysSeparate()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-isolation-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        string otherDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-other-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            string sharedArchive = Path.Combine(configDir, "shared-archive");
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath, "--archive", sharedArchive], handler: null);
            initExit.ShouldBe(0);

            string article = WriteArticle(configDir, "review.md", "# Season 2 Stage 1\n\nReal review.\n");
            (int commitExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", article, "--evidence", evidencePath, "--archive", sharedArchive],
                handler: null);
            commitExit.ShouldBe(0);

            var (otherStore, otherRoot) = TestSaveStores.CreateStore("mtgsolosports-archive-other-save-");
            Guid otherSaveId = await CreateOtherSaveAsync(otherStore);
            otherSaveId.ShouldNotBe(saveId);
            string otherConfig = SnapshotTestSupport.WriteConfig(otherDir, otherSaveId, "./journalist-archive");

            (int otherInitExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", otherConfig, "--archive", sharedArchive], handler: null);
            otherInitExit.ShouldBe(0);

            (int contextExit, string contextStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "context", "--config", otherConfig, "--archive", sharedArchive], handler: null);
            contextExit.ShouldBe(0);
            using JsonDocument context = JsonDocument.Parse(contextStdout);
            context.RootElement.GetProperty("recent").GetArrayLength().ShouldBe(0);
            contextStdout.ShouldNotContain("Real review.");
            Directory.Exists(Path.Combine(sharedArchive, $"save-{saveId:D}")).ShouldBeTrue();
            Directory.Exists(Path.Combine(sharedArchive, $"save-{otherSaveId:D}")).ShouldBeTrue();
            TestSaveStores.DeleteRoot(otherRoot);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
            TestSaveStores.DeleteRoot(otherDir);
        }
    }

    [Fact]
    public async Task Context_Historical_ExcludesLaterOutcomes()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-future-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string firstEvidence = await EvidenceToFileAsync(configPath, configDir, "stage1.json", season: 2, stage: 1);
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);
            string firstArticle = WriteArticle(configDir, "s1.md", "# Season 2 Stage 1\n\nOpener.\n");
            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", firstArticle, "--evidence", firstEvidence, "--note", "Opener note."],
                handler: null);
            firstExit.ShouldBe(0);

            await new CompleteStageForAllLeaguesHandler(store).HandleAsync(saveId);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string reConfigPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            reConfigPath.ShouldBe(configPath);
            string secondEvidence = await EvidenceToFileAsync(configPath, configDir, "stage2.json", season: 2, stage: 2);
            string secondArticle = WriteArticle(configDir, "s2.md", "# Season 2 Stage 2\n\nLater winner takes it.\n");
            (int secondExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "2",
                 "--article", secondArticle, "--evidence", secondEvidence, "--note", "Later note."],
                handler: null);
            secondExit.ShouldBe(0);

            AppendUnscopedNotebookSection(configPath, saveId, "Unscoped future summary: later winner takes it.");
            (int contextExit, string contextStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "context", "--config", configPath, "--season", "2", "--stage", "1"], handler: null);
            contextExit.ShouldBe(0);
            using JsonDocument context = JsonDocument.Parse(contextStdout);
            context.RootElement.GetProperty("excludedLaterArticles").GetInt32().ShouldBe(1);
            context.RootElement.GetProperty("excludedUnscopedNotebookSections").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
            string recentText = context.RootElement.GetProperty("recent").GetRawText();
            recentText.ShouldContain("Opener.");
            recentText.ShouldNotContain("Later winner takes it.");
            string excerpt = context.RootElement.GetProperty("notebookExcerpt").GetString()!;
            excerpt.ShouldContain("Season 2 Stage 1");
            excerpt.ShouldNotContain("Season 2 Stage 2");
            excerpt.ShouldNotContain("Unscoped future summary");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Commit_ConflictingEvidence_RequiresRevision_AndFlagsConflict()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-conflict-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string evidencePath = await EvidenceToFileAsync(configPath, configDir, "stage.json", season: 2, stage: 1);
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);
            string article = WriteArticle(configDir, "review.md", "# Season 2 Stage 1\n\nOriginal.\n");
            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", article, "--evidence", evidencePath],
                handler: null);
            firstExit.ShouldBe(0);

            string replayed = TamperEvidenceFingerprints(evidencePath, configDir, "replayed.json");
            string revisedArticle = WriteArticle(configDir, "rev2.md", "# Season 2 Stage 1\n\nReplayed timeline.\n");
            (int plainExit, _, string plainStderr) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", revisedArticle, "--evidence", replayed],
                handler: null);
            plainExit.ShouldBe(1);
            plainStderr.ShouldContain("'--revision'");

            (int revExit, string revStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", revisedArticle, "--evidence", replayed, "--revision"],
                handler: null);
            revExit.ShouldBe(0);
            using JsonDocument meta = JsonDocument.Parse(revStdout);
            meta.RootElement.GetProperty("conflict").GetBoolean().ShouldBeTrue();
            meta.RootElement.GetProperty("revision").GetInt32().ShouldBe(2);

            string archiveRoot = ArchiveRootFor(configPath);
            string seasonDir = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02");
            (await File.ReadAllTextAsync(Path.Combine(seasonDir, "stage-01.r1.md"))).ShouldContain("Original.");
            (await File.ReadAllTextAsync(Path.Combine(seasonDir, "stage-01.md"))).ShouldContain("Replayed timeline.");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Rehearsal_TwoStages_Revision_AndHistorical()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-archive-rehearsal-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-archive-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            string firstEvidence = await EvidenceToFileAsync(configPath, configDir, "stage1.json", season: 2, stage: 1);
            (int initExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "init", "--config", configPath], handler: null);
            initExit.ShouldBe(0);
            string firstArticle = WriteArticle(configDir, "s1.md", "# Season 2 Stage 1\n\nOpener review.\n");
            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
                 "--article", firstArticle, "--evidence", firstEvidence, "--note", "Opener continuity."],
                handler: null);
            firstExit.ShouldBe(0);

            string archiveRoot = ArchiveRootFor(configPath);
            string storedFirst = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02", "stage-01.md");
            await File.AppendAllTextAsync(storedFirst, "\nHuman edit after first review.\n");

            await new CompleteStageForAllLeaguesHandler(store).HandleAsync(saveId);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            await AcquireSnapshotAsync(store, saveId, configDir);
            string secondEvidence = await EvidenceToFileAsync(configPath, configDir, "stage2.json", season: 2, stage: 2);

            (int latestExit, string latestStdout, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "context", "--config", configPath], handler: null);
            latestExit.ShouldBe(0);
            latestStdout.ShouldContain("Human edit after first review.");

            string secondArticle = WriteArticle(configDir, "s2.md", "# Season 2 Stage 2\n\nSecond review.\n");
            (int secondExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "2",
                 "--article", secondArticle, "--evidence", secondEvidence, "--note", "Second continuity."],
                handler: null);
            secondExit.ShouldBe(0);

            await ReviseFirstAndCheckHistoricalAsync(configDir, configPath, archiveRoot, saveId, firstEvidence);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    private static async Task ReviseFirstAndCheckHistoricalAsync(
        string configDir, string configPath, string archiveRoot, Guid saveId, string firstEvidence)
    {
        string revisedFirst = WriteArticle(configDir, "s1-rev.md", "# Season 2 Stage 1\n\nOpener revised.\n");
        (int revExit, _, _) = await SnapshotTestSupport.RunCliAsync(
            ["archive", "commit", "--config", configPath, "--season", "2", "--stage", "1",
             "--article", revisedFirst, "--evidence", firstEvidence, "--revision"],
            handler: null).ConfigureAwait(false);
        revExit.ShouldBe(0);
        (await File.ReadAllTextAsync(Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02", "stage-01.r1.md")).ConfigureAwait(false))
            .ShouldContain("Human edit after first review.");

        (int histExit, string histStdout, _) = await SnapshotTestSupport.RunCliAsync(
            ["archive", "context", "--config", configPath, "--season", "2", "--stage", "1"], handler: null).ConfigureAwait(false);
        histExit.ShouldBe(0);
        using JsonDocument historical = JsonDocument.Parse(histStdout);
        historical.RootElement.GetProperty("recent").GetArrayLength().ShouldBe(1);
        historical.RootElement.GetProperty("recent")[0].GetProperty("stageNumber").GetInt32().ShouldBe(1);
        historical.RootElement.GetProperty("excludedLaterArticles").GetInt32().ShouldBe(1);
        string notebookExcerpt = historical.RootElement.GetProperty("notebookExcerpt").GetString()!;
        notebookExcerpt.ShouldContain("Season 2 Stage 1");
        notebookExcerpt.ShouldNotContain("Season 2 Stage 2");
        histStdout.ShouldNotContain("Second review.");
    }

    private static async Task<(string ConfigPath, string WorkingDir)> AcquireSnapshotAsync(
        SaveStore store, Guid saveId, string configDir)
    {
        string configPath = SnapshotTestSupport.WriteConfig(configDir, saveId);
        SaveStore.ExportRecord exported = await store.ExportAsync(saveId).ConfigureAwait(false);
        string workingDir = Path.Combine(configDir, "snapshot-data");
        await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
            exported.ZipBytes, saveId, workingDir, FixedAcquiredUtc).ConfigureAwait(false);
        return (configPath, workingDir);
    }

    private static async Task<string> EvidenceToFileAsync(
        string configPath, string configDir, string fileName, int season, int stage)
    {
        (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(
            ["evidence", "--config", configPath, "--season", season.ToString(CultureInfo.InvariantCulture), "--stage", stage.ToString(CultureInfo.InvariantCulture)],
            handler: null).ConfigureAwait(false);
        exit.ShouldBe(0, stderr);
        string path = Path.Combine(configDir, fileName);
        await File.WriteAllTextAsync(path, stdout).ConfigureAwait(false);
        return path;
    }

    private static string WriteArticle(string directory, string fileName, string content)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ArchiveRootFor(string configPath)
    {
        SaveSnapshot.SnapshotConfig config = SaveSnapshot.SnapshotConfig.Load(configPath);
        return SaveSnapshot.JournalArchive.ResolveArchiveRoot(config, null);
    }

    private static string TamperEvidenceSave(string evidencePath, string directory, string fileName, Guid otherSaveId)
    {
        string json = File.ReadAllText(evidencePath);
        using JsonDocument parsed = JsonDocument.Parse(json);
        string tampered = json.Replace(
            parsed.RootElement.GetProperty("source").GetProperty("saveId").GetString()!,
            otherSaveId.ToString("D"),
            StringComparison.Ordinal);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, tampered);
        return path;
    }

    private static string TamperEvidenceFingerprints(string evidencePath, string directory, string fileName)
    {
        string json = File.ReadAllText(evidencePath);
        using JsonDocument parsed = JsonDocument.Parse(json);
        string stageChecksum = parsed.RootElement.GetProperty("target").GetProperty("stageChecksum").GetString()!;
        string databaseSha256 = parsed.RootElement.GetProperty("source").GetProperty("databaseSha256").GetString()!;
        string tampered = json
            .Replace(stageChecksum, new string('a', stageChecksum.Length), StringComparison.Ordinal)
            .Replace(databaseSha256, new string('b', databaseSha256.Length), StringComparison.Ordinal);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, tampered);
        return path;
    }

    private static void AppendUnscopedNotebookSection(string configPath, Guid saveId, string text)
    {
        string archiveRoot = ArchiveRootFor(configPath);
        string notebook = Path.Combine(archiveRoot, $"save-{saveId:D}", "season-02", "notebook.md");
        File.AppendAllText(notebook, "\n## Unscoped notes\n\n" + text + "\n");
    }

    private static async Task<Guid> CreateOtherSaveAsync(SaveStore otherStore)
    {
        SaveStore.CreationRecord otherCreated = await otherStore.CreateAsync(
            "Shared Season1", 4242UL, 777UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        return otherCreated.Detail.SaveId;
    }

    private static async Task<int> SuperleagueIdAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int seasonId = await context.Seasons.Where(e => e.SeasonNumber == seasonNumber).Select(e => e.Id).SingleAsync().ConfigureAwait(false);
        return await context.Leagues
            .Where(e => e.SeasonId == seasonId && e.Kind == (int)LeagueKind.Superleague)
            .Select(e => e.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private static string FindExamplePath()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "tools", "SaveSnapshot", "snapshot.config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Tracked snapshot.config.example.json was not found.");
    }
}
