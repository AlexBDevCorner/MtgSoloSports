import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { SeasonProgress, SeasonStatus } from '../dashboard/dashboardApi.ts';
import { seasonFlow } from '../dashboard/seasonFlowSteps.ts';
import type { QualifierEvent, QualifierList } from './qualifierApi.ts';
import { buildQualifierOverview, shouldShowQualifierRun } from './qualifierModel.ts';

function tieredProgress(): SeasonProgress {
  const leagues: SeasonProgress['leagues'] = [];
  let id = 1;
  leagues.push({
    leagueId: id++,
    leagueName: 'Superleague',
    leagueKind: 'Superleague',
    currentStage: 33,
    completedStages: 32,
    isLeagueComplete: true,
  });
  for (let color = 0; color < 8; color += 1) {
    leagues.push({
      leagueId: id++,
      leagueName: `F1 ${color}`,
      leagueKind: 'Feeder',
      feederDivision: 1,
      leagueLevel: 'Feeder1',
      currentStage: 33,
      completedStages: 32,
      isLeagueComplete: true,
    });
    leagues.push({
      leagueId: id++,
      leagueName: `F2 ${color}`,
      leagueKind: 'Feeder',
      feederDivision: 2,
      leagueLevel: 'Feeder2',
      currentStage: 33,
      completedStages: 32,
      isLeagueComplete: true,
    });
    leagues.push({
      leagueId: id++,
      leagueName: `F3 ${color}`,
      leagueKind: 'Feeder',
      feederDivision: 3,
      leagueLevel: 'Feeder3',
      currentStage: 33,
      completedStages: 32,
      isLeagueComplete: true,
    });
  }
  return { saveId: 'save', seasonNumber: 3, globalStage: 33, isSeasonComplete: true, leagues };
}

function qualifierStatus(
  overrides: Partial<SeasonStatus>,
  eventProgress: SeasonStatus['eventProgress'],
): SeasonStatus {
  return {
    saveId: 'save',
    currentSeasonNumber: 3,
    persistedPhase: 'AutomaticMovementResolved',
    computedPhase: 'AutomaticMovementResolved',
    sourceSeasonNumber: 3,
    nextSeasonNumber: 4,
    isInauguralTransition: false,
    globalStage: 33,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    movementResolved: true,
    qualifierResolved: false,
    rebalanced: false,
    cupSelectionResolved: false,
    cupIndividualResolved: false,
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    expectedCup: 'ColorCup',
    legalNextActions: ['RunQualifier'],
    nextActionDetail: 'Run qualifiers.',
    eventProgress,
    ...overrides,
  };
}

function qualifierEvent(boundary: string, color: number | null, size: number): QualifierEvent {
  return {
    saveId: 'save',
    fromSeasonNumber: 3,
    toSeasonNumber: 4,
    boundaryId: boundary === 'Superleague' ? 0 : 1,
    boundary,
    sportingColor: color,
    sportingColorName: color === null ? '-' : `Color ${color}`,
    qualifierSize: size,
    roundCount: 16,
    winners: 8,
    checksum: 'abc',
    standings: [],
  };
}

function fullList(): QualifierList {
  const events: QualifierEvent[] = [qualifierEvent('Superleague', null, 32)];
  for (let color = 0; color < 8; color += 1) {
    events.push(qualifierEvent('Feeder1Feeder2', color, 16));
    events.push(qualifierEvent('Feeder2Feeder3', color, 16));
  }
  return { saveId: 'save', fromSeasonNumber: 3, toSeasonNumber: 4, events };
}

describe('MSS-075 qualifier dashboard agreement', () => {
  it('renders the same Season 3 full completion on both pages without hardcoded totals', () => {
    const status = qualifierStatus(
      {},
      {
        event: 'qualifier',
        sourceSeasonNumber: 3,
        roundsPlayed: 272,
        totalRounds: 272,
        groupCount: 1,
        roundsPerGroup: 272,
        group: null,
        roundInGroup: null,
        completedEvents: 17,
        totalEvents: 17,
      },
    );
    const flow = seasonFlow(tieredProgress(), status);
    const qualifierStep = flow.steps.find((step) => step.key === 'qualifier')!;
    assert.equal(qualifierStep.detail, '17/17 qualifiers · 272/272 total rounds');
    assert.equal(flow.next?.label, 'Run 17 qualifiers');

    const overview = buildQualifierOverview(fullList(), true);
    const completed = overview.filter((entry) => entry.status === 'complete').length;
    assert.equal(completed, 17);
    assert.equal(overview.length, 17);
  });

  it('represents partial completion identically (2/17, 32/272)', () => {
    const status = qualifierStatus(
      {},
      {
        event: 'qualifier',
        sourceSeasonNumber: 3,
        roundsPlayed: 32,
        totalRounds: 272,
        groupCount: 1,
        roundsPerGroup: 272,
        group: null,
        roundInGroup: null,
        completedEvents: 2,
        totalEvents: 17,
      },
    );
    const flow = seasonFlow(tieredProgress(), status);
    assert.equal(flow.steps.find((s) => s.key === 'qualifier')?.detail, '2/17 qualifiers · 32/272 total rounds');

    const partial = buildQualifierOverview(
      {
        saveId: 'save',
        fromSeasonNumber: 3,
        toSeasonNumber: 4,
        events: [qualifierEvent('Superleague', null, 32), qualifierEvent('Feeder1Feeder2', 0, 16)],
      },
      true,
    );
    assert.equal(partial.filter((e) => e.status === 'complete').length, 2);
  });

  it('hides completed run controls and surfaces the next step once qualifiers finish', () => {
    const overview = buildQualifierOverview(fullList(), true);
    const completed = overview.filter((e) => e.status === 'complete').length;
    assert.equal(shouldShowQualifierRun(true, completed, overview.length), false);
    assert.equal(shouldShowQualifierRun(true, 5, overview.length), true);
    assert.equal(shouldShowQualifierRun(false, completed, overview.length), false);

    const doneStatus = qualifierStatus(
      {
        qualifierResolved: true,
        computedPhase: 'QualifierResolved',
        legalNextActions: ['RebalanceFeeders'],
        eventProgress: null,
      },
      null,
    );
    const flow = seasonFlow(tieredProgress(), doneStatus);
    assert.equal(flow.steps.find((s) => s.key === 'qualifier')?.state, 'done');
    assert.equal(flow.steps.find((s) => s.key === 'rebalance')?.state, 'current');
    assert.equal(flow.next?.label, 'Rebalance & reveal feeders');
  });

  it('supports single-feeder saves without hardcoded 17/272', () => {
    const status = qualifierStatus(
      { sourceSeasonNumber: 2, nextSeasonNumber: 3 },
      {
        event: 'qualifier',
        sourceSeasonNumber: 2,
        roundsPlayed: 5,
        totalRounds: 16,
        groupCount: 1,
        roundsPerGroup: 16,
        group: null,
        roundInGroup: null,
        completedEvents: 0,
        totalEvents: 1,
      },
    );
    const progress: SeasonProgress = { saveId: 'save', seasonNumber: 2, globalStage: 33, isSeasonComplete: true, leagues: [] };
    const flow = seasonFlow(progress, status);
    assert.equal(flow.steps.find((s) => s.key === 'qualifier')?.detail, '0/1 qualifiers · 5/16 total rounds');
    assert.equal(flow.next?.label, 'Run qualifier');
  });

  it('never marks standings without full rounds as complete', () => {
    const overview = buildQualifierOverview(
      {
        saveId: 'save',
        fromSeasonNumber: 3,
        toSeasonNumber: 4,
        events: [
          {
            ...qualifierEvent('Superleague', null, 32),
            roundCount: 0,
          },
        ],
      },
      true,
    );
    const sl = overview.find((e) => e.key === 'Superleague:-')!;
    assert.equal(sl.status, 'pending');
  });
});
