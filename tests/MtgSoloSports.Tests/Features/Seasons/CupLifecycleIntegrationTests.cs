using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupSelection;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupSelection;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Features.Stories.ListRecent;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-028 integration: alternating post-season Cups are explicit inspectable
/// Next Event boundaries after rebalancing, gated before season finalization,
/// with identical RNG/mathematics for manual AdvanceToNextEvent and bulk
/// SimulateSeasons progression.
/// </summary>
public sealed class CupLifecycleIntegrationTests
{
    [Fact]
    public async Task ThreeSeasons_Rotate_ColorTypeColor_WithInspectableBoundaries()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Rotation", 4242UL, 777UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            // Season 1 (odd) -> Color Cup.
            await AdvanceSeasonStagesAsync(store, saveId, expectedSeason: 1, stages: 32);
            await AssertCupPendingAsync(store, saveId, sourceSeason: 1, expectedCup: CupExtensionPoint.ColorCup);
            await RunColorCupViaAdvanceAsync(store, saveId, sourceSeason: 1);
            await AssertCupCompleteAsync(store, saveId, sourceSeason: 1, expectedCup: CupExtensionPoint.ColorCup);
            await StartNextSeasonAsync(store, saveId, fromSeason: 1, toSeason: 2);

            // Season 2 (even) -> Type Cup.
            await AdvanceSeasonStagesAsync(store, saveId, expectedSeason: 2, stages: 32);
            await AssertCupPendingAsync(store, saveId, sourceSeason: 2, expectedCup: CupExtensionPoint.TypeCup);
            await RunTypeCupViaAdvanceAsync(store, saveId, sourceSeason: 2);
            await AssertCupCompleteAsync(store, saveId, sourceSeason: 2, expectedCup: CupExtensionPoint.TypeCup);
            await StartNextSeasonAsync(store, saveId, fromSeason: 2, toSeason: 3);

            // Season 3 (odd) -> Color Cup again, proving rotation.
            await AdvanceSeasonStagesAsync(store, saveId, expectedSeason: 3, stages: 32);
            await AssertCupPendingAsync(store, saveId, sourceSeason: 3, expectedCup: CupExtensionPoint.ColorCup);
            await RunColorCupViaAdvanceAsync(store, saveId, sourceSeason: 3);
            await AssertCupCompleteAsync(store, saveId, sourceSeason: 3, expectedCup: CupExtensionPoint.ColorCup);

            await AssertThreeSeasonHonoursAsync(store, saveId);
            await AssertCupStoriesAsync(store, saveId);
            await AssertHistoryNavigationAsync(store, saveId);
            await AssertCupBonusUsesPreAgingAsync(store, saveId, sourceSeason: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartNextSeason_RequiresCupComplete()
    {
        // MSS-067: shared Season 1 template (Season 1 already complete, same as
        // after AdvanceStagesOnlyAsync) forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-cup-gate-");
        try
        {
            AdvanceToNextEventHandler advance = new(store);
            StartNextSeasonHandler starter = new(store);

            // Movement + rebalance without Cups.
            AdvanceToNextEventResponse inauguralStep = await advance.HandleAsync(saveId);
            inauguralStep.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveInauguralMovement);
            AdvanceToNextEventResponse rebalanceStep = await advance.HandleAsync(saveId);
            rebalanceStep.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);

            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse pending = await status.HandleAsync(saveId);
            pending.LegalNextActions.ShouldBe([SeasonLifecycleActions.SelectColorCup]);
            pending.CupComplete.ShouldBeFalse();
            pending.ReadyToStartNextSeason.ShouldBeFalse();

            await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId));

            // Selection alone is not enough.
            AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId);
            selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectColorCup);
            await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId));

            // Individual alone is not enough.
            AdvanceToNextEventResponse individual = await advance.HandleAsync(saveId);
            individual.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupIndividual);
            await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId));

            // Team completes the Cup and unlocks the next season.
            AdvanceToNextEventResponse team = await advance.HandleAsync(saveId);
            team.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupTeam);
            GetSeasonStatusResponse ready = await status.HandleAsync(saveId);
            ready.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);
            ready.CupComplete.ShouldBeTrue();

            StartNextSeasonResponse started = await starter.HandleAsync(saveId);
            started.FromSeasonNumber.ShouldBe(1);
            started.ToSeasonNumber.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// MSS-067: opt-in multi-season soak. The same fast-vs-manual equivalence
    /// invariant is covered in normal CI over one season by
    /// <c>FastSimulationTests.SimulateSeasons_EquivalentToManualAdvanceToNextEvent</c>
    /// and <c>LongRunChecksumHandlerTests.FastSimulation_MatchesManual_Checksum</c>;
    /// this three-season variant runs only with <c>MTG_LONGRUN=1</c>.
    /// </summary>
    [Fact]
    public async Task FastAndManual_ThreeSeasons_ProduceIdenticalCupsAndRng()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MTG_LONGRUN"), "1", StringComparison.Ordinal))
        {
            SimulateSeasonsHandler.MaxSeasonsPerRequest.ShouldBeGreaterThanOrEqualTo(3);
            return;
        }

        var (manualStore, manualRoot) = CreateStore();
        var (fastStore, fastRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord manual = await manualStore.CreateAsync("Cup Manual", 9991UL, 8882UL, catalog);
            SaveStore.CreationRecord fast = await fastStore.CreateAsync("Cup Fast", 9991UL, 8882UL, catalog);

            await AdvanceThreeSeasonsManuallyAsync(manualStore, manual.Detail.SaveId);

            SimulateSeasonsHandler bulk = new(fastStore);
            SimulateSeasonsResponse response = await bulk.HandleAsync(
                fast.Detail.SaveId, new SimulateSeasonsRequest(3));
            response.SeasonsCompleted.ShouldBe(3);
            response.StagesCompleted.ShouldBe(96);
            response.StartSeasonNumber.ShouldBe(1);
            response.EndSeasonNumber.ShouldBe(4);

            await AssertRngEqualAsync(manualStore, manual.Detail.SaveId, fastStore, fast.Detail.SaveId);
            await AssertCupChecksumsEqualAsync(manualStore, manual.Detail.SaveId, fastStore, fast.Detail.SaveId);
            await AssertHonoursEqualAsync(manualStore, manual.Detail.SaveId, fastStore, fast.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(manualRoot, recursive: true);
            Directory.Delete(fastRoot, recursive: true);
        }
    }

    private static async Task AdvanceSeasonStagesAsync(SaveStore store, Guid saveId, int expectedSeason, int stages)
    {
        AdvanceToNextEventHandler advance = new(store);
        for (int i = 0; i < stages; i++)
        {
            AdvanceToNextEventResponse stepped = await advance.HandleAsync(saveId).ConfigureAwait(false);
            stepped.ExecutedAction.ShouldBe(SeasonLifecycleActions.CompleteNextGlobalStage);
        }

        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse after = await status.HandleAsync(saveId).ConfigureAwait(false);
        after.IsCurrentSeasonComplete.ShouldBeTrue();

        // Drain movement/qualifier/rebalance until the Cup selection is next.
        // Season 1 needs 2 steps (inaugural + rebalance); Season 2+ needs 3.
        while (after.LegalNextActions.Count == 1
            && !string.Equals(after.LegalNextActions[0], SeasonLifecycleActions.SelectColorCup, StringComparison.Ordinal)
            && !string.Equals(after.LegalNextActions[0], SeasonLifecycleActions.SelectTypeCup, StringComparison.Ordinal))
        {
            AdvanceToNextEventResponse step = await advance.HandleAsync(saveId).ConfigureAwait(false);
            string.Equals(step.ExecutedAction, SeasonLifecycleActions.StartNextSeason, StringComparison.Ordinal).ShouldBeFalse();
            after = await status.HandleAsync(saveId).ConfigureAwait(false);
        }

        after.SourceSeasonNumber.ShouldBe(expectedSeason);
    }

    private static async Task AdvanceStagesOnlyAsync(SaveStore store, Guid saveId, int stages)
    {
        AdvanceToNextEventHandler advance = new(store);
        for (int i = 0; i < stages; i++)
        {
            AdvanceToNextEventResponse stepped = await advance.HandleAsync(saveId).ConfigureAwait(false);
            stepped.ExecutedAction.ShouldBe(SeasonLifecycleActions.CompleteNextGlobalStage);
        }
    }

    private static async Task AssertCupPendingAsync(SaveStore store, Guid saveId, int sourceSeason, string expectedCup)
    {
        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse pending = await status.HandleAsync(saveId).ConfigureAwait(false);
        pending.SourceSeasonNumber.ShouldBe(sourceSeason);
        pending.ExpectedCup.ShouldBe(expectedCup);
        pending.Rebalanced.ShouldBeTrue();
        pending.CupSelectionResolved.ShouldBeFalse();
        pending.CupComplete.ShouldBeFalse();
        pending.ReadyToStartNextSeason.ShouldBeFalse();
        string expectedAction = string.Equals(expectedCup, CupExtensionPoint.ColorCup, StringComparison.Ordinal)
            ? SeasonLifecycleActions.SelectColorCup
            : SeasonLifecycleActions.SelectTypeCup;
        pending.LegalNextActions.ShouldBe([expectedAction]);
        pending.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.Rebalanced));
    }

    private static async Task RunColorCupViaAdvanceAsync(SaveStore store, Guid saveId, int sourceSeason)
    {
        AdvanceToNextEventHandler advance = new(store);
        GetSeasonStatusHandler status = new(store);

        AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectColorCup);
        GetSeasonStatusResponse afterSelect = await status.HandleAsync(saveId).ConfigureAwait(false);
        afterSelect.CupSelectionResolved.ShouldBeTrue();
        afterSelect.CupIndividualResolved.ShouldBeFalse();
        afterSelect.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupSelectionResolved));
        afterSelect.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunColorCupIndividual]);

        // Field is inspectable before the events run.
        GetColorCupSelectionHandler selectionQuery = new(store);
        GetColorCupSelectionResponse field = await selectionQuery.HandleAsync(saveId, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        field.SourceSeasonNumber.ShouldBe(sourceSeason);
        field.Teams.Count.ShouldBe(8);
        field.Teams.Sum(t => t.Members.Count).ShouldBe(32);

        AdvanceToNextEventResponse individual = await advance.HandleAsync(saveId).ConfigureAwait(false);
        individual.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupIndividual);
        GetSeasonStatusResponse afterIndividual = await status.HandleAsync(saveId).ConfigureAwait(false);
        afterIndividual.CupIndividualResolved.ShouldBeTrue();
        afterIndividual.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupIndividualResolved));
        afterIndividual.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunColorCupTeam]);

        // Individual result is inspectable before the team event.
        GetColorCupIndividualResultHandler individualQuery = new(store);
        GetColorCupIndividualResultResponse individualResult =
            await individualQuery.HandleAsync(saveId, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        individualResult.SourceSeasonNumber.ShouldBe(sourceSeason);
        individualResult.Standings.Count.ShouldBe(32);

        AdvanceToNextEventResponse team = await advance.HandleAsync(saveId).ConfigureAwait(false);
        team.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupTeam);
        GetColorCupTeamResultHandler teamQuery = new(store);
        GetColorCupTeamResultResponse teamResult =
            await teamQuery.HandleAsync(saveId, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        teamResult.SourceSeasonNumber.ShouldBe(sourceSeason);
        teamResult.Teams.Count.ShouldBe(8);
    }

    private static async Task RunTypeCupViaAdvanceAsync(SaveStore store, Guid saveId, int sourceSeason)
    {
        AdvanceToNextEventHandler advance = new(store);
        GetSeasonStatusHandler status = new(store);

        AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectTypeCup);
        GetSeasonStatusResponse afterSelect = await status.HandleAsync(saveId).ConfigureAwait(false);
        afterSelect.CupSelectionResolved.ShouldBeTrue();
        afterSelect.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupSelectionResolved));
        afterSelect.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunTypeCupTeam]);

        GetTypeCupSelectionHandler selectionQuery = new(store);
        GetTypeCupSelectionResponse field = await selectionQuery.HandleAsync(saveId, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        field.SourceSeasonNumber.ShouldBe(sourceSeason);
        field.TeamCount.ShouldBeGreaterThan(0);
        field.TotalSelected.ShouldBe(field.TeamCount * 4);

        AdvanceToNextEventResponse team = await advance.HandleAsync(saveId).ConfigureAwait(false);
        team.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunTypeCupTeam);
        GetTypeCupTeamResultHandler teamQuery = new(store);
        GetTypeCupTeamResultResponse teamResult =
            await teamQuery.HandleAsync(saveId, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        teamResult.SourceSeasonNumber.ShouldBe(sourceSeason);
        teamResult.TeamCount.ShouldBe(field.TeamCount);

        // Permanent nationality is set atomically with participation.
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber == sourceSeason)
            .ToListAsync().ConfigureAwait(false);
        selections.Count.ShouldBeGreaterThan(0);
        foreach (TypeCupSelectionEntity row in selections)
        {
            SaveAthleteEntity athlete = await context.SaveAthletes
                .AsNoTracking()
                .SingleAsync(e => e.Id == row.SaveAthleteId).ConfigureAwait(false);
            athlete.TypeCupNationality.ShouldBe(row.CreatureType);
        }
    }

    private static async Task AssertCupCompleteAsync(SaveStore store, Guid saveId, int sourceSeason, string expectedCup)
    {
        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse ready = await status.HandleAsync(saveId).ConfigureAwait(false);
        ready.SourceSeasonNumber.ShouldBe(sourceSeason);
        ready.ExpectedCup.ShouldBe(expectedCup);
        ready.CupSelectionResolved.ShouldBeTrue();
        ready.CupTeamResolved.ShouldBeTrue();
        ready.CupComplete.ShouldBeTrue();
        ready.ReadyToStartNextSeason.ShouldBeTrue();
        ready.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupComplete));
        ready.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);
    }

    private static async Task StartNextSeasonAsync(SaveStore store, Guid saveId, int fromSeason, int toSeason)
    {
        AdvanceToNextEventHandler advance = new(store);
        AdvanceToNextEventResponse started = await advance.HandleAsync(saveId).ConfigureAwait(false);
        started.ExecutedAction.ShouldBe(SeasonLifecycleActions.StartNextSeason);
        started.CurrentSeasonNumber.ShouldBe(toSeason);

        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse after = await status.HandleAsync(saveId).ConfigureAwait(false);
        after.CurrentSeasonNumber.ShouldBe(toSeason);
        after.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonInProgress));
        _ = fromSeason;
    }

    private static async Task AssertThreeSeasonHonoursAsync(SaveStore store, Guid saveId)
    {
        ListHonoursHandler honours = new(store);
        ListHonoursResponse response = await honours.HandleAsync(saveId).ConfigureAwait(false);
        // MSS-047: podiums accumulate across seasons/types without duplication.
        // Two odd seasons => 2 champions/2 runner-ups/2 thirds for the individual event.
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupIndividualChampion), StringComparison.Ordinal)).ShouldBe(2);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupIndividualRunnerUp), StringComparison.Ordinal)).ShouldBe(2);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupIndividualThirdPlace), StringComparison.Ordinal)).ShouldBe(2);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupTeamChampion), StringComparison.Ordinal)).ShouldBe(8);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupTeamRunnerUp), StringComparison.Ordinal)).ShouldBe(8);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.ColorCupTeamThirdPlace), StringComparison.Ordinal)).ShouldBe(8);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.TypeCupTeamChampion), StringComparison.Ordinal)).ShouldBe(4);
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.FeederTitle), StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        // Wins stay win-only: runner-up/third kinds never counted as titles.
        response.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.FeederRunnerUp), StringComparison.Ordinal)).ShouldBeGreaterThan(0);
    }

    private static async Task AssertCupStoriesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> types = await context.StoryEvents.AsNoTracking().Select(e => e.EventType).ToListAsync().ConfigureAwait(false);
        types.Any(t => string.Equals(t, StoryEventType.ColorCupIndividualTitle, StringComparison.Ordinal)).ShouldBeTrue();
        types.Any(t => string.Equals(t, StoryEventType.ColorCupTeamTitle, StringComparison.Ordinal)).ShouldBeTrue();
        types.Any(t => string.Equals(t, StoryEventType.TypeCupTeamTitle, StringComparison.Ordinal)).ShouldBeTrue();

        // Dashboard recent feed stays readable with Cups present.
        ListRecentStoriesHandler stories = new(store);
        ListRecentStoriesResponse feed = await stories.HandleAsync(saveId, take: 20).ConfigureAwait(false);
        feed.Stories.Count.ShouldBeGreaterThan(0);
    }

    private static async Task AssertHistoryNavigationAsync(SaveStore store, Guid saveId)
    {
        ListHistoryCompetitionsHandler competitions = new(store);
        ListHistoryCompetitionsResponse seasonOne = await competitions.HandleAsync(saveId, seasonNumber: 1).ConfigureAwait(false);
        seasonOne.Competitions.Count.ShouldBeGreaterThan(0);
        seasonOne.IsSeasonComplete.ShouldBeTrue();

        ListHistoryCompetitionsResponse seasonTwo = await competitions.HandleAsync(saveId, seasonNumber: 2).ConfigureAwait(false);
        seasonTwo.Competitions.Count.ShouldBeGreaterThan(0);
    }

    private static async Task AssertCupBonusUsesPreAgingAsync(SaveStore store, Guid saveId, int sourceSeason)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == sourceSeason).ConfigureAwait(false);
        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(16);

        Dictionary<int, int> seasonNumbers = await context.Seasons.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        int cupSeason = sourceSeason + 1;
        RulesV1 rules = RulesV1.CreateDefault();
        ColorCupIndividualRoundPayloadDocument first =
            ColorCupIndividualRoundPayloadDocument.FromStored(rounds[0].PayloadJson);
        foreach (RoundPayloadEntry placement in first.Placements)
        {
            List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking()
                .Where(e => e.SaveAthleteId == placement.AthleteId)
                .ToListAsync().ConfigureAwait(false);
            List<BonusContribution> contributions = rows
                .Select(r => new BonusContribution(seasonNumbers[r.SeasonId], r.StageNumber, Bonus.FromThousandths(r.EarnedBonusThousandths)))
                .ToList();
            int expected = BonusCalculator.EffectiveBonus(contributions, cupSeason, 1, rules).Thousandths;
            placement.ActiveBonusThousandths.ShouldBe(expected);
        }
    }

    private static async Task AdvanceThreeSeasonsManuallyAsync(SaveStore store, Guid saveId)
    {
        AdvanceToNextEventHandler stepper = new(store);
        GetSeasonStatusHandler status = new(store);
        int guard = 0;
        while (true)
        {
            guard = checked(guard + 1);
            if (guard > 200)
            {
                throw new InvalidOperationException("Manual lifecycle did not reach Season 4.");
            }

            GetSeasonStatusResponse before = await status.HandleAsync(saveId).ConfigureAwait(false);
            if (before.CurrentSeasonNumber == 4
                && string.Equals(before.ComputedPhase, SavePhaseParser.ToText(SavePhase.SeasonInProgress), StringComparison.Ordinal))
            {
                break;
            }

            await stepper.HandleAsync(saveId).ConfigureAwait(false);
        }
    }

    private static async Task AssertRngEqualAsync(SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave)
    {
        using SaveDbContext first = firstStore.OpenDbContext(firstSave);
        using SaveDbContext second = secondStore.OpenDbContext(secondSave);
        RngStateEntity firstRng = await first.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        RngStateEntity secondRng = await second.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        ((ulong)firstRng.State).ShouldBe((ulong)secondRng.State);
        ((ulong)firstRng.Stream).ShouldBe((ulong)secondRng.Stream);
    }

    private static async Task AssertCupChecksumsEqualAsync(SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave)
    {
        // Color Cup Season 1 individual + team, Type Cup Season 2 team, Color Cup Season 3 both.
        await AssertColorIndividualEqualAsync(firstStore, firstSave, secondStore, secondSave, sourceSeason: 1).ConfigureAwait(false);
        await AssertColorTeamEqualAsync(firstStore, firstSave, secondStore, secondSave, sourceSeason: 1).ConfigureAwait(false);
        await AssertTypeTeamEqualAsync(firstStore, firstSave, secondStore, secondSave, sourceSeason: 2).ConfigureAwait(false);
        await AssertColorIndividualEqualAsync(firstStore, firstSave, secondStore, secondSave, sourceSeason: 3).ConfigureAwait(false);
        await AssertColorTeamEqualAsync(firstStore, firstSave, secondStore, secondSave, sourceSeason: 3).ConfigureAwait(false);
    }

    private static async Task AssertColorIndividualEqualAsync(
        SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave, int sourceSeason)
    {
        GetColorCupIndividualResultHandler firstQuery = new(firstStore);
        GetColorCupIndividualResultHandler secondQuery = new(secondStore);
        GetColorCupIndividualResultResponse first =
            await firstQuery.HandleAsync(firstSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        GetColorCupIndividualResultResponse second =
            await secondQuery.HandleAsync(secondSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        first.Checksum.ShouldBe(second.Checksum);
        first.Standings.Select(s => s.AthleteId).ShouldBe(second.Standings.Select(s => s.AthleteId).ToList());
        first.Standings.Select(s => s.CupRank).ShouldBe(second.Standings.Select(s => s.CupRank).ToList());
    }

    private static async Task AssertColorTeamEqualAsync(
        SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave, int sourceSeason)
    {
        GetColorCupTeamResultHandler firstQuery = new(firstStore);
        GetColorCupTeamResultHandler secondQuery = new(secondStore);
        GetColorCupTeamResultResponse first =
            await firstQuery.HandleAsync(firstSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        GetColorCupTeamResultResponse second =
            await secondQuery.HandleAsync(secondSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        first.Checksum.ShouldBe(second.Checksum);
        first.Teams.Select(t => t.SportingColor).ShouldBe(second.Teams.Select(t => t.SportingColor).ToList());
        first.Teams.Select(t => t.TeamRank).ShouldBe(second.Teams.Select(t => t.TeamRank).ToList());
    }

    private static async Task AssertTypeTeamEqualAsync(
        SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave, int sourceSeason)
    {
        GetTypeCupTeamResultHandler firstQuery = new(firstStore);
        GetTypeCupTeamResultHandler secondQuery = new(secondStore);
        GetTypeCupTeamResultResponse first =
            await firstQuery.HandleAsync(firstSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        GetTypeCupTeamResultResponse second =
            await secondQuery.HandleAsync(secondSave, sourceSeasonNumber: sourceSeason).ConfigureAwait(false);
        first.Checksum.ShouldBe(second.Checksum);
        first.Teams.Select(t => t.CreatureType).ShouldBe(second.Teams.Select(t => t.CreatureType).ToList());
    }

    private static async Task AssertHonoursEqualAsync(SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave)
    {
        ListHonoursHandler firstQuery = new(firstStore);
        ListHonoursHandler secondQuery = new(secondStore);
        ListHonoursResponse first = await firstQuery.HandleAsync(firstSave).ConfigureAwait(false);
        ListHonoursResponse second = await secondQuery.HandleAsync(secondSave).ConfigureAwait(false);
        first.Honours
            .OrderBy(h => h.SeasonNumber)
            .ThenBy(h => h.HonourKind, StringComparer.Ordinal)
            .ThenBy(h => h.AthleteId)
            .Select(h => $"{h.SeasonNumber}:{h.HonourKind}:{h.AthleteId}")
            .ShouldBe(second.Honours
                .OrderBy(h => h.SeasonNumber)
                .ThenBy(h => h.HonourKind, StringComparer.Ordinal)
                .ThenBy(h => h.AthleteId)
                .Select(h => $"{h.SeasonNumber}:{h.HonourKind}:{h.AthleteId}")
                .ToList());
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cup-lifecycle-" + Guid.NewGuid().ToString("N"));
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
