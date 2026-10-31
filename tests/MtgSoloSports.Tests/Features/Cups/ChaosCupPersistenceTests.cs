using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-099: compact save-scoped Chaos persistence with resumable partial groups.
/// Round-trips partial and complete test editions with exact round payloads,
/// keeps historic saves without Chaos editions valid, and rejects duplicate
/// editions, groups, ranks and advancement.
/// </summary>
public sealed class ChaosCupPersistenceTests
{
    [Fact]
    public async Task PartialEdition_RoundTripsExactRoundData()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-partial-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9001UL, 9002UL);
            await PersistPartialEditionAsync(store, saveId, setup);
            await VerifyPartialEditionAsync(store, saveId, setup);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompleteStage_RoundTripsAllGroups_WithAdvancementLinkage()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-complete-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9101UL, 9102UL);
            await PersistCompleteStageZeroAsync(store, saveId, setup);
            await VerifyCompleteStageZeroAsync(store, saveId, setup);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FinalStage_PersistsOfficialPlaces_WithoutAdvancement()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-final-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9201UL, 9202UL);
            await PersistFinalStageAsync(store, saveId, setup);
            await VerifyFinalStageAsync(store, saveId, setup);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DuplicateEdition_IsRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-dup-ed-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9301UL, 9302UL);
            await PersistPartialEditionAsync(store, saveId, setup);
            using SaveDbContext context = store.OpenDbContext(saveId);
            ChaosField field = await LoadFieldAsync(context, setup.Source.Id);
            context.ChaosCupEditions.Add(DuplicateEdition(setup, field));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DuplicateEntrant_IsRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-dup-en-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9311UL, 9312UL);
            await PersistPartialEditionAsync(store, saveId, setup);
            using SaveDbContext context = store.OpenDbContext(saveId);
            ChaosField field = await LoadFieldAsync(context, setup.Source.Id);
            ChaosCupEntrantEntity first = field.Entrants[0];
            context.ChaosCupEntrants.Add(DuplicateEntrant(first));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DuplicateRound_IsRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-dup-rd-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9321UL, 9322UL);
            await PersistPartialEditionAsync(store, saveId, setup);
            using SaveDbContext context = store.OpenDbContext(saveId);
            ChaosField field = await LoadFieldAsync(context, setup.Source.Id);
            ChaosCupRoundEntity round = field.Rounds.First(r => r.StageIndex == 0 && r.GroupNumber == 1);
            context.ChaosCupRounds.Add(DuplicateRound(round));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DuplicateRank_AndGap_AreRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-dup-rank-");
        try
        {
            (Guid saveId, ChaosSetup setup) = await PrepareFieldAsync(store, 9331UL, 9332UL);
            await PersistPartialEditionAsync(store, saveId, setup);
            using SaveDbContext context = store.OpenDbContext(saveId);
            ChaosField field = await LoadFieldAsync(context, setup.Source.Id);
            AssertDuplicateRankRejected(setup, field);
            AssertRoundGapRejected(field);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExistingSaves_MigrateWithoutPastResultChanges()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-chaos-migrate-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Chaos Compat", 9401UL, 9402UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int stagesBefore;
            int roundsBefore;
            int standingsBefore;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                stagesBefore = await context.Stages.CountAsync();
                roundsBefore = await context.Rounds.CountAsync();
                standingsBefore = await context.StageStandings.CountAsync();
                (await context.ChaosCupEditions.CountAsync()).ShouldBe(0);
            }

            await store.EnsureMigratedAsync(saveId);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                (await context.Stages.CountAsync()).ShouldBe(stagesBefore);
                (await context.Rounds.CountAsync()).ShouldBe(roundsBefore);
                (await context.StageStandings.CountAsync()).ShouldBe(standingsBefore);
                (await context.ChaosCupEditions.CountAsync()).ShouldBe(0);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task PersistPartialEditionAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await PersistEditionAsync(context, setup, stageCount: 1).ConfigureAwait(false);
        await PersistPartialRoundsAsync(context, setup, groupOneRounds: 3, groupTwoRounds: 1).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task VerifyPartialEditionAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        ChaosField field = await LoadFieldAsync(context, setup.Source.Id).ConfigureAwait(false);
        field.Entrants.Count.ShouldBe(ChaosCupFormat.FieldSize);
        field.Draws.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(0));
        List<ChaosCupRoundEntity> groupOne = field.Rounds
            .Where(r => r.StageIndex == 0 && r.GroupNumber == 1)
            .OrderBy(r => r.RoundNumber)
            .ToList();
        groupOne.Count.ShouldBe(3);
        ChaosCupInvariants.ValidateGroupRoundsContiguous(groupOne, 0, 1);
        foreach (ChaosCupRoundEntity row in groupOne)
        {
            ChaosCupInvariants.ValidatePersistedRound(row, setup.Source, field.Edition, setup.Rules);
        }

        ChaosCupRoundEntity first = groupOne[0];
        ChaosCupRoundPayloadDocument decoded = ChaosCupRoundPayloadDocument.FromStored(first.PayloadJson);
        decoded.Placements.Count.ShouldBe(ChaosCupFormat.GroupSize);
        decoded.StageIndex.ShouldBe(0);
        decoded.GroupNumber.ShouldBe(1);
        decoded.RoundNumber.ShouldBe(1);
        ChaosCupRoundPayloadDocument.FromStored(decoded.ToStored()).Placements
            .Select(p => p.AthleteId).ShouldBe(decoded.Placements.Select(p => p.AthleteId).ToList());
        field.Standings.Count.ShouldBe(0);
        ChaosCupInvariants.ValidateStageDraw(setup.Source, field.Edition, field.Entrants, field.Draws.Where(d => d.StageIndex == 0).ToList(), 0);
        ChaosCupInvariants.ValidateStageStandings(
            setup.Source,
            field.Edition,
            field.Draws.Where(d => d.StageIndex == 0).ToList(),
            field.Rounds.Where(r => r.StageIndex == 0).ToList(),
            field.Standings.Where(s => s.StageIndex == 0).ToList(),
            0,
            setup.Rules);
    }

    private static async Task PersistCompleteStageZeroAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await PersistEditionAsync(context, setup, stageCount: 1).ConfigureAwait(false);
        await PersistCompleteStageAsync(context, setup, stageIndex: 0).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task VerifyCompleteStageZeroAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        ChaosField field = await LoadFieldAsync(context, setup.Source.Id).ConfigureAwait(false);
        List<ChaosCupDrawEntity> stage0 = field.Draws.Where(d => d.StageIndex == 0).ToList();
        List<ChaosCupRoundEntity> rounds0 = field.Rounds.Where(r => r.StageIndex == 0).ToList();
        List<ChaosCupGroupStandingEntity> standings0 = field.Standings.Where(s => s.StageIndex == 0).ToList();
        rounds0.Count.ShouldBe(32 * ChaosCupFormat.RoundsPerGroup);
        standings0.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(0));
        ChaosCupInvariants.ValidateStageStandings(setup.Source, field.Edition, stage0, rounds0, standings0, 0, setup.Rules);
        List<ChaosCupGroupStandingEntity> tampered = standings0.Select(s => new ChaosCupGroupStandingEntity
        {
            SourceSeasonId = s.SourceSeasonId,
            SourceSeasonNumber = s.SourceSeasonNumber,
            StageIndex = s.StageIndex,
            GroupNumber = s.GroupNumber,
            SaveAthleteId = s.SaveAthleteId,
            GroupRank = s.GroupRank,
            GroupScoreThousandths = 0,
            BaseScoreThousandths = 0,
            RoundWins = s.RoundWins,
            RoundPlaceCountsJson = s.RoundPlaceCountsJson,
            SportingColor = s.SportingColor,
            IsAdvanced = s.IsAdvanced,
        }).ToList();
        Should.Throw<InvalidOperationException>(() => ChaosCupInvariants.ValidateStageStandings(
            setup.Source, field.Edition, stage0, rounds0, tampered, 0, setup.Rules));
        List<ChaosCupDrawEntity> stage1 = field.Draws.Where(d => d.StageIndex == 1).ToList();
        stage1.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(1));
        ChaosCupInvariants.ValidateStageDraw(setup.Source, field.Edition, field.Entrants, stage1, 1);
        ChaosCupInvariants.ValidateAdvancementLinkage(standings0, stage1, 0);
    }

    private static async Task PersistFinalStageAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await PersistEditionAsync(context, setup, stageCount: 1, includeFinalDraw: true).ConfigureAwait(false);
        await PersistCompleteStageAsync(context, setup, stageIndex: 5).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task VerifyFinalStageAsync(SaveStore store, Guid saveId, ChaosSetup setup)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        ChaosField field = await LoadFieldAsync(context, setup.Source.Id).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> finalStandings = field.Standings.Where(s => s.StageIndex == 5).ToList();
        finalStandings.Count.ShouldBe(ChaosCupFormat.FinalFieldSize);
        finalStandings.Select(s => s.GroupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 32).ToList());
        finalStandings.All(s => !s.IsAdvanced).ShouldBeTrue();
        List<ChaosCupDrawEntity> finalDraw = field.Draws.Where(d => d.StageIndex == 5).ToList();
        ChaosCupInvariants.ValidateStageStandings(setup.Source, field.Edition, finalDraw, field.Rounds.Where(r => r.StageIndex == 5).ToList(), finalStandings, 5, setup.Rules);
    }

    private sealed record ChaosSetup(
        SeasonEntity Source,
        RulesV1 Rules,
        List<ChaosCupEntrantEntity> Entrants,
        Dictionary<int, SportingColor> ColorByAthlete);

    private sealed record ChaosField(
        ChaosCupEditionEntity Edition,
        List<ChaosCupEntrantEntity> Entrants,
        List<ChaosCupDrawEntity> Draws,
        List<ChaosCupRoundEntity> Rounds,
        List<ChaosCupGroupStandingEntity> Standings);

    private static async Task<(Guid SaveId, ChaosSetup Setup)> PrepareFieldAsync(SaveStore store, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync("Chaos Test", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        RulesV1 rules = RulesV3.CreateDefault();
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        Dictionary<int, int?> leagueByAthlete = await context.SeasonMemberships
            .Where(m => m.SeasonId == source.Id)
            .ToDictionaryAsync(m => m.SaveAthleteId, m => m.LeagueId).ConfigureAwait(false);
        Dictionary<int, (int Kind, int Division)> leagueMeta = await context.Leagues
            .ToDictionaryAsync(l => l.Id, l => (l.Kind, l.FeederDivision)).ConfigureAwait(false);
        Dictionary<int, int> colorById = await context.SaveAthletes.ToDictionaryAsync(a => a.Id, a => a.SportingColor).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = BuildEntrants(source, leagueByAthlete, leagueMeta, colorById);
        Dictionary<int, SportingColor> colorMap = entrants.ToDictionary(e => e.SaveAthleteId, e => (SportingColor)e.SportingColor);
        return (saveId, new ChaosSetup(source, rules, entrants, colorMap));
    }

    private static List<ChaosCupEntrantEntity> BuildEntrants(
        SeasonEntity source,
        Dictionary<int, int?> leagueByAthlete,
        Dictionary<int, (int Kind, int Division)> leagueMeta,
        Dictionary<int, int> colorById)
    {
        List<ChaosCupEntrantEntity> entrants = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<int> ofColor = colorById.Where(kv => kv.Value == (int)color).Select(kv => kv.Key).OrderBy(id => id).ToList();
            List<int> actives = ofColor.Where(id => leagueByAthlete.TryGetValue(id, out int? league) && league.HasValue).ToList();
            List<int> pool = ofColor.Where(id => !leagueByAthlete.TryGetValue(id, out int? league) || !league.HasValue).ToList();
            List<int> chosen = [.. actives, .. pool.Take(ChaosCupFormat.EntrantsPerColor - actives.Count)];
            chosen.Count.ShouldBe(ChaosCupFormat.EntrantsPerColor);
            foreach (int athleteId in chosen)
            {
                int? leagueId = leagueByAthlete.TryGetValue(athleteId, out int? found) ? found : null;
                int kind = 0;
                int division = 0;
                if (leagueId.HasValue && leagueMeta.TryGetValue(leagueId.Value, out var meta))
                {
                    kind = meta.Kind;
                    division = meta.Division;
                }

                entrants.Add(new ChaosCupEntrantEntity
                {
                    SourceSeasonId = source.Id,
                    SourceSeasonNumber = source.SeasonNumber,
                    SaveAthleteId = athleteId,
                    SportingColor = (int)color,
                    SourceLeagueId = leagueId,
                    SourceLeagueKind = kind,
                    SourceFeederDivision = division,
                    IsPoolInvite = leagueId is null,
                });
            }
        }

        entrants.Count.ShouldBe(ChaosCupFormat.FieldSize);
        return entrants;
    }

    private static async Task PersistEditionAsync(SaveDbContext context, ChaosSetup setup, int stageCount, bool includeFinalDraw = false)
    {
        string editionChecksum = ChaosCupInvariants.ComputeEditionChecksum(setup.Entrants);
        ChaosCupEditionEntity edition = new()
        {
            SourceSeasonId = setup.Source.Id,
            SourceSeasonNumber = setup.Source.SeasonNumber,
            RulesVersion = setup.Rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            FieldSize = ChaosCupFormat.FieldSize,
            RngBeforeState = 7001,
            RngBeforeStream = 7002,
            RngAfterState = 7003,
            RngAfterStream = 7004,
            EditionChecksum = editionChecksum,
        };
        ChaosCupInvariants.ValidateEdition(edition, setup.Rules);
        ChaosCupInvariants.ValidateEntrants(setup.Source, edition, setup.Entrants);
        context.ChaosCupEditions.Add(edition);
        foreach (ChaosCupEntrantEntity entrant in setup.Entrants)
        {
            context.ChaosCupEntrants.Add(entrant);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        List<ChaosCupEntrantEntity> persistedEntrants = await context.ChaosCupEntrants.Where(e => e.SourceSeasonId == setup.Source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> stage0 = BuildBalancedFirstDraw(setup, persistedEntrants);
        foreach (ChaosCupDrawEntity row in stage0)
        {
            context.ChaosCupDraws.Add(row);
        }

        if (stageCount > 1)
        {
            List<int> placeholder = stage0.Take(ChaosCupFormat.EntrantCountForStage(1)).Select(d => d.SaveAthleteId).ToList();
            foreach (ChaosCupDrawEntity row in BuildRandomDraw(setup, placeholder, stageIndex: 1))
            {
                context.ChaosCupDraws.Add(row);
            }
        }

        if (includeFinalDraw)
        {
            List<int> finalAthletes = persistedEntrants.Take(ChaosCupFormat.FinalFieldSize).Select(e => e.SaveAthleteId).ToList();
            foreach (ChaosCupDrawEntity row in BuildRandomDraw(setup, finalAthletes, stageIndex: 5))
            {
                context.ChaosCupDraws.Add(row);
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static List<ChaosCupDrawEntity> BuildBalancedFirstDraw(ChaosSetup setup, List<ChaosCupEntrantEntity> entrants)
    {
        Dictionary<SportingColor, List<int>> byColor = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            byColor[color] = entrants.Where(e => (SportingColor)e.SportingColor == color).Select(e => e.SaveAthleteId).OrderBy(id => id).ToList();
        }

        List<ChaosCupDrawEntity> draws = [];
        for (int group = 1; group <= ChaosCupFormat.GroupCountForStage(0); group++)
        {
            int drawIndex = 0;
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                List<int> list = byColor[color];
                for (int k = 0; k < ChaosCupFormat.FirstStagePerColorPerGroup; k++)
                {
                    draws.Add(NewDraw(setup, 0, group, list[(group - 1) * ChaosCupFormat.FirstStagePerColorPerGroup + k], drawIndex++, 10001, 10002, 10003, 10004));
                }
            }
        }

        return WithChecksum(draws, 0);
    }

    private static List<ChaosCupDrawEntity> BuildRandomDraw(ChaosSetup setup, List<int> athleteIds, int stageIndex)
    {
        int groups = ChaosCupFormat.GroupCountForStage(stageIndex);
        (long beforeState, long beforeStream, long afterState, long afterStream) = stageIndex == 5
            ? (15001L, 15002L, 15003L, 15004L)
            : (11001L, 11002L, 11003L, 11004L);
        List<ChaosCupDrawEntity> draws = [];
        List<int> ordered = athleteIds.OrderBy(id => id).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int group = (i % groups) + 1;
            draws.Add(NewDraw(setup, stageIndex, group, ordered[i], i / groups, beforeState, beforeStream, afterState, afterStream));
        }

        return WithChecksum(draws, stageIndex);
    }

    private static ChaosCupDrawEntity NewDraw(ChaosSetup setup, int stageIndex, int group, int athleteId, int drawIndex, long rngBeforeState, long rngBeforeStream, long rngAfterState, long rngAfterStream)
    {
        return new ChaosCupDrawEntity
        {
            SourceSeasonId = setup.Source.Id,
            SourceSeasonNumber = setup.Source.SeasonNumber,
            StageIndex = stageIndex,
            GroupNumber = group,
            SaveAthleteId = athleteId,
            DrawIndex = drawIndex,
            GroupSize = ChaosCupFormat.GroupSize,
            StageGroupCount = ChaosCupFormat.GroupCountForStage(stageIndex),
            RulesVersion = setup.Rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            RngBeforeState = rngBeforeState,
            RngBeforeStream = rngBeforeStream,
            RngAfterState = rngAfterState,
            RngAfterStream = rngAfterStream,
            DrawChecksum = string.Empty,
        };
    }

    private static List<ChaosCupDrawEntity> WithChecksum(List<ChaosCupDrawEntity> draws, int stageIndex)
    {
        string checksum = ChaosCupInvariants.ComputeStageDrawChecksum(stageIndex, draws);
        foreach (ChaosCupDrawEntity row in draws)
        {
            row.DrawChecksum = checksum;
        }

        return draws;
    }

    private static async Task PersistPartialRoundsAsync(SaveDbContext context, ChaosSetup setup, int groupOneRounds, int groupTwoRounds)
    {
        Dictionary<int, int> cumulative = setup.Entrants.ToDictionary(e => e.SaveAthleteId, _ => 0);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.Where(d => d.SourceSeasonId == setup.Source.Id && d.StageIndex == 0).ToListAsync().ConfigureAwait(false);
        Dictionary<int, List<int>> orderByGroup = draws.GroupBy(d => d.GroupNumber)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.DrawIndex).Select(d => d.SaveAthleteId).ToList());
        ulong rng = 20000UL;
        foreach (int round in Enumerable.Range(1, groupOneRounds))
        {
            var (row, next) = BuildRoundRow(setup, 0, 1, round, orderByGroup[1], cumulative, rng, rng + 100000UL);
            rng = next;
            context.ChaosCupRounds.Add(row);
        }

        foreach (int round in Enumerable.Range(1, groupTwoRounds))
        {
            var (row, next) = BuildRoundRow(setup, 0, 2, round, orderByGroup[2], cumulative, rng, rng + 100000UL);
            rng = next;
            context.ChaosCupRounds.Add(row);
        }
    }

    private static async Task PersistCompleteStageAsync(SaveDbContext context, ChaosSetup setup, int stageIndex)
    {
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .Where(d => d.SourceSeasonId == setup.Source.Id && d.StageIndex == stageIndex)
            .ToListAsync().ConfigureAwait(false);
        Dictionary<int, List<int>> orderByGroup = draws.GroupBy(d => d.GroupNumber)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.DrawIndex).Select(d => d.SaveAthleteId).ToList());
        Dictionary<int, int> cumulative = draws.Select(d => d.SaveAthleteId).Distinct().ToDictionary(id => id, _ => 0);
        ulong rngBase = (ulong)(30000 + stageIndex * 1000000);
        foreach (int group in orderByGroup.Keys.OrderBy(g => g))
        {
            ulong rng = rngBase + (ulong)group * 10000UL;
            for (int round = 1; round <= ChaosCupFormat.RoundsPerGroup; round++)
            {
                var (row, next) = BuildRoundRow(setup, stageIndex, group, round, orderByGroup[group], cumulative, rng, rng + 7919UL);
                rng = next;
                context.ChaosCupRounds.Add(row);
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        await PersistStandingsAsync(context, setup, orderByGroup, stageIndex).ConfigureAwait(false);
        if (stageIndex == 0)
        {
            await ReplaceStageOnePlaceholderAsync(context, setup).ConfigureAwait(false);
        }
    }

    private static async Task PersistStandingsAsync(SaveDbContext context, ChaosSetup setup, Dictionary<int, List<int>> orderByGroup, int stageIndex)
    {
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        foreach (int group in orderByGroup.Keys.OrderBy(g => g))
        {
            List<int> drawOrder = orderByGroup[group];
            Dictionary<int, int> positionByAthlete = drawOrder
                .Select((id, index) => (Id: id, Position: index + 1))
                .ToDictionary(x => x.Id, x => x.Position);
            List<int> byScore = drawOrder.OrderBy(id => positionByAthlete[id]).ToList();
            for (int rank = 1; rank <= ChaosCupFormat.GroupSize; rank++)
            {
                int athleteId = byScore[rank - 1];
                int position = positionByAthlete[athleteId];
                int basePoints = setup.Rules.ScoringTable[position - 1] * RulesV1.FixedScale;
                int score = checked(basePoints * ChaosCupFormat.RoundsPerGroup);
                int wins = position == 1 ? ChaosCupFormat.RoundsPerGroup : 0;
                context.ChaosCupGroupStandings.Add(new ChaosCupGroupStandingEntity
                {
                    SourceSeasonId = setup.Source.Id,
                    SourceSeasonNumber = setup.Source.SeasonNumber,
                    StageIndex = stageIndex,
                    GroupNumber = group,
                    SaveAthleteId = athleteId,
                    GroupRank = rank,
                    GroupScoreThousandths = score,
                    BaseScoreThousandths = score,
                    RoundWins = wins,
                    RoundPlaceCountsJson = PlaceCountsJson(position),
                    SportingColor = (int)setup.ColorByAthlete[athleteId],
                    IsAdvanced = !isFinal && rank <= ChaosCupFormat.AdvancingPerGroup,
                });
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task ReplaceStageOnePlaceholderAsync(SaveDbContext context, ChaosSetup setup)
    {
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .Where(s => s.SourceSeasonId == setup.Source.Id && s.StageIndex == 0)
            .ToListAsync().ConfigureAwait(false);
        List<int> advancers = standings.Where(s => s.IsAdvanced).Select(s => s.SaveAthleteId).OrderBy(id => id).ToList();
        List<ChaosCupDrawEntity> stale = await context.ChaosCupDraws
            .Where(d => d.SourceSeasonId == setup.Source.Id && d.StageIndex == 1)
            .ToListAsync().ConfigureAwait(false);
        context.ChaosCupDraws.RemoveRange(stale);
        await context.SaveChangesAsync().ConfigureAwait(false);
        foreach (ChaosCupDrawEntity row in BuildRandomDraw(setup, advancers, stageIndex: 1))
        {
            context.ChaosCupDraws.Add(row);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (ChaosCupRoundEntity Row, ulong NextRng) BuildRoundRow(
        ChaosSetup setup, int stageIndex, int groupNumber, int roundNumber,
        List<int> drawOrder, Dictionary<int, int> cumulative, ulong rngBefore, ulong rngAfter)
    {
        List<RoundPayloadEntry> placements = new(drawOrder.Count);
        for (int position = 1; position <= drawOrder.Count; position++)
        {
            int athleteId = drawOrder[position - 1];
            int basePoints = setup.Rules.ScoringTable[position - 1] * RulesV1.FixedScale;
            int before = cumulative[athleteId];
            int after = checked(before + basePoints);
            cumulative[athleteId] = after;
            placements.Add(new RoundPayloadEntry(athleteId, $"Athlete {athleteId}", position, basePoints, 0, basePoints, before, after, position, position, 0));
        }

        ChaosCupRoundPayloadDocument payload = new(
            ChaosCupRoundPayloadDocument.PayloadVersion,
            setup.Rules.Version,
            ChaosCupFormat.ChaosFormatVersion,
            setup.Source.SeasonNumber,
            stageIndex,
            groupNumber,
            roundNumber,
            rngBefore,
            77UL,
            rngAfter,
            77UL,
            $"chaos-{stageIndex}-{groupNumber}-{roundNumber}",
            placements);
        ChaosCupRoundEntity row = new()
        {
            SourceSeasonId = setup.Source.Id,
            SourceSeasonNumber = setup.Source.SeasonNumber,
            StageIndex = stageIndex,
            GroupNumber = groupNumber,
            RoundNumber = roundNumber,
            RulesVersion = setup.Rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            RngBeforeState = unchecked((long)rngBefore),
            RngBeforeStream = 77L,
            RngAfterState = unchecked((long)rngAfter),
            RngAfterStream = 77L,
            PayloadJson = payload.ToStored(),
            PayloadChecksum = payload.Checksum,
        };
        return (row, rngAfter);
    }

    private static string PlaceCountsJson(int position = 1)
    {
        List<int> counts = new(new int[ChaosCupFormat.GroupSize]);
        counts[position - 1] = ChaosCupFormat.RoundsPerGroup;
        return System.Text.Json.JsonSerializer.Serialize(counts);
    }

    private static async Task<ChaosField> LoadFieldAsync(SaveDbContext context, int sourceSeasonId)
    {
        ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == sourceSeasonId).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == sourceSeasonId).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == sourceSeasonId).ToListAsync().ConfigureAwait(false);
        List<ChaosCupRoundEntity> rounds = await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == sourceSeasonId).ToListAsync().ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonId == sourceSeasonId).ToListAsync().ConfigureAwait(false);
        return new ChaosField(edition, entrants, draws, rounds, standings);
    }

    private static ChaosCupEditionEntity DuplicateEdition(ChaosSetup setup, ChaosField field)
    {
        return new ChaosCupEditionEntity
        {
            SourceSeasonId = setup.Source.Id,
            SourceSeasonNumber = setup.Source.SeasonNumber,
            RulesVersion = setup.Rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            FieldSize = ChaosCupFormat.FieldSize,
            RngBeforeState = 1,
            RngBeforeStream = 2,
            RngAfterState = 3,
            RngAfterStream = 4,
            EditionChecksum = field.Edition.EditionChecksum,
        };
    }

    private static ChaosCupEntrantEntity DuplicateEntrant(ChaosCupEntrantEntity first)
    {
        return new ChaosCupEntrantEntity
        {
            SourceSeasonId = first.SourceSeasonId,
            SourceSeasonNumber = first.SourceSeasonNumber,
            SaveAthleteId = first.SaveAthleteId,
            SportingColor = first.SportingColor,
            SourceLeagueId = first.SourceLeagueId,
            SourceLeagueKind = first.SourceLeagueKind,
            SourceFeederDivision = first.SourceFeederDivision,
            IsPoolInvite = first.IsPoolInvite,
        };
    }

    private static ChaosCupRoundEntity DuplicateRound(ChaosCupRoundEntity round)
    {
        return new ChaosCupRoundEntity
        {
            SourceSeasonId = round.SourceSeasonId,
            SourceSeasonNumber = round.SourceSeasonNumber,
            StageIndex = round.StageIndex,
            GroupNumber = round.GroupNumber,
            RoundNumber = round.RoundNumber,
            RulesVersion = round.RulesVersion,
            ChaosFormatVersion = round.ChaosFormatVersion,
            RngBeforeState = round.RngBeforeState,
            RngBeforeStream = round.RngBeforeStream,
            RngAfterState = round.RngAfterState,
            RngAfterStream = round.RngAfterStream,
            PayloadJson = round.PayloadJson,
            PayloadChecksum = round.PayloadChecksum,
        };
    }

    private static void AssertDuplicateRankRejected(ChaosSetup setup, ChaosField field)
    {
        List<ChaosCupDrawEntity> stage0 = field.Draws.Where(d => d.StageIndex == 0).ToList();
        List<int> groupAthletes = stage0.Where(d => d.GroupNumber == 1).Select(d => d.SaveAthleteId).ToList();
        List<ChaosCupGroupStandingEntity> corrupt = groupAthletes.Select((id, i) => new ChaosCupGroupStandingEntity
        {
            SourceSeasonId = setup.Source.Id,
            SourceSeasonNumber = setup.Source.SeasonNumber,
            StageIndex = 0,
            GroupNumber = 1,
            SaveAthleteId = id,
            GroupRank = i == 1 ? 1 : i + 1,
            GroupScoreThousandths = 0,
            BaseScoreThousandths = 0,
            RoundWins = 0,
            RoundPlaceCountsJson = PlaceCountsJson(),
            SportingColor = (int)SportingColor.White,
            IsAdvanced = true,
        }).ToList();
        Should.Throw<InvalidOperationException>(() => ChaosCupInvariants.ValidateStageStandings(
            setup.Source, field.Edition, stage0, field.Rounds.Where(r => r.StageIndex == 0).ToList(),
            corrupt.Concat(field.Standings.Where(s => !(s.StageIndex == 0 && s.GroupNumber == 1))).ToList(),
            0, setup.Rules));
    }

    private static void AssertRoundGapRejected(ChaosField field)
    {
        List<ChaosCupRoundEntity> groupRounds = field.Rounds.Where(r => r.StageIndex == 0 && r.GroupNumber == 1).ToList();
        List<ChaosCupRoundEntity> gapped = groupRounds.Where(r => r.RoundNumber != 2).ToList();
        Should.Throw<InvalidOperationException>(() => ChaosCupInvariants.ValidateGroupRoundsContiguous(gapped, 0, 1));
    }
}
