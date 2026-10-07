using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using TeamStanding = MtgSoloSports.Features.Cups.RunColorCupTeam.ColorCupTeamMember;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class ColorCupTeamTests
{
    [Fact]
    public async Task Run_BeforeSelection_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Early", 1111UL, 2222UL, UniverseTestCatalog.Build());
            RunColorCupTeamHandler handler = new(store);
            await Should.ThrowAsync<RunColorCupTeamConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_EvenSeason_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Even", 3033UL, 4044UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            SelectColorCupTeamsHandler select = new(store);
            await select.HandleAsync(created.Detail.SaveId, sourceSeasonNumber: 1);
            await MarkSeasonTwoCompleteAsync(store, created.Detail.SaveId);

            RunColorCupTeamHandler handler = new(store);
            await Should.ThrowAsync<RunColorCupTeamConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Get_BeforeResolved_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Missing", 5055UL, 6066UL, UniverseTestCatalog.Build());
            GetColorCupTeamResultHandler query = new(store);
            await Should.ThrowAsync<ColorCupTeamResultNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WrongRankGroup_Aborts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Wrong", 7077UL, 8088UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            SelectColorCupTeamsHandler select = new(store);
            await select.HandleAsync(created.Detail.SaveId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupSelectionEntity> selection = await context.ColorCupSelections.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();

            // Corrupt one group: move a #1 athlete into the #2 bucket and assert the invariant aborts.
            Dictionary<int, List<ColorCupSelectionEntity>> groups = BuildCorruptGroups(selection);
            var rules = MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault();
            Should.Throw<InvalidOperationException>(() => ColorCupTeamInvariants.ValidateGroups(groups, rules));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<int, List<ColorCupSelectionEntity>> BuildCorruptGroups(List<ColorCupSelectionEntity> selection)
    {
        Dictionary<int, List<ColorCupSelectionEntity>> groups = new()
        {
            [1] = selection.Where(e => e.SelectionRank == 1).ToList(),
            [2] = selection.Where(e => e.SelectionRank == 2).ToList(),
            [3] = selection.Where(e => e.SelectionRank == 3).ToList(),
            [4] = selection.Where(e => e.SelectionRank == 4).ToList(),
        };
        ColorCupSelectionEntity intruder = groups[1][0];
        groups[1].RemoveAt(0);
        groups[2].Add(intruder);
        return groups;
    }

    [Fact]
    public async Task Run_AfterSelection_ProducesFourGroupsEightRoundsWithMedalsHonourAndPreservesHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Full", 9091UL, 1011UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            SelectColorCupTeamsHandler select = new(store);
            SelectColorCupTeamsResponse selection = await select.HandleAsync(created.Detail.SaveId);
            selection.TotalSelected.ShouldBe(32);

            (int stages, int seasons, int rounds, long lifetime, long effective, long championship, ulong rng) =
                await CapturePreservationAsync(store, created.Detail.SaveId);

            RunColorCupTeamHandler handler = new(store);
            RunColorCupTeamResponse response = await handler.HandleAsync(created.Detail.SaveId);

            AssertResponseBasics(response);
            AssertGroupStructure(response);
            await AssertFullHistoryAsync(store, created.Detail.SaveId, response, stages, seasons, rounds, lifetime, effective, championship, rng);

            await Should.ThrowAsync<RunColorCupTeamConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertResponseBasics(RunColorCupTeamResponse response)
    {
        response.SourceSeasonNumber.ShouldBe(1);
        response.TeamCount.ShouldBe(8);
        response.GroupCount.ShouldBe(4);
        response.GroupRounds.ShouldBe(8);
        response.Teams.Count.ShouldBe(8);
        response.Legs.Count.ShouldBe(32);
        response.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 8).ToList());
        response.Checksum.ShouldNotBeNullOrWhiteSpace();

        TeamStanding gold = response.Teams.Single(t => t.TeamRank == 1);
        TeamStanding silver = response.Teams.Single(t => t.TeamRank == 2);
        TeamStanding bronze = response.Teams.Single(t => t.TeamRank == 3);
        gold.Medal.ShouldBe(nameof(ColorCupMedal.Gold));
        silver.Medal.ShouldBe(nameof(ColorCupMedal.Silver));
        bronze.Medal.ShouldBe(nameof(ColorCupMedal.Bronze));
        response.Teams.Where(t => t.TeamRank > 3).All(t => string.Equals(t.Medal, nameof(ColorCupMedal.None), StringComparison.Ordinal)).ShouldBeTrue();
        response.ChampionSportingColor.ShouldBe(gold.SportingColor);
        response.ChampionTeamName.ShouldBe(gold.TeamName);
    }

    private static void AssertGroupStructure(RunColorCupTeamResponse response)
    {
        foreach (int group in Enumerable.Range(1, 4))
        {
            List<ColorCupTeamLegMember> legs = response.Legs.Where(l => l.GroupNumber == group).ToList();
            legs.Count.ShouldBe(8);
            legs.Select(l => l.GroupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 8).ToList());
            legs.All(l => l.SelectionRank == group).ShouldBeTrue();
            legs.Select(l => l.SportingColor).Distinct(StringComparer.Ordinal).Count().ShouldBe(8);
        }
    }

    private static async Task AssertFullHistoryAsync(
        SaveStore store,
        Guid saveId,
        RunColorCupTeamResponse response,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long effective,
        long championship,
        ulong rng)
    {
        await AssertTeamScoresAreSumsAsync(store, saveId, response).ConfigureAwait(false);
        await AssertNoWrongRankGroupAsync(store, saveId, response).ConfigureAwait(false);
        await AssertPersistedAsync(store, saveId, response).ConfigureAwait(false);
        await AssertHonourAsync(store, saveId, response).ConfigureAwait(false);
        await AssertStoriesAsync(store, saveId, response).ConfigureAwait(false);
        await AssertPreservationAsync(store, saveId, stages, seasons, rounds, lifetime, effective, championship, rng).ConfigureAwait(false);
        await AssertQueryMatchesAsync(store, saveId, response).ConfigureAwait(false);
        await AssertReplayAsync(store, saveId, response).ConfigureAwait(false);
    }

    [Fact]
    public async Task Run_ParticipationCannotChangeCareerBonus()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Team Bonus", 1213UL, 1415UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            SelectColorCupTeamsHandler select = new(store);
            await select.HandleAsync(created.Detail.SaveId);

            Dictionary<int, int> lifetimeBefore = await LoadCareerBonusAsync(store, created.Detail.SaveId, lifetime: true);
            Dictionary<int, int> effectiveBefore = await LoadCareerBonusAsync(store, created.Detail.SaveId, lifetime: false);
            (int stagesBefore, int seasonsBefore, int roundsBefore, long championshipBefore) =
                await CaptureLeagueCountsAsync(store, created.Detail.SaveId);

            RunColorCupTeamHandler handler = new(store);
            RunColorCupTeamResponse response = await handler.HandleAsync(created.Detail.SaveId);
            response.Teams.Count.ShouldBe(8);
            response.Legs.Count.ShouldBe(32);

            Dictionary<int, int> lifetimeAfter = await LoadCareerBonusAsync(store, created.Detail.SaveId, lifetime: true);
            Dictionary<int, int> effectiveAfter = await LoadCareerBonusAsync(store, created.Detail.SaveId, lifetime: false);
            lifetimeAfter.ShouldBe(lifetimeBefore);
            effectiveAfter.ShouldBe(effectiveBefore);

            (int stagesAfter, int seasonsAfter, int roundsAfter, long championshipAfter) =
                await CaptureLeagueCountsAsync(store, created.Detail.SaveId);
            stagesAfter.ShouldBe(stagesBefore);
            seasonsAfter.ShouldBe(seasonsBefore);
            roundsAfter.ShouldBe(roundsBefore);
            championshipAfter.ShouldBe(championshipBefore);

            await AssertActiveBonusUsedAsync(store, created.Detail.SaveId, response);
            await AssertTeamScoresAreSumsAsync(store, created.Detail.SaveId, response);
            await AssertNoWrongRankGroupAsync(store, created.Detail.SaveId, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_SameSeed_IsDeterministic()
    {
        // MSS-067: one prepared save forked into two isolated copies instead of
        // simulating the same Season 1 twice; both runs still execute
        // independently from bit-identical starting state.
        var (store, root, saveId) = await PrepareTeamForSeedAsync(42421UL, 7771UL);
        var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-cup-team-det-");
        try
        {
            RunColorCupTeamResponse first = await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            RunColorCupTeamResponse second = await new RunColorCupTeamHandler(secondStore).HandleAsync(secondId);
            first.Checksum.ShouldBe(second.Checksum);
            first.Teams.Select(t => t.SportingColor).ShouldBe(second.Teams.Select(t => t.SportingColor).ToList());
            first.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(second.Teams.Select(t => t.TeamScoreThousandths).ToList());
            first.ChampionSportingColor.ShouldBe(second.ChampionSportingColor);
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            TestSaveStores.DeleteRoot(secondRoot);
        }
    }

    private static async Task AssertTeamScoresAreSumsAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (TeamStanding team in response.Teams)
        {
            int expected = legs.Where(l => l.SportingColor == team.SportingColor).Sum(l => l.GroupScoreThousandths);
            team.TeamScoreThousandths.ShouldBe(expected);
            int expectedBase = legs.Where(l => l.SportingColor == team.SportingColor).Sum(l => l.BaseScoreThousandths);
            team.TeamBaseThousandths.ShouldBe(expectedBase);
            legs.Count(l => l.SportingColor == team.SportingColor).ShouldBe(4);
        }
    }

    private static async Task AssertNoWrongRankGroupAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        Dictionary<int, int> selectionRanks = await context.ColorCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.SelectionRank).ConfigureAwait(false);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (ColorCupTeamRoundEntity round in rounds)
        {
            ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.GroupNumber.ShouldBe(round.GroupNumber);
            document.Placements.Count.ShouldBe(8);
            foreach (var placement in document.Placements)
            {
                selectionRanks.TryGetValue(placement.AthleteId, out int rank).ShouldBeTrue();
                rank.ShouldBe(round.GroupNumber);
            }
        }

        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (ColorCupTeamGroupStandingEntity leg in legs)
        {
            leg.SelectionRank.ShouldBe(leg.GroupNumber);
            selectionRanks[leg.SaveAthleteId].ShouldBe(leg.GroupNumber);
        }
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);

        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (int group in Enumerable.Range(1, 4))
        {
            rounds.Where(r => r.GroupNumber == group).Select(r => r.RoundNumber).ShouldBe(Enumerable.Range(1, 8).ToList());
        }

        foreach (ColorCupTeamRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Placements.Count.ShouldBe(8);
            document.Checksum.ShouldBe(round.PayloadChecksum);
            document.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
            document.GroupNumber.ShouldBe(round.GroupNumber);
        }

        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        legs.Count.ShouldBe(32);

        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank).ToListAsync().ConfigureAwait(false);
        teams.Count.ShouldBe(8);
        teams.Select(s => s.TeamRank).ShouldBe(Enumerable.Range(1, 8).ToList());
        teams.Single(s => s.TeamRank == 1).Medal.ShouldBe((int)ColorCupMedal.Gold);
        teams.Single(s => s.TeamRank == 2).Medal.ShouldBe((int)ColorCupMedal.Silver);
        teams.Single(s => s.TeamRank == 3).Medal.ShouldBe((int)ColorCupMedal.Bronze);
        teams.Where(s => s.TeamRank > 3).All(s => s.Medal == (int)ColorCupMedal.None).ShouldBeTrue();
    }

    private static async Task AssertHonourAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<HonourEntity> teamHonours = await context.Honours.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && (e.Kind == (int)HonourKind.ColorCupTeamChampion
                || e.Kind == (int)HonourKind.ColorCupTeamRunnerUp
                || e.Kind == (int)HonourKind.ColorCupTeamThirdPlace))
            .ToListAsync().ConfigureAwait(false);
        // MSS-047: podium teams 1st/2nd/3rd each contribute four honours (one per leg).
        teamHonours.Count.ShouldBe(12);
        teamHonours.Count(h => h.Kind == (int)HonourKind.ColorCupTeamChampion).ShouldBe(4);
        teamHonours.Count(h => h.Kind == (int)HonourKind.ColorCupTeamRunnerUp).ShouldBe(4);
        teamHonours.Count(h => h.Kind == (int)HonourKind.ColorCupTeamThirdPlace).ShouldBe(4);
        foreach (HonourEntity honour in teamHonours)
        {
            honour.LeagueName.ShouldBe(RunColorCupTeamHandler.TeamLeagueName);
        }

        HashSet<int> championLegs = response.Legs
            .Where(l => string.Equals(l.SportingColor, response.ChampionTeamName, StringComparison.Ordinal))
            .Select(l => l.AthleteId).ToHashSet();
        championLegs.Count.ShouldBe(4);
        teamHonours.Where(h => h.Kind == (int)HonourKind.ColorCupTeamChampion).Select(h => h.SaveAthleteId).OrderBy(id => id).ShouldBe(championLegs.OrderBy(id => id).ToList());

        // Fourth-place team contributes no honour.
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        ColorCupTeamStandingEntity fourth = teams.Single(t => t.TeamRank == 4);
        List<ColorCupTeamGroupStandingEntity> fourthLegs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.SportingColor == fourth.SportingColor).ToListAsync().ConfigureAwait(false);
        fourthLegs.Count.ShouldBe(4);
        foreach (ColorCupTeamGroupStandingEntity leg in fourthLegs)
        {
            teamHonours.Any(h => h.SaveAthleteId == leg.SaveAthleteId).ShouldBeFalse();
        }

        ListHonoursHandler honoursHandler = new(store);
        ListHonoursResponse honours = await honoursHandler.HandleAsync(saveId).ConfigureAwait(false);
        honours.Honours.Any(h =>
            h.SeasonNumber == response.SourceSeasonNumber &&
            string.Equals(h.HonourKind, nameof(HonourKind.ColorCupTeamChampion), StringComparison.Ordinal)).ShouldBeTrue();
    }

    private static async Task AssertStoriesAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<StoryEventEntity> stories = await context.StoryEvents.AsNoTracking()
            .Where(e => e.SeasonNumber == response.SourceSeasonNumber &&
                (e.EventType == StoryEventType.ColorCupTeamTitle || e.EventType == StoryEventType.ColorCupTeamMedal))
            .ToListAsync().ConfigureAwait(false);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.ColorCupTeamMedal, StringComparison.Ordinal)).ShouldBe(12);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.ColorCupTeamTitle, StringComparison.Ordinal)).ShouldBe(4);
        foreach (StoryEventEntity title in stories.Where(e => string.Equals(e.EventType, StoryEventType.ColorCupTeamTitle, StringComparison.Ordinal)))
        {
            string text = StoryEventRenderer.Render(title.EventType, title.ContextJson);
            text.ShouldContain(response.ChampionTeamName);
        }
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long effective,
        long championship,
        ulong rngBefore)
    {
        (int stagesAfter, int seasonsAfter, int roundsAfter, long lifetimeAfter, long effectiveAfter, long championshipAfter, ulong rngAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stagesAfter.ShouldBe(stages);
        seasonsAfter.ShouldBe(seasons);
        roundsAfter.ShouldBe(rounds);
        lifetimeAfter.ShouldBe(lifetime);
        effectiveAfter.ShouldBe(effective);
        championshipAfter.ShouldBe(championship);
        rngAfter.ShouldNotBe(rngBefore);
    }

    private static async Task AssertQueryMatchesAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        GetColorCupTeamResultHandler query = new(store);
        GetColorCupTeamResultResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        summary.TeamCount.ShouldBe(8);
        summary.GroupCount.ShouldBe(4);
        summary.GroupRounds.ShouldBe(8);
        summary.Checksum.ShouldNotBeNullOrWhiteSpace();
        summary.ChampionSportingColor.ShouldBe(response.ChampionSportingColor);
        summary.Teams.Count.ShouldBe(8);
        summary.Legs.Count.ShouldBe(32);
        summary.Teams.Select(m => m.SportingColor).ShouldBe(response.Teams.Select(m => m.SportingColor).ToList());
        summary.Teams.Select(m => m.TeamRank).ShouldBe(response.Teams.Select(m => m.TeamRank).ToList());
        summary.Teams.Select(m => m.Medal).ShouldBe(response.Teams.Select(m => m.Medal).ToList());

        GetColorCupTeamResultResponse again = await query.HandleAsync(saveId, sourceSeasonNumber: response.SourceSeasonNumber).ConfigureAwait(false);
        again.ChampionSportingColor.ShouldBe(response.ChampionSportingColor);
        again.GroupRounds.ShouldBe(8);
    }

    private static async Task AssertReplayAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        ColorCupTeamRoundPayloadDocument first = ColorCupTeamRoundPayloadDocument.FromStored(rounds[0].PayloadJson);
        first.Placements.Count.ShouldBe(8);
        first.RngBeforeState.ShouldBe(response.RngBeforeState);
        ColorCupTeamRoundPayloadDocument last = ColorCupTeamRoundPayloadDocument.FromStored(rounds[^1].PayloadJson);
        last.RngAfterState.ShouldBe(response.RngAfterState);
        foreach (ColorCupTeamRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Checksum.ShouldBe(round.PayloadChecksum);
        }
    }

    private static async Task AssertActiveBonusUsedAsync(SaveStore store, Guid saveId, RunColorCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        ColorCupTeamRoundEntity roundOne = await context.ColorCupTeamRounds.AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == source.Id && e.GroupNumber == 1 && e.RoundNumber == 1).ConfigureAwait(false);
        roundOne.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
        ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(roundOne.PayloadJson);

        Dictionary<int, int> seasonNumbers = await context.Seasons.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        int cupSeason = source.SeasonNumber + 1;
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (ColorCupTeamGroupStandingEntity leg in legs.Where(l => l.GroupNumber == 1))
        {
            List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking()
                .Where(e => e.SaveAthleteId == leg.SaveAthleteId).ToListAsync().ConfigureAwait(false);
            List<MtgSoloSports.SimulationKernel.Scoring.BonusContribution> contributions = new(rows.Count);
            foreach (StageStandingEntity row in rows)
            {
                int earnedSeason = seasonNumbers[row.SeasonId];
                contributions.Add(new MtgSoloSports.SimulationKernel.Scoring.BonusContribution(
                    earnedSeason,
                    row.StageNumber,
                    MtgSoloSports.SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
            }

            MtgSoloSports.SimulationKernel.FixedPoint.Bonus expected =
                MtgSoloSports.SimulationKernel.Scoring.BonusCalculator.EffectiveBonus(
                    contributions, cupSeason, 1, MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault());
            int actual = document.Placements.Single(p => p.AthleteId == leg.SaveAthleteId).ActiveBonusThousandths;
            actual.ShouldBe(expected.Thousandths);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTeamForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Team Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        SelectColorCupTeamsHandler select = new(store);
        await select.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task<(int StageCount, int SeasonCount, int RoundCount, long Lifetime, long Effective, long Championship, ulong Rng)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, rounds, lifetime, effective, championship, (ulong)rng.State);
        }
    }

    private static async Task<Dictionary<int, int>> LoadCareerBonusAsync(SaveStore store, Guid saveId, bool lifetime)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<AthleteCareerEntity> careers = await context.AthleteCareers.AsNoTracking().ToListAsync().ConfigureAwait(false);
        return lifetime
            ? careers.ToDictionary(e => e.SaveAthleteId, e => e.LifetimeEarnedBonusThousandths)
            : careers.ToDictionary(e => e.SaveAthleteId, e => e.CurrentEffectiveBonusThousandths);
    }

    private static async Task<(int Stages, int Seasons, int Rounds, long Championship)> CaptureLeagueCountsAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        return (stages, seasons, rounds, championship);
    }

    private static async Task CompleteSeasonOneAsync(SaveStore store, Guid saveId)
    {
        CompleteStageForAllLeaguesHandler bulk = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            CompleteStageForAllLeaguesResponse completed = await bulk.HandleAsync(saveId).ConfigureAwait(false);
            completed.CompletedStage.ShouldBe(stage);
        }
    }

    private static async Task MarkSeasonTwoCompleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = new() { SeasonNumber = 2, HasSuperleague = true, IsComplete = true };
        context.Seasons.Add(seasonTwo);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cup-team-" + Guid.NewGuid().ToString("N"));
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
