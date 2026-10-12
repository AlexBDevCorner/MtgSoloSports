import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { SeasonProgress, SeasonStatus } from './dashboardApi.ts';
import { seasonFlow } from './seasonFlowSteps.ts';

function progress(): SeasonProgress {
  return {
    saveId: 'save-1',
    seasonNumber: 4,
    globalStage: 33,
    isSeasonComplete: true,
    leagues: [
      { leagueId: 1, leagueName: 'Superleague', leagueKind: 'Superleague', currentStage: null, completedStages: 32, isLeagueComplete: true },
    ],
  };
}

function completedStatus(): SeasonStatus {
  return {
    saveId: 'save-1',
    currentSeasonNumber: 4,
    persistedPhase: 'CupComplete',
    computedPhase: 'CupComplete',
    sourceSeasonNumber: 4,
    nextSeasonNumber: 5,
    isInauguralTransition: false,
    globalStage: 33,
    isCurrentSeasonComplete: true,
    seasonComplete: true,
    movementResolved: true,
    qualifierResolved: true,
    rebalanced: true,
    cupSelectionResolved: true,
    cupIndividualResolved: false,
    cupTeamResolved: true,
    cupComplete: true,
    readyToStartNextSeason: true,
    expectedCup: 'TypeCup',
    legalNextActions: ['StartNextSeason'],
    nextActionDetail: 'Post-season TypeCup for Season 4 is complete.',
    eventProgress: null,
  };
}

function pendingStatus(): SeasonStatus {
  return {
    ...completedStatus(),
    persistedPhase: 'CupSelectionResolved',
    computedPhase: 'TypeCupTeamInProgress',
    cupTeamResolved: false,
    cupComplete: false,
    readyToStartNextSeason: false,
    legalNextActions: ['RunTypeCupTeam'],
    nextActionDetail: 'Type Cup field is allocated.',
    eventProgress: {
      event: 'type-cup-team',
      sourceSeasonNumber: 4,
      roundsPlayed: 32,
      totalRounds: 128,
      groupCount: 4,
      roundsPerGroup: 8,
      group: 4,
      roundInGroup: 8,
      tournamentPhase: 1,
      qualificationGroup: 1,
      qualificationGroupCount: 3,
      tournamentStage: 'Qualification Group 1 of 3',
    },
  };
}

describe('MSS-079 Type Cup Dashboard completion', () => {
  it('marks Cup team done and offers Start Season 5 with no Run all rounds', () => {
    const flow = seasonFlow(progress(), completedStatus());
    const cupTeam = flow.steps.find((step) => step.key === 'cupTeam');
    assert.ok(cupTeam, 'cup team step exists');
    assert.equal(cupTeam!.state, 'done');
    assert.ok(flow.next, 'next step exists');
    assert.equal(flow.next!.kind, 'event');
    if (flow.next!.kind === 'event') {
      assert.equal(flow.next!.action, 'StartNextSeason');
      assert.ok(flow.next!.label.includes('Start Season 5'), `label starts Season 5, got ${flow.next!.label}`);
      assert.equal(flow.next!.liveEvent, null);
    }
    const text = JSON.stringify(flow);
    assert.ok(!text.includes('Run all rounds'), 'no stale bulk action');
  });

  it('labels the pending Type Cup by tournament stage, not generic Group 4', () => {
    const flow = seasonFlow(progress(), pendingStatus());
    const cupTeam = flow.steps.find((step) => step.key === 'cupTeam');
    assert.ok(cupTeam, 'cup team step exists');
    assert.equal(cupTeam!.state, 'current');
    assert.ok(
      cupTeam!.detail?.includes('Qualification Group 1 of 3'),
      `stage label shown, got ${cupTeam!.detail}`,
    );
  });

  it('never invents a next action when status is missing', () => {
    const flow = seasonFlow(progress(), null);
    assert.equal(flow.next, null);
  });
});
