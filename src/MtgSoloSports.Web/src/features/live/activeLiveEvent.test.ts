import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { activeLivePath, resolveActiveLiveTarget } from './activeLiveEvent.ts';
import type { SeasonProgress, SeasonStatus } from '../dashboard/dashboardApi.ts';
import type { EventProgress } from '../events/eventsApi.ts';

function progress(overrides?: Partial<SeasonProgress>): SeasonProgress {
  return {
    saveId: 'save-1',
    seasonNumber: 2,
    globalStage: 33,
    isSeasonComplete: false,
    leagues: [],
    ...overrides,
  };
}

function status(overrides?: Partial<SeasonStatus>): SeasonStatus {
  return {
    saveId: 'save-1',
    currentSeasonNumber: 2,
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

function eventProgress(event: EventProgress['event'], season: number): EventProgress {
  return {
    event,
    sourceSeasonNumber: season,
    roundsPlayed: 3,
    totalRounds: 16,
    groupCount: 1,
    roundsPerGroup: 16,
    group: null,
    roundInGroup: null,
  };
}

describe('MSS-143 active Live event resolver', () => {
  it('keeps league play on the league Live view', () => {
    const target = resolveActiveLiveTarget(progress({ isSeasonComplete: false }), status());
    assert.deepEqual(target, { kind: 'league' });
    assert.equal(activeLivePath('save-1', target), '/saves/save-1/live');
  });

  it('resolves an in-progress Chaos Cup to the Chaos Live view, not stale Superleague', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        computedPhase: 'ChaosSelectionResolved',
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        seasonComplete: true,
        legalNextActions: ['RunChaosCup'],
        chaosEligible: true,
        chaosSelectionResolved: true,
        chaosComplete: false,
      }),
    );
    assert.deepEqual(target, { kind: 'chaos', season: 2 });
    assert.equal(activeLivePath('save-1', target), '/saves/save-1/live?event=chaos-cup&season=2');
  });

  it('moves to the Dashboard CTA after Chaos completes (post-Chaos transition)', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        computedPhase: 'ChaosComplete',
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        seasonComplete: true,
        legalNextActions: ['ResolveAutomaticMovement'],
        chaosEligible: true,
        chaosSelectionResolved: true,
        chaosComplete: true,
      }),
    );
    assert.equal(target.kind, 'dashboard');
    // The sidebar stays on Live; the Live page renders the Dashboard CTA.
    assert.equal(activeLivePath('save-1', target), '/saves/save-1/live');
  });

  it('shows a Dashboard CTA (not stale league) for Chaos field selection setup', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        seasonComplete: true,
        legalNextActions: ['SelectChaosCup'],
        chaosEligible: true,
        chaosSelectionResolved: false,
        chaosComplete: false,
      }),
    );
    assert.equal(target.kind, 'dashboard');
    assert.equal(activeLivePath('save-1', target), '/saves/save-1/live');
  });

  it('resolves a supported non-Chaos postseason event (Color Cup individual)', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 3, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 3,
        nextSeasonNumber: 4,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        qualifierResolved: true,
        rebalanced: true,
        cupSelectionResolved: true,
        legalNextActions: ['RunColorCupIndividual'],
        eventProgress: eventProgress('color-cup-individual', 3),
      }),
    );
    assert.deepEqual(target, { kind: 'event', event: 'color-cup-individual', season: 3 });
    assert.equal(
      activeLivePath('save-1', target),
      '/saves/save-1/live?event=color-cup-individual&season=3',
    );
  });

  it('resolves the qualifier postseason event without stale Superleague league play', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        legalNextActions: ['RunQualifier'],
        eventProgress: eventProgress('qualifier', 2),
      }),
    );
    assert.deepEqual(target, { kind: 'event', event: 'qualifier', season: 2 });
  });

  it('resolves Cup squad selection to the selection Live view', () => {
    const target = resolveActiveLiveTarget(
      progress({ seasonNumber: 3, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 3,
        nextSeasonNumber: 4,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        qualifierResolved: true,
        rebalanced: true,
        legalNextActions: ['SelectColorCup'],
      }),
    );
    assert.deepEqual(target, { kind: 'selection', selection: 'color-cup-selection', season: 3 });
  });

  it('falls back to the Dashboard CTA for transition and season-start setup phases', () => {
    const rebalance = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        movementResolved: true,
        qualifierResolved: true,
        legalNextActions: ['RebalanceFeeders'],
      }),
    );
    assert.equal(rebalance.kind, 'dashboard');
    const start = resolveActiveLiveTarget(
      progress({ seasonNumber: 2, isSeasonComplete: true }),
      status({
        sourceSeasonNumber: 2,
        nextSeasonNumber: 3,
        isCurrentSeasonComplete: true,
        seasonComplete: true,
        cupComplete: true,
        readyToStartNextSeason: true,
        legalNextActions: ['StartNextSeason'],
      }),
    );
    assert.equal(start.kind, 'dashboard');
  });

  it('never shows league play as the active event once the season is complete without status', () => {
    const target = resolveActiveLiveTarget(progress({ isSeasonComplete: true }), null);
    assert.equal(target.kind, 'dashboard');
    const running = resolveActiveLiveTarget(progress({ isSeasonComplete: false }), null);
    assert.deepEqual(running, { kind: 'league' });
  });
});
