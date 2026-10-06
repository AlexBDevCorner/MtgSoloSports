using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Features.Superleague.GetInauguralRoster;
using MtgSoloSports.Features.Superleague.GetQualifierResult;
using MtgSoloSports.Features.Superleague.GetRebalanceResult;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

public sealed class SeasonLifecycleTests
{
    [Fact]
    public async Task AdvanceToNextEvent_SingleStage_EquivalentToBulk()
    {
        var (firstStore, firstRoot) = CreateStore();
        var (secondStore, secondRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord first = await firstStore.CreateAsync("Lifecycle Equiv A", 4242UL, 777UL, catalog);
            SaveStore.CreationRecord second = await secondStore.CreateAsync("Lifecycle Equiv B", 4242UL, 777UL, catalog);

            AdvanceToNextEventHandler advance = new(firstStore);
            AdvanceToNextEventResponse stepped = await advance.HandleAsync(first.Detail.SaveId);
            stepped.ExecutedAction.ShouldBe(SeasonLifecycleActions.CompleteNextGlobalStage);

            CompleteStageForAllLeaguesHandler bulk = new(secondStore);
            CompleteStageForAllLeaguesResponse bulkResponse = await bulk.HandleAsync(second.Detail.SaveId);
            bulkResponse.CompletedStage.ShouldBe(1);

            await AssertSingleStageEquivalentAsync(firstStore, secondStore, first.Detail.SaveId, second.Detail.SaveId, bulkResponse);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SeasonLifecycle_RejectsIllegalTransitions_AndExposesStatus()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Lifecycle Illegal", 111UL, 222UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await AssertInitialStatusAsync(store, saveId);
            await CompleteSeasonOneViaBulkAsync(store, saveId);
            await AssertSeasonCompleteStatusAsync(store, saveId);
            await AssertInauguralSequenceAsync(store, saveId);
            await AssertBonusAgingAsync(store, saveId, fromSeason: 1, toSeason: 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdvanceToNextEvent_FullLifecycle_Season1_To_Season3_Start()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Lifecycle Full", 707UL, 808UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await RunSeasonOneToSeasonTwoAsync(store, saveId);
            await RunSeasonTwoToSeasonThreeAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertSingleStageEquivalentAsync(
        SaveStore firstStore,
        SaveStore secondStore,
        Guid firstSave,
        Guid secondSave,
        CompleteStageForAllLeaguesResponse bulkResponse)
    {
        ulong steppedRng = await LoadRngStateAsync(firstStore, firstSave).ConfigureAwait(false);
        ulong bulkRng = await LoadRngStateAsync(secondStore, secondSave).ConfigureAwait(false);
        steppedRng.ShouldBe(bulkRng);

        List<string> steppedOrder = await LoadStageOrderAsync(firstStore, firstSave, 1).ConfigureAwait(false);
        List<string> bulkOrder = await LoadStageOrderAsync(secondStore, secondSave, 1).ConfigureAwait(false);
        steppedOrder.ShouldBe(bulkOrder);

        GetSeasonStatusHandler status = new(firstStore);
        GetSeasonStatusResponse after = await status.HandleAsync(firstSave).ConfigureAwait(false);
        after.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonInProgress));
        after.LegalNextActions.ShouldBe([SeasonLifecycleActions.CompleteNextGlobalStage]);
        after.GlobalStage.ShouldBe(2);
    }

    private static async Task AssertInitialStatusAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        StartNextSeasonHandler starter = new(store);
        GetSeasonStatusResponse initial = await status.HandleAsync(saveId).ConfigureAwait(false);
        initial.CurrentSeasonNumber.ShouldBe(1);
        initial.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonInProgress));
        initial.LegalNextActions.ShouldBe([SeasonLifecycleActions.CompleteNextGlobalStage]);
        initial.ReadyToStartNextSeason.ShouldBeFalse();
        await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId)).ConfigureAwait(false);
    }

    private static async Task AssertSeasonCompleteStatusAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        StartNextSeasonHandler starter = new(store);
        GetSeasonStatusResponse completed = await status.HandleAsync(saveId).ConfigureAwait(false);
        completed.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonComplete));
        completed.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveInauguralMovement]);
        completed.IsInauguralTransition.ShouldBeTrue();
        completed.ExpectedCup.ShouldBe(CupExtensionPoint.ColorCup);
        completed.PersistedPhase.ShouldBe(completed.ComputedPhase);
        await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId)).ConfigureAwait(false);
    }

    private static async Task AssertInauguralSequenceAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        StartNextSeasonHandler starter = new(store);
        AdvanceToNextEventHandler advance = new(store);

        AdvanceToNextEventResponse inaugural = await advance.HandleAsync(saveId).ConfigureAwait(false);
        inaugural.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveInauguralMovement);

        GetSeasonStatusResponse afterInaugural = await status.HandleAsync(saveId).ConfigureAwait(false);
        afterInaugural.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.InauguralMovementResolved));
        afterInaugural.LegalNextActions.ShouldBe([SeasonLifecycleActions.RebalanceFeeders]);
        await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId)).ConfigureAwait(false);

        AdvanceToNextEventResponse rebalanced = await advance.HandleAsync(saveId).ConfigureAwait(false);
        rebalanced.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);

        // Post-season Color Cup for Season 1 is an explicit Next Event chain:
        // selection, individual, team, then start. Starting early must conflict.
        GetSeasonStatusResponse afterRebalance = await status.HandleAsync(saveId).ConfigureAwait(false);
        afterRebalance.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.Rebalanced));
        afterRebalance.LegalNextActions.ShouldBe([SeasonLifecycleActions.SelectColorCup]);
        afterRebalance.CupSelectionResolved.ShouldBeFalse();
        await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId)).ConfigureAwait(false);

        AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectColorCup);

        AdvanceToNextEventResponse individual = await advance.HandleAsync(saveId).ConfigureAwait(false);
        individual.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupIndividual);

        AdvanceToNextEventResponse team = await advance.HandleAsync(saveId).ConfigureAwait(false);
        team.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupTeam);

        await AssertReadyToStartAsync(store, saveId).ConfigureAwait(false);
        await AssertRosterValidAsync(store, saveId, 2).ConfigureAwait(false);

        StartNextSeasonResponse started = await starter.HandleAsync(saveId).ConfigureAwait(false);
        started.FromSeasonNumber.ShouldBe(1);
        started.ToSeasonNumber.ShouldBe(2);
        started.BonusDecayWeightsThousandths.ShouldBe([1000, 800, 600, 400, 200, 0]);

        await AssertSeasonTwoStartedAsync(store, saveId).ConfigureAwait(false);
    }

    private static async Task AssertReadyToStartAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse ready = await status.HandleAsync(saveId).ConfigureAwait(false);
        ready.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupComplete));
        ready.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);
        ready.ReadyToStartNextSeason.ShouldBeTrue();
        ready.Rebalanced.ShouldBeTrue();
        ready.CupSelectionResolved.ShouldBeTrue();
        ready.CupTeamResolved.ShouldBeTrue();
        ready.CupComplete.ShouldBeTrue();
        ready.ExpectedCup.ShouldBe(CupExtensionPoint.ColorCup);
    }

    private static async Task AssertSeasonTwoStartedAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        StartNextSeasonHandler starter = new(store);
        GetSeasonStatusResponse seasonTwo = await status.HandleAsync(saveId).ConfigureAwait(false);
        seasonTwo.CurrentSeasonNumber.ShouldBe(2);
        seasonTwo.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonInProgress));
        seasonTwo.LegalNextActions.ShouldBe([SeasonLifecycleActions.CompleteNextGlobalStage]);
        seasonTwo.GlobalStage.ShouldBe(1);
        await Should.ThrowAsync<StartNextSeasonConflictException>(() => starter.HandleAsync(saveId)).ConfigureAwait(false);
    }

    private static async Task RunSeasonOneToSeasonTwoAsync(SaveStore store, Guid saveId)
    {
        AdvanceToNextEventHandler advance = new(store);
        GetSeasonStatusHandler status = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            AdvanceToNextEventResponse stepped = await advance.HandleAsync(saveId).ConfigureAwait(false);
            stepped.ExecutedAction.ShouldBe(SeasonLifecycleActions.CompleteNextGlobalStage);
        }

        GetSeasonStatusResponse seasonComplete = await status.HandleAsync(saveId).ConfigureAwait(false);
        seasonComplete.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonComplete));

        AdvanceToNextEventResponse inaugural = await advance.HandleAsync(saveId).ConfigureAwait(false);
        inaugural.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveInauguralMovement);
        await AssertInauguralInspectableAsync(store, saveId, inaugural).ConfigureAwait(false);

        AdvanceToNextEventResponse rebalanceOne = await advance.HandleAsync(saveId).ConfigureAwait(false);
        rebalanceOne.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);
        await AssertRebalanceInspectableAsync(store, saveId, fromSeason: 1, expectedToSeason: 2).ConfigureAwait(false);
        await AssertRosterValidAsync(store, saveId, 2).ConfigureAwait(false);

        AdvanceToNextEventResponse selectOne = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selectOne.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectColorCup);

        AdvanceToNextEventResponse individualOne = await advance.HandleAsync(saveId).ConfigureAwait(false);
        individualOne.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupIndividual);

        AdvanceToNextEventResponse teamOne = await advance.HandleAsync(saveId).ConfigureAwait(false);
        teamOne.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunColorCupTeam);

        AdvanceToNextEventResponse startTwo = await advance.HandleAsync(saveId).ConfigureAwait(false);
        startTwo.ExecutedAction.ShouldBe(SeasonLifecycleActions.StartNextSeason);
        startTwo.CurrentSeasonNumber.ShouldBe(2);
        await AssertBonusAgingAsync(store, saveId, fromSeason: 1, toSeason: 2).ConfigureAwait(false);
    }

    private static async Task RunSeasonTwoToSeasonThreeAsync(SaveStore store, Guid saveId)
    {
        AdvanceToNextEventHandler advance = new(store);
        GetSeasonStatusHandler status = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            AdvanceToNextEventResponse stepped = await advance.HandleAsync(saveId).ConfigureAwait(false);
            stepped.ExecutedAction.ShouldBe(SeasonLifecycleActions.CompleteNextGlobalStage);
            stepped.CurrentSeasonNumber.ShouldBe(2);
        }

        GetSeasonStatusResponse seasonTwoComplete = await status.HandleAsync(saveId).ConfigureAwait(false);
        seasonTwoComplete.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonComplete));
        seasonTwoComplete.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveAutomaticMovement]);
        seasonTwoComplete.ExpectedCup.ShouldBe(CupExtensionPoint.TypeCup);

        AdvanceToNextEventResponse automatic = await advance.HandleAsync(saveId).ConfigureAwait(false);
        automatic.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveAutomaticMovement);
        await AssertAutomaticInspectableAsync(store, saveId, automatic).ConfigureAwait(false);

        AdvanceToNextEventResponse qualifier = await advance.HandleAsync(saveId).ConfigureAwait(false);
        qualifier.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunQualifier);
        await AssertQualifierInspectableAsync(store, saveId, fromSeason: 2, expectedToSeason: 3).ConfigureAwait(false);

        AdvanceToNextEventResponse rebalanceTwo = await advance.HandleAsync(saveId).ConfigureAwait(false);
        rebalanceTwo.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);
        await AssertRebalanceInspectableAsync(store, saveId, fromSeason: 2, expectedToSeason: 3).ConfigureAwait(false);
        await AssertRosterValidAsync(store, saveId, 3).ConfigureAwait(false);
        await AssertNormalSuperCompositionAsync(store, saveId, fromSeason: 2, toSeason: 3).ConfigureAwait(false);

        AdvanceToNextEventResponse selectTwo = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selectTwo.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectTypeCup);

        AdvanceToNextEventResponse teamTwo = await advance.HandleAsync(saveId).ConfigureAwait(false);
        teamTwo.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunTypeCupTeam);

        AdvanceToNextEventResponse startThree = await advance.HandleAsync(saveId).ConfigureAwait(false);
        startThree.ExecutedAction.ShouldBe(SeasonLifecycleActions.StartNextSeason);
        startThree.CurrentSeasonNumber.ShouldBe(3);

        await AssertSeasonThreeStartedAsync(store, saveId).ConfigureAwait(false);
        await AssertBonusAgingAsync(store, saveId, fromSeason: 2, toSeason: 3).ConfigureAwait(false);
    }

    private static async Task AssertSeasonThreeStartedAsync(SaveStore store, Guid saveId)
    {
        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse seasonThree = await status.HandleAsync(saveId).ConfigureAwait(false);
        seasonThree.CurrentSeasonNumber.ShouldBe(3);
        seasonThree.GlobalStage.ShouldBe(1);
        seasonThree.LegalNextActions.ShouldBe([SeasonLifecycleActions.CompleteNextGlobalStage]);
        await AssertRosterValidAsync(store, saveId, 3).ConfigureAwait(false);
    }

    private static async Task CompleteSeasonOneViaBulkAsync(SaveStore store, Guid saveId)
    {
        CompleteStageForAllLeaguesHandler bulk = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            CompleteStageForAllLeaguesResponse completed = await bulk.HandleAsync(saveId).ConfigureAwait(false);
            completed.CompletedStage.ShouldBe(stage);
        }
    }

    private static async Task<ulong> LoadRngStateAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (ulong)rng.State;
        }
    }

    private static async Task<List<string>> LoadStageOrderAsync(SaveStore store, Guid saveId, int stageNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        LeagueEntity firstLeague = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == season.Id).OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        List<StageStandingEntity> standings = await context.StageStandings.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == firstLeague.Id && e.StageNumber == stageNumber)
            .ToListAsync().ConfigureAwait(false);
        standings.Count.ShouldBe(32);
        Dictionary<int, string> names = await context.SaveAthletes.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name).ConfigureAwait(false);
        return standings.OrderBy(s => s.StageRank).Select(s => names[s.SaveAthleteId]).ToList();
    }

    private static async Task AssertRosterValidAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RulesV1 rules = RulesV1.CreateDefault();
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == season.Id).ToListAsync().ConfigureAwait(false);
        leagues.Count.ShouldBe(25);
        LeagueEntity superleague = leagues.Single(l => l.Kind == (int)LeagueKind.Superleague);
        foreach (LeagueEntity league in leagues)
        {
            int count = await context.SeasonMemberships.CountAsync(e => e.SeasonId == season.Id && e.LeagueId == league.Id).ConfigureAwait(false);
            count.ShouldBe(rules.LeagueSize);
        }

        await AssertMembershipTotalsAsync(context, season, superleague, leagues, rules).ConfigureAwait(false);
    }

    private static async Task AssertMembershipTotalsAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity superleague,
        List<LeagueEntity> leagues,
        RulesV1 rules)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships.AsNoTracking().Where(e => e.SeasonId == season.Id).ToListAsync().ConfigureAwait(false);
        memberships.Count.ShouldBe(rules.TotalAthletesInSave);
        memberships.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(rules.TotalAthletesInSave);
        int active = memberships.Count(m => m.LeagueId is not null);
        active.ShouldBe((24 * 32) + 32);
        Dictionary<int, LeagueEntity> byId = leagues.ToDictionary(l => l.Id);
        foreach (SeasonMembershipEntity membership in memberships.Where(m => m.LeagueId is not null && m.LeagueId != superleague.Id))
        {
            byId[membership.LeagueId!.Value].SportingColor.ShouldBe(membership.SportingColor);
        }
    }

    private static async Task AssertInauguralInspectableAsync(SaveStore store, Guid saveId, AdvanceToNextEventResponse step)
    {
        step.SourceSeasonNumber.ShouldBe(1);
        step.NextSeasonNumber.ShouldBe(2);
        GetInauguralRosterHandler query = new(store);
        GetInauguralRosterResponse roster = await query.HandleAsync(saveId).ConfigureAwait(false);
        roster.Members.Count.ShouldBe(32);
        roster.MovementCount.ShouldBe(32);
    }

    private static async Task AssertAutomaticInspectableAsync(SaveStore store, Guid saveId, AdvanceToNextEventResponse step)
    {
        step.SourceSeasonNumber.ShouldBe(2);
        step.NextSeasonNumber.ShouldBe(3);
        GetAutomaticMovementHandler query = new(store);
        GetAutomaticMovementResponse movement = await query.HandleAsync(saveId).ConfigureAwait(false);
        movement.FromSeasonNumber.ShouldBe(2);
        movement.ToSeasonNumber.ShouldBe(3);
        movement.Safe.Count.ShouldBe(16);
        movement.Promoted.Count.ShouldBe(8);
        movement.MovementCount.ShouldBe(48);
    }

    private static async Task AssertQualifierInspectableAsync(SaveStore store, Guid saveId, int fromSeason, int expectedToSeason)
    {
        GetQualifierResultHandler query = new(store);
        GetQualifierResultResponse qualifier = await query.HandleAsync(saveId).ConfigureAwait(false);
        qualifier.FromSeasonNumber.ShouldBe(fromSeason);
        qualifier.ToSeasonNumber.ShouldBe(expectedToSeason);
        qualifier.Winners.ShouldBe(8);
        qualifier.QualifierSize.ShouldBe(32);
    }

    private static async Task AssertRebalanceInspectableAsync(SaveStore store, Guid saveId, int fromSeason, int expectedToSeason)
    {
        GetRebalanceResultHandler query = new(store);
        GetRebalanceResultResponse rebalance = await query.HandleAsync(saveId, fromSeasonNumber: fromSeason).ConfigureAwait(false);
        rebalance.FromSeasonNumber.ShouldBe(fromSeason);
        rebalance.ToSeasonNumber.ShouldBe(expectedToSeason);
        foreach (var color in rebalance.Colors)
        {
            color.FinalCount.ShouldBe(32);
        }
    }

    private static async Task AssertNormalSuperCompositionAsync(SaveStore store, Guid saveId, int fromSeason, int toSeason)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == fromSeason).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == toSeason).ConfigureAwait(false);
        LeagueEntity sourceSuper = await context.Leagues.AsNoTracking()
            .SingleAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague).ConfigureAwait(false);
        LeagueEntity nextSuper = await context.Leagues.AsNoTracking()
            .SingleAsync(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague).ConfigureAwait(false);

        HashSet<int> safe = (await context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == sourceSuper.Id && e.SeasonRank >= 1 && e.SeasonRank <= 16)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();
        HashSet<int> promoted = (await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();
        HashSet<int> qualified = (await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.IsQualified
                && e.QualifierBoundary == (int)MtgSoloSports.SimulationKernel.Leagues.QualifierBoundary.Superleague)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();
        HashSet<int> super = (await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId == nextSuper.Id)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();

        safe.Count.ShouldBe(16);
        promoted.Count.ShouldBe(8);
        qualified.Count.ShouldBe(8);
        super.Count.ShouldBe(32);
        HashSet<int> expected = safe.Union(promoted).Union(qualified).ToHashSet();
        super.SetEquals(expected).ShouldBeTrue();
    }

    private static async Task AssertBonusAgingAsync(SaveStore store, Guid saveId, int fromSeason, int toSeason)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RulesV1 rules = RulesV1.CreateDefault();
        rules.BonusAgeWeightsThousandths.ShouldBe([1000, 800, 600, 400, 200, 0]);

        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == fromSeason).ConfigureAwait(false);
        List<StageStandingEntity> stage32 = await context.StageStandings.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.StageNumber == rules.StagesPerSeason && e.EarnedBonusThousandths > 0)
            .Take(5).ToListAsync().ConfigureAwait(false);
        stage32.Count.ShouldBeGreaterThan(0);

        foreach (StageStandingEntity row in stage32)
        {
            List<BonusContribution> single = [new BonusContribution(fromSeason, rules.StagesPerSeason, Bonus.FromThousandths(row.EarnedBonusThousandths))];
            int decayed = BonusCalculator.EffectiveBonus(single, toSeason, 1, rules).Thousandths;
            int expected = (row.EarnedBonusThousandths * 800) / 1000;
            decayed.ShouldBe(expected);
        }

        await AssertCareerEffectiveAsync(context, stage32[0].SaveAthleteId, toSeason, rules).ConfigureAwait(false);
    }

    private static async Task AssertCareerEffectiveAsync(SaveDbContext context, int athleteId, int toSeason, RulesV1 rules)
    {
        List<StageStandingEntity> allForAthlete = await context.StageStandings.AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync().ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await context.Seasons.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        List<BonusContribution> contributions = allForAthlete
            .Select(r => new BonusContribution(seasonNumbers[r.SeasonId], r.StageNumber, Bonus.FromThousandths(r.EarnedBonusThousandths)))
            .ToList();
        int effective = BonusCalculator.EffectiveBonus(contributions, toSeason, 1, rules).Thousandths;

        AthleteCareerEntity career = await context.AthleteCareers.AsNoTracking()
            .SingleAsync(e => e.SaveAthleteId == athleteId).ConfigureAwait(false);
        career.CurrentEffectiveBonusThousandths.ShouldBe(effective);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-lifecycle-" + Guid.NewGuid().ToString("N"));
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
