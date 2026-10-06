import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';
import { executedSummary, seasonFlow } from './seasonFlowSteps.ts';

function progress(overrides?: Partial<SeasonProgress>): SeasonProgress {
  return {
    saveId: 'save-1',
    seasonNumber: 1,
    globalStage: 12,
    isSeasonComplete: false,
    leagues: [],
    ...overrides,
  };
}

function status(overrides?: Partial<SeasonStatus>): SeasonStatus {
  return {
    saveId: 'save-1',
    currentSeasonNumber: 1,
    persistedPhase: 'SeasonInProgress',
    computedPhase: 'SeasonInProgress',
    sourceSeasonNumber: null,
    nextSeasonNumber: null,
    isInauguralTransition: false,
    globalStage: 12,
    isCurrentSeasonComplete: false,
    seasonComplete: false,
    movementResolved: false,
    qualifierResolved: false,
    rebalanced: false,
    cupSelectionResolved: false,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'ColorCup',
    legalNextActions: ['CompleteNextGlobalStage'],
    nextActionDetail: 'Backend detail.',
    eventProgress: null,
    ...overrides,
  };
}

/** Season 1 after league play: the inaugural Superleague transition. */
function inaugural(overrides?: Partial<SeasonStatus>): SeasonStatus {
  return status({
    computedPhase: 'SeasonComplete',
    sourceSeasonNumber: 1,
    nextSeasonNumber: 2,
    isInauguralTransition: true,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    legalNextActions: ['ResolveInauguralMovement'],
    ...overrides,
  });
}

function labels(flow: ReturnType<typeof seasonFlow>): string[] {
  return flow.steps.map((step) => `${step.label}:${step.state}`);
}

describe('season flow steps', () => {
  it('shows league play as current with the stage while the season runs', () => {
    const flow = seasonFlow(progress(), status());
    assert.equal(flow.seasonNumber, 1);
    assert.deepEqual(flow.steps[0], { key: 'league', label: 'League play', detail: 'Stage 12/32', state: 'current' });
    assert.ok(flow.steps.slice(1).every((step) => step.state === 'upcoming'));
    assert.deepEqual(flow.next, {
      kind: 'live',
      label: 'Play stage 12',
      explanation: 'Run rounds for every league on Live, or fast-forward the rest of the league season.',
      globalStage: 12,
      leagueCount: 0,
    });
  });

  it('builds the inaugural path without a qualifier and with the Color Cup individual event', () => {
    const flow = seasonFlow(progress({ isSeasonComplete: true, globalStage: 33 }), inaugural());
    assert.deepEqual(labels(flow), [
      'League play:done',
      'Form Superleague:current',
      'Rebalance feeders:upcoming',
      'Color Cup squads:upcoming',
      'Cup: individual:upcoming',
      'Cup: team:upcoming',
      'Start Season 2:upcoming',
    ]);
    assert.equal(flow.next?.kind, 'event');
    assert.equal(flow.next?.label, 'Form Superleague & reveal');
    assert.equal(flow.next?.kind === 'event' ? flow.next.action : null, 'ResolveInauguralMovement');
  });

  it('includes the qualifier and Type Cup team-only path in later even seasons', () => {
    const flow = seasonFlow(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        expectedCup: 'TypeCup',
        legalNextActions: ['RunQualifier'],
      }),
    );
    assert.deepEqual(labels(flow), [
      'League play:done',
      'Promotion & relegation:done',
      'Qualifier:current',
      'Rebalance feeders:upcoming',
      'Type Cup squads:upcoming',
      'Cup: team:upcoming',
      'Start Season 3:upcoming',
    ]);
    assert.equal(flow.next?.label, 'Run qualifier');
  });

  it('marks the start of the next season as the final current step', () => {
    const flow = seasonFlow(
      progress({ isSeasonComplete: true }),
      inaugural({
        movementResolved: true,
        rebalanced: true,
        cupSelectionResolved: true,
        cupIndividualResolved: true,
        cupTeamResolved: true,
        cupComplete: true,
        readyToStartNextSeason: true,
        legalNextActions: ['StartNextSeason'],
      }),
    );
    assert.equal(flow.steps.at(-1)?.state, 'current');
    assert.ok(flow.steps.slice(0, -1).every((step) => step.state === 'done'));
    assert.equal(flow.next?.label, 'Start Season 2');
  });

  it('labels every lifecycle action in plain words', () => {
    const cases: [string, Partial<SeasonStatus>, string][] = [
      ['ResolveAutomaticMovement', { sourceSeasonNumber: 3 }, 'Resolve & reveal promotion'],
      ['RebalanceFeeders', {}, 'Rebalance & reveal feeders'],
      ['SelectColorCup', {}, 'Select Color Cup squads'],
      ['RunColorCupIndividual', {}, 'Run Color Cup — individual'],
      ['RunColorCupTeam', {}, 'Run Color Cup — team'],
      ['SelectTypeCup', { expectedCup: 'TypeCup' }, 'Select Type Cup squads'],
      ['RunTypeCupTeam', { expectedCup: 'TypeCup' }, 'Run Type Cup — team'],
    ];
    for (const [action, overrides, label] of cases) {
      const flow = seasonFlow(progress({ isSeasonComplete: true }), inaugural({ ...overrides, legalNextActions: [action] }));
      assert.equal(flow.next?.label, label, action);
      assert.ok(flow.next && flow.next.explanation.length > 0, `${action} explains itself`);
      assert.ok(!flow.next?.explanation.includes('/api/'), `${action} hides backend routes`);
    }
  });

  it('falls back to the backend detail for an unknown action', () => {
    const flow = seasonFlow(progress({ isSeasonComplete: true }), inaugural({ legalNextActions: ['SomethingNew'] }));
    assert.deepEqual(flow.next, {
      kind: 'event',
      action: 'SomethingNew',
      label: 'Run next event',
      explanation: 'Backend detail.',
      liveEvent: null,
      liveSelection: null,
      liveTransition: null,
    });
  });

  it('shows league play only when lifecycle status is unavailable', () => {
    const running = seasonFlow(progress(), null);
    assert.equal(running.steps[0].state, 'current');
    assert.equal(running.next?.kind, 'live');
    const finished = seasonFlow(progress({ isSeasonComplete: true }), null);
    assert.equal(finished.steps[0].state, 'done');
    assert.equal(finished.next, null);
  });
});

describe('round-based events', () => {
  it('sends round-based events to Live with progress detail', () => {
    const flow = seasonFlow(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        expectedCup: 'TypeCup',
        legalNextActions: ['RunQualifier'],
        eventProgress: {
          event: 'qualifier',
          sourceSeasonNumber: 2,
          roundsPlayed: 5,
          totalRounds: 16,
          groupCount: 1,
          roundsPerGroup: 16,
          group: null,
          roundInGroup: null,
        },
      }),
    );
    assert.equal(flow.next?.kind, 'event');
    assert.equal(flow.next?.kind === 'event' ? flow.next.liveEvent : null, 'qualifier');
    assert.equal(flow.steps.find((s) => s.state === 'current')?.detail, 'Round 5 / 16');
  });

  it('keeps single-step Cup-adjacent events off round-based Live but sends transitions to their reveal', () => {
    const rebalance = seasonFlow(progress({ isSeasonComplete: true }), inaugural({ legalNextActions: ['RebalanceFeeders'] }));
    assert.equal(rebalance.next?.kind === 'event' ? rebalance.next.liveEvent : 'x', null);
    assert.equal(rebalance.next?.kind === 'event' ? rebalance.next.liveSelection : 'x', null);
    assert.equal(rebalance.next?.kind === 'event' ? rebalance.next.liveTransition : null, 'rebalance');
    const movement = seasonFlow(progress({ isSeasonComplete: true }), inaugural({ legalNextActions: ['ResolveInauguralMovement'] }));
    assert.equal(movement.next?.kind === 'event' ? movement.next.liveTransition : null, 'movement');
    const automatic = seasonFlow(
      progress({ isSeasonComplete: true }),
      inaugural({ sourceSeasonNumber: 3, legalNextActions: ['ResolveAutomaticMovement'] }),
    );
    assert.equal(automatic.next?.kind === 'event' ? automatic.next.liveTransition : null, 'movement');
  });

  it('sends the Cup squad selections to Live as events of their own', () => {
    const color = seasonFlow(progress({ isSeasonComplete: true }), inaugural({ legalNextActions: ['SelectColorCup'] }));
    assert.equal(color.next?.kind === 'event' ? color.next.liveSelection : null, 'color-cup-selection');
    assert.equal(color.next?.kind === 'event' ? color.next.liveEvent : 'x', null);
    const type = seasonFlow(
      progress({ isSeasonComplete: true }),
      inaugural({ expectedCup: 'TypeCup', legalNextActions: ['SelectTypeCup'] }),
    );
    assert.equal(type.next?.kind === 'event' ? type.next.liveSelection : null, 'type-cup-selection');
  });
});

describe('executed step summary', () => {
  it('says what happened and where to look', () => {
    assert.deepEqual(executedSummary('RunQualifier', 3), { text: 'Qualifier finished.', target: 'standings' });
    assert.deepEqual(executedSummary('ResolveInauguralMovement', 2), { text: 'Superleague formed.', target: 'standings' });
    assert.deepEqual(executedSummary('SelectColorCup', 2), { text: 'Cup squads selected.', target: 'cups' });
    assert.deepEqual(executedSummary('RunTypeCupTeam', 2), { text: 'Cup team event finished.', target: 'cups' });
    assert.deepEqual(executedSummary('StartNextSeason', 2), { text: 'Season 2 started.', target: 'live' });
    assert.deepEqual(executedSummary('CompleteNextGlobalStage', 2), {
      text: 'Global stage completed for every league.',
      target: 'live',
    });
  });
});

describe('tiered pyramid flow (MSS-060)', () => {
  function tieredProgress(): SeasonProgress {
    const leagues: SeasonProgress['leagues'] = [];
    let id = 1;
    leagues.push({ leagueId: id++, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: 33, completedStages: 32, isLeagueComplete: true });
    for (let color = 0; color < 8; color += 1) {
      leagues.push({ leagueId: id++, leagueName: `Color ${color} F1`, leagueKind: 'Feeder', feederDivision: 1, leagueLevel: 'Feeder1', currentStage: 33, completedStages: 32, isLeagueComplete: true });
      leagues.push({ leagueId: id++, leagueName: `Color ${color} F2`, leagueKind: 'Feeder', feederDivision: 2, leagueLevel: 'Feeder2', currentStage: 33, completedStages: 32, isLeagueComplete: true });
      leagues.push({ leagueId: id++, leagueName: `Color ${color} F3`, leagueKind: 'Feeder', feederDivision: 3, leagueLevel: 'Feeder3', currentStage: 33, completedStages: 32, isLeagueComplete: true });
    }
    return progress({ seasonNumber: 2, isSeasonComplete: true, leagues });
  }

  it('keeps one qualifier step but labels all 17 qualifiers', () => {
    const flow = seasonFlow(
      tieredProgress(),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        legalNextActions: ['RunQualifier'],
      }),
    );
    assert.deepEqual(
      flow.steps.map((step) => step.key),
      ['league', 'movement', 'qualifier', 'rebalance', 'cupField', 'cupIndividual', 'cupTeam', 'nextSeason'],
    );
    assert.equal(flow.next?.label, 'Run 17 qualifiers');
    assert.deepEqual(executedSummary('RunQualifier', 3, true), {
      text: 'All 17 qualifiers finished.',
      target: 'standings',
    });
  });

  it('describes tiered movement and rebalance without extra steps', () => {
    const movement = seasonFlow(
      tieredProgress(),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        legalNextActions: ['ResolveAutomaticMovement'],
      }),
    );
    assert.match(movement.next?.kind === 'event' ? movement.next.explanation : '', /Feeder 1↔Feeder 2/);
    const rebalance = seasonFlow(
      tieredProgress(),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        qualifierResolved: true,
        legalNextActions: ['RebalanceFeeders'],
      }),
    );
    assert.match(rebalance.next?.kind === 'event' ? rebalance.next.explanation : '', /only Feeder 3 touches the common pool/);
  });

  it('offers one-click global stage completion across all leagues', () => {
    const leagues = tieredProgress().leagues.map((league) => ({ ...league, currentStage: 5, completedStages: 4, isLeagueComplete: false }));
    const flow = seasonFlow(
      progress({ seasonNumber: 2, globalStage: 5, leagues }),
      status({ currentSeasonNumber: 2, globalStage: 5, legalNextActions: ['CompleteNextGlobalStage'] }),
    );
    assert.equal(flow.next?.kind, 'live');
    assert.equal(flow.next?.kind === 'live' ? flow.next.leagueCount : 0, 25);
    assert.match(flow.next?.kind === 'live' ? flow.next.explanation : '', /all 25 leagues/);
  });
});
