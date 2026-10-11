import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { DashboardPage } from './DashboardPage.tsx';

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '11111111-1111-1111-1111-111111111111';

function dashboardData(): Parameters<typeof DashboardPage>[0]['data'] {
  return {
    detail: {
      saveId: SAVE,
      name: 'Universe',
      createdUtc: new Date(0).toISOString(),
      schemaVersion: 1,
      currentSeason: 2,
      phase: 'CupComplete',
      rngAlgorithm: 'x',
      rngVersion: 1,
      rngState: '0',
      rngStream: '0',
      rulesVersion: 1,
    },
    progress: {
      saveId: SAVE,
      seasonNumber: 2,
      globalStage: 32,
      isSeasonComplete: true,
      leagues: [],
    },
    rosters: null,
    status: {
      saveId: SAVE,
      currentSeasonNumber: 2,
      persistedPhase: 'CupComplete',
      computedPhase: 'CupComplete',
      sourceSeasonNumber: 2,
      nextSeasonNumber: 3,
      isInauguralTransition: false,
      globalStage: 32,
      isCurrentSeasonComplete: true,
      seasonComplete: true,
      movementResolved: true,
      qualifierResolved: true,
      rebalanced: true,
      cupSelectionResolved: true,
      cupIndividualResolved: true,
      cupTeamResolved: true,
      cupComplete: true,
      readyToStartNextSeason: true,
      expectedCup: 'TypeCup',
      legalNextActions: ['StartNextSeason'],
      nextActionDetail: 'Opens Season 3 for league play.',
      eventProgress: null,
      qualifierProgress: null,
    },
    statusError: null,
    stories: [
      {
        id: 1,
        eventType: 'type_cup_team_title',
        athleteId: 3,
        athleteName: 'Champion',
        seasonNumber: 2,
        stageNumber: null,
        contextJson: '{}',
        text: 'Champion wins the Type Cup team championship with Faerie in Season 2.',
      },
    ],
    leaders: [],
    superleagueComposition: [],
    recentHonours: [
      {
        seasonNumber: 2,
        seasonId: 2,
        leagueId: 9,
        leagueName: 'Superleague',
        leagueKind: 1,
        honourKind: 'SuperleagueTitle',
        athleteId: 3,
        athleteName: 'Champion',
      },
      {
        seasonNumber: 1,
        seasonId: 1,
        leagueId: 1,
        leagueName: 'White League',
        leagueKind: 0,
        honourKind: 'FeederRunnerUp',
        athleteId: 4,
        athleteName: 'Runner',
      },
    ],
    records: {
      saveId: SAVE,
      records: [
        {
          recordKey: 'feeder_titles',
          label: 'feeder titles',
          value: 2,
          valueDisplay: '2',
          isBonus: false,
          isVacant: false,
          holders: [{ athleteId: 3, athleteName: 'Champion' }],
        },
      ],
      recentHistory: [],
      scoringRecords: [],
    },
    hallOfFame: [],
  };
}

describe('MSS-072 dashboard display labels', () => {
  it('shows Cup Complete and readable honours, cups, actions and stories', () => {
    let renderer: ReturnType<typeof create> | null = null;
    act(() => {
      renderer = create(
        React.createElement(DashboardPage, {
          saveId: SAVE,
          data: dashboardData(),
          loading: false,
          error: null,
          notFound: false,
          onRefresh: () => undefined,
        }),
      );
    });
    const text = JSON.stringify(renderer!.toJSON());
    assert.ok(text.includes('Cup Complete'), 'CupComplete phase reads as Cup Complete');
    assert.ok(text.includes('Superleague title'), 'SuperleagueTitle honour reads naturally');
    assert.ok(text.includes('Feeder runner-up'), 'FeederRunnerUp honour reads naturally');
    assert.ok(text.includes('Type Cup'), 'TypeCup reads as Type Cup');
    assert.ok(text.includes('Start next season'), 'lifecycle action reads naturally');
    assert.ok(text.includes('Type Cup team title'), 'story badge reads naturally');
    assert.ok(text.includes('Feeder titles'), 'record label uses consistent capitalization');
    assert.ok(!text.includes('CupComplete'), 'raw phase identifier is gone');
    assert.ok(!text.includes('SuperleagueTitle'), 'raw honour identifier is gone');
    assert.ok(!text.includes('FeederRunnerUp'), 'raw honour identifier is gone');
    assert.ok(!text.includes('TypeCup'), 'raw cup identifier is gone');
    assert.ok(!text.includes('StartNextSeason'), 'raw action identifier is gone');
    assert.ok(!text.includes('type_cup_team_title'), 'raw story identifier is gone');
  });
});
