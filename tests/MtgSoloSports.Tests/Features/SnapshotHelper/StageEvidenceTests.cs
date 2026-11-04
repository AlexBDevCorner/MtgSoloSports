using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Seasons;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

public sealed class StageEvidenceTests
{
    private static readonly DateTimeOffset FixedAcquiredUtc = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Latest_SelectsFirstSuperleagueStage_WithBeforeAfter()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-latest-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, string workingDir) = await AcquireSnapshotAsync(store, saveId, configDir);
            string hashBefore = SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));
            var rngBefore = SaveSnapshot.SnapshotReader.ReadRngState(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));

            (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);

            exit.ShouldBe(0, stderr);
            SaveSnapshot.StageEvidenceDocument evidence = ParseEvidence(stdout);
            evidence.Outcome.ShouldBe("completed");
            evidence.Target.SeasonNumber.ShouldBe(2);
            evidence.Target.StageNumber.ShouldBe(1);
            evidence.Target.IsFirstStage.ShouldBeTrue();
            evidence.Target.IsFinalStage.ShouldBeFalse();
            evidence.Target.LeagueKind.ShouldBe("Superleague");
            evidence.StageTable.Count.ShouldBe(32);
            evidence.Rounds.Count.ShouldBe(16);
            evidence.RoundWinners.Count.ShouldBe(16);
            evidence.ChampionshipBefore.Available.ShouldBeFalse();
            evidence.ChampionshipAfter.Available.ShouldBeTrue();
            evidence.ChampionshipAfter.CompletedStages.ShouldBe(1);
            evidence.ChampionshipAfter.Standings.Count.ShouldBe(32);
            evidence.History.StageWinners.Count.ShouldBe(1);
            evidence.History.PriorChampions.ShouldBeEmpty();
            evidence.Limitations.ShouldContain(l => l.Contains("stage one", StringComparison.OrdinalIgnoreCase));

            // Every awarded championship equals after-minus-before (before is zero for stage one).
            foreach (SaveSnapshot.ChampionshipRow row in evidence.ChampionshipAfter.Standings)
            {
                row.PointsAwarded.ShouldBe(row.TotalChampionshipPointsThousandths);
            }

            // Stage winners and round winners are traceable to persisted rows.
            evidence.Target.StageChecksum.ShouldNotBeNullOrWhiteSpace();
            evidence.Rounds.ShouldAllBe(r => r.Placements.Count == 32);

            // Read-only: snapshot bytes and RNG unchanged.
            SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
            SaveSnapshot.SnapshotReader.ReadRngState(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(rngBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task ExplicitSeasonStage_MatchesLatest_AndPartialIsRejected()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-explicit-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            // Complete the global stage for the remaining leagues so Superleague stage 2 becomes legal,
            // then start it with a single round: partial progress, not a completed stage.
            await new CompleteStageForAllLeaguesHandler(store).HandleAsync(saveId);
            await new AdvanceRoundHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int latestExit, string latestStdout, string latestStderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);
            latestExit.ShouldBe(0, latestStderr);
            SaveSnapshot.StageEvidenceDocument latest = ParseEvidence(latestStdout);
            latest.Target.StageNumber.ShouldBe(1);

            (int explicitExit, string explicitStdout, string explicitStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1"], handler: null);
            explicitExit.ShouldBe(0, explicitStderr);
            SaveSnapshot.StageEvidenceDocument explicitEvidence = ParseEvidence(explicitStdout);
            explicitEvidence.Target.StageNumber.ShouldBe(1);
            explicitEvidence.Target.StageChecksum.ShouldBe(latest.Target.StageChecksum);

            (int partialExit, _, string partialStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "2"], handler: null);
            partialExit.ShouldBe(1);
            partialStderr.ShouldContain("partial-stage");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task NoCompletedSuperleague_ReturnsClearOutcome()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-none-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);

            exit.ShouldBe(1);
            stderr.ShouldContain("No completed Superleague stage");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task FinalStage_UsesPersistedOfficialTable()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason2CompleteAsync("mtgsolosports-evidence-final-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);

            exit.ShouldBe(0, stderr);
            SaveSnapshot.StageEvidenceDocument evidence = ParseEvidence(stdout);
            evidence.Target.SeasonNumber.ShouldBe(2);
            evidence.Target.StageNumber.ShouldBe(32);
            evidence.Target.IsFinalStage.ShouldBeTrue();
            evidence.ChampionshipAfter.IsFinal.ShouldBeTrue();
            evidence.ChampionshipAfter.CompletedStages.ShouldBe(32);
            evidence.ChampionshipAfter.Standings.Count.ShouldBe(32);

            // After-final matches persisted SeasonStandings order and totals.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                int seasonId = await context.Seasons.Where(e => e.SeasonNumber == 2).Select(e => e.Id).SingleAsync();
                int leagueId = await context.Leagues.Where(e => e.SeasonId == seasonId && e.Kind == (int)LeagueKind.Superleague).Select(e => e.Id).SingleAsync();
                var persisted = await context.SeasonStandings
                    .Where(e => e.SeasonId == seasonId && e.LeagueId == leagueId)
                    .OrderBy(e => e.SeasonRank)
                    .ToListAsync();
                persisted.Count.ShouldBe(32);
                for (int i = 0; i < persisted.Count; i++)
                {
                    evidence.ChampionshipAfter.Standings[i].AthleteId.ShouldBe(persisted[i].SaveAthleteId);
                    evidence.ChampionshipAfter.Standings[i].TotalChampionshipPointsThousandths.ShouldBe(persisted[i].TotalChampionshipPointsThousandths);
                }
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task ProvisionalRanking_FlagsExactDeterministicTies()
    {
        // Pure tie-break coverage: same championship, same vectors and raw totals tie;
        // different stage-place vectors break the tie without RNG.
        var tied = new SeasonAthleteTotals(1, "Alpha", 77000, 100000, 90000, [1, 0], [2, 0]);
        var tiedOther = new SeasonAthleteTotals(2, "Beta", 77000, 100000, 90000, [1, 0], [2, 0]);
        var ahead = new SeasonAthleteTotals(3, "Gamma", 77000, 100000, 90000, [0, 1], [2, 0]);

        List<SaveSnapshot.StageEvidenceBuilder.ChampionshipEntry> ranked =
            SaveSnapshot.StageEvidenceBuilder.RankProvisional([tied, tiedOther, ahead], leagueSize: 3);

        ranked.Count.ShouldBe(3);
        // Gamma has fewer wins (stage-place vector decides) so ranks behind despite same points.
        ranked[2].AthleteId.ShouldBe(3);
        // Alpha and Beta are exactly tied deterministically.
        ranked[0].DeterministicTie.ShouldBeTrue();
        ranked[1].DeterministicTie.ShouldBeTrue();
        ranked[0].TieGroupSize.ShouldBe(2);
    }

    [Fact]
    public async Task SupportedEncodings_PlainPayloadsStayReadable()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-enc-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);

            // Rewrite one persisted round as plain JSON (same document, different storage encoding).
            int seasonId = await SeasonIdAsync(store, saveId, seasonNumber: 2);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                var round = await context.Rounds
                    .Where(e => e.SeasonId == seasonId && e.LeagueId == superleagueId && e.StageNumber == 1 && e.RoundNumber == 1)
                    .SingleAsync();
                string plain = RoundPayloadCodec.DecodeToJson(round.PayloadJson);
                plain.ShouldNotStartWith(RoundPayloadCodec.BrotliPrefix);
                round.PayloadJson = plain;
                await context.SaveChangesAsync();
            }

            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);

            exit.ShouldBe(0, stderr);
            SaveSnapshot.StageEvidenceDocument evidence = ParseEvidence(stdout);
            evidence.Coverage.PayloadEncodings.Any(e => string.Equals(e, "plain", StringComparison.Ordinal)).ShouldBeTrue();
            evidence.Coverage.PayloadEncodings.Any(e => string.Equals(e, "br1", StringComparison.Ordinal)).ShouldBeTrue();
            evidence.Rounds.Count.ShouldBe(16);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task CorruptPayload_FailsVisiblyWithoutInventingResults()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-corrupt-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);

            // Corrupt one persisted round before export so the bundle stays consistent
            // but the round payload cannot be replayed.
            int seasonId = await SeasonIdAsync(store, saveId, seasonNumber: 2);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                var round = await context.Rounds
                    .Where(e => e.SeasonId == seasonId && e.LeagueId == superleagueId && e.StageNumber == 1 && e.RoundNumber == 1)
                    .SingleAsync();
                round.PayloadJson = "{corrupt";
                await context.SaveChangesAsync();
            }

            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);

            exit.ShouldBe(1);
            stderr.ShouldContain("corrupt");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task HistoricalCutoff_LaterStagesDoNotChangeEarlierEvidence()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-cutoff-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int firstExit, string firstStdout, string firstStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1"], handler: null);
            firstExit.ShouldBe(0, firstStderr);
            string firstCanonical = CanonicalEvidence(firstStdout);

            // Later sporting history: complete the global stage so Superleague stage 2 completes.
            await new CompleteStageForAllLeaguesHandler(store).HandleAsync(saveId);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string reConfigPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);
            reConfigPath.ShouldBe(configPath);

            (int secondExit, string secondStdout, string secondStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1"], handler: null);
            secondExit.ShouldBe(0, secondStderr);
            string secondCanonical = CanonicalEvidence(secondStdout);

            secondCanonical.ShouldBe(firstCanonical);

            // The latest cutoff did advance, proving the earlier request was not silently switched.
            (int latestExit, string latestStdout, string latestStderr) = await SnapshotTestSupport.RunCliAsync(["evidence", "--config", configPath], handler: null);
            latestExit.ShouldBe(0, latestStderr);
            ParseEvidence(latestStdout).Target.StageNumber.ShouldBe(2);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task AthleteFollowup_RespectsCutoff_AndStaysBounded()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-evidence-athlete-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-evidence-cfg-");
        try
        {
            int superleagueId = await SuperleagueIdAsync(store, saveId, seasonNumber: 2);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            (string configPath, _) = await AcquireSnapshotAsync(store, saveId, configDir);

            (int baseExit, string baseStdout, string baseStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1"], handler: null);
            baseExit.ShouldBe(0, baseStderr);
            string winner = ParseEvidence(baseStdout).RoundWinners[0].Name;

            (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1", "--athlete", winner], handler: null);

            exit.ShouldBe(0, stderr);
            SaveSnapshot.StageEvidenceDocument evidence = ParseEvidence(stdout);
            evidence.AthleteFocus.ShouldNotBeNull();
            evidence.AthleteFocus!.Name.ShouldBe(winner);
            evidence.AthleteFocus.RoundPlacements.Count.ShouldBe(16);
            evidence.AthleteFocus.RecentForm.Count.ShouldBeLessThanOrEqualTo(5);
            evidence.AthleteFocus.PriorTitles.ShouldBe(0);

            // Later history does not leak into the earlier athlete cutoff.
            await new CompleteStageForAllLeaguesHandler(store).HandleAsync(saveId);
            await new CompleteStageHandler(store).HandleAsync(saveId, superleagueId);
            await AcquireSnapshotAsync(store, saveId, configDir);
            (int laterExit, string laterStdout, string laterStderr) = await SnapshotTestSupport.RunCliAsync(
                ["evidence", "--config", configPath, "--season", "2", "--stage", "1", "--athlete", winner], handler: null);
            laterExit.ShouldBe(0, laterStderr);
            SaveSnapshot.StageEvidenceDocument later = ParseEvidence(laterStdout);
            later.AthleteFocus!.RecentForm.Count.ShouldBe(evidence.AthleteFocus.RecentForm.Count);
            later.AthleteFocus.PriorStageWins.ShouldBe(evidence.AthleteFocus.PriorStageWins);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    private static SaveSnapshot.StageEvidenceDocument ParseEvidence(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("outcome").GetString().ShouldBe("completed");
        document.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        return JsonSerializer.Deserialize<SaveSnapshot.StageEvidenceDocument>(
            stdout,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    private static string CanonicalEvidence(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement root = document.RootElement;
        // Source snapshot metadata legitimately changes as the game advances;
        // sporting evidence must not.
        Dictionary<string, JsonElement> copy = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        copy.Remove("source");
        copy.Remove("generatedUtc");
        copy.Remove("elapsedMs");
        return JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = false });
    }

    private static async Task<(string ConfigPath, string WorkingDir)> AcquireSnapshotAsync(SaveStore store, Guid saveId, string configDir)
    {
        string configPath = SnapshotTestSupport.WriteConfig(configDir, saveId);
        SaveStore.ExportRecord exported = await store.ExportAsync(saveId).ConfigureAwait(false);
        string workingDir = Path.Combine(configDir, "snapshot-data");
        await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
            exported.ZipBytes, saveId, workingDir, FixedAcquiredUtc).ConfigureAwait(false);
        return (configPath, workingDir);
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

    private static async Task<int> SeasonIdAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Seasons.Where(e => e.SeasonNumber == seasonNumber).Select(e => e.Id).SingleAsync().ConfigureAwait(false);
    }
}
