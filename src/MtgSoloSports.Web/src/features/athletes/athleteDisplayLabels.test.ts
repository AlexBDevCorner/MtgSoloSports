import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { create, act } from 'react-test-renderer';
import { AthleteProfilePage } from './AthleteProfilePage.tsx';
import type { AthleteProfileState } from './useAthleteProfile.ts';

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
(globalThis as unknown as { React: typeof React }).React = React;

const SAVE = '11111111-1111-1111-1111-111111111111';

function stateWithPersistedCopy(): AthleteProfileState {
  return {
    profile: {
      saveId: SAVE,
      athleteId: 7,
      card: {
        name: 'Test Athlete',
        sportingColor: 0,
        sportingColorName: 'White',
        creatureTypes: ['Faerie'],
        frontColors: 'W',
        manaCost: '{W}',
        typeLine: 'Creature — Faerie',
        imageUrl: null,
        setCode: null,
        isArtifact: false,
        hasDevoid: false,
        hasHybridMana: false,
        typeCupNationality: null,
      },
      career: {
        seasonsActive: 2,
        isActive: true,
        currentLeagueId: 1,
        currentLeagueName: 'White League',
        currentLeagueKind: 0,
        roundWins: 3,
        stageWins: 1,
        stageSeconds: 0,
        stageThirds: 0,
        stagePodiums: 1,
        bestSeasonFinish: 2,
        bestSeasonNumber: 1,
        lifetimeEarnedBonusThousandths: 1000,
        currentEffectiveBonusThousandths: 1000,
        lastSeasonNumber: 2,
        lastStageNumber: 32,
      },
      seasons: [],
      honours: [
        { seasonNumber: 1, leagueName: 'White League', honourKind: 'FeederRunnerUp' },
        { seasonNumber: 1, leagueName: 'White League', honourKind: 'ColorCupIndividualRunnerUp' },
        { seasonNumber: 2, leagueName: 'Superleague', honourKind: 'SuperleagueTitle' },
      ],
      movements: [],
      cupSelections: [
        { cupKind: 'ColorCup', sourceSeasonNumber: 1, team: 'White', selectionRank: 1 },
        { cupKind: 'TypeCup', sourceSeasonNumber: 2, team: 'Faerie', selectionRank: 2 },
      ],
      cupHistory: [],
    },
    stories: [
      {
        id: 1,
        eventType: 'first_stage_win',
        athleteId: 7,
        athleteName: 'Test Athlete',
        seasonNumber: 1,
        stageNumber: 1,
        contextJson: '{}',
        text: 'Test Athlete wins White League Stage 1 in Season 1 for a first stage triumph.',
      },
    ],
    recordHoldings: [
      // Backend labels carry the reported mixed capitalization; the page must
      // render the centralized sentence-case names instead.
      { recordKey: 'feeder_titles', label: 'feeder titles', value: 1, valueDisplay: '1', isBonus: false },
      { recordKey: 'superleague_titles', label: 'Superleague titles', value: 1, valueDisplay: '1', isBonus: false },
    ],
    loading: false,
    error: null,
    notFound: false,
    storiesLoading: false,
    storiesError: null,
    holdingsLoading: false,
    holdingsError: null,
    refresh: () => undefined,
  };
}

function renderedText(node: unknown): string {
  return JSON.stringify(node);
}

describe('MSS-072 athlete profile display labels', () => {
  it('renders honours, selections, records and stories in readable English', () => {
    let renderer: ReturnType<typeof create> | null = null;
    act(() => {
      renderer = create(
        React.createElement(AthleteProfilePage, {
          saveId: SAVE,
          athleteId: 7,
          state: stateWithPersistedCopy(),
        }),
      );
    });
    const text = renderedText(renderer!.toJSON());
    assert.ok(text.includes('Feeder runner-up'), 'FeederRunnerUp reads naturally');
    assert.ok(text.includes('Color Cup individual runner-up'), 'ColorCupIndividualRunnerUp reads naturally');
    assert.ok(text.includes('Superleague title'), 'SuperleagueTitle reads naturally');
    assert.ok(text.includes('Color Cup'), 'ColorCup selection reads as Color Cup');
    assert.ok(text.includes('Type Cup'), 'TypeCup selection reads as Type Cup');
    assert.ok(text.includes('Feeder titles'), 'record label uses consistent capitalization');
    assert.ok(text.includes('Superleague titles'), 'record label keeps the proper noun');
    assert.ok(text.includes('First stage win'), 'story badge reads naturally');
    assert.ok(!text.includes('FeederRunnerUp'), 'raw honour identifier is gone');
    assert.ok(!text.includes('ColorCupIndividualRunnerUp'), 'raw honour identifier is gone');
    assert.ok(!text.includes('SuperleagueTitle'), 'raw honour identifier is gone');
    assert.ok(!text.includes('ColorCup'), 'raw selection identifier is gone');
    assert.ok(!text.includes('TypeCup'), 'raw selection identifier is gone');
    assert.ok(!text.includes('first_stage_win'), 'raw story identifier is gone');
    assert.ok(!text.includes('feeder titles'), 'mixed-case backend record label is gone');
  });
});
