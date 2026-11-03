import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  chaosDrawRuleLabel,
  chaosEntrantsForStage,
  chaosGroupNumbers,
  chaosStageStrip,
  isFinalOfficial,
  survivalStageFor,
} from './chaosEditionModel.ts';
import type { ChaosCupEdition, ChaosCupGroup, ChaosCupSurvival } from './chaosCupApi.ts';

function edition(isComplete: boolean): ChaosCupEdition {
  return {
    saveId: 'save-1',
    sourceSeasonNumber: 1,
    sourceSeasonId: 9,
    rulesVersion: 3,
    chaosFormatVersion: 1,
    fieldSize: 1024,
    editionChecksum: 'ed',
    state: isComplete ? 'Completed' : 'InProgress',
    isComplete,
    stageCount: 6,
    totalGroups: 63,
    totalRounds: 1008,
    roundsPlayed: isComplete ? 1008 : 16,
    groupsCompleted: isComplete ? 63 : 1,
    stages: [0, 1, 2, 3, 4, 5].map((stageIndex) => ({
      stageIndex,
      groupCount: [32, 16, 8, 4, 2, 1][stageIndex]!,
      entrantCount: [1024, 512, 256, 128, 64, 32][stageIndex]!,
      isFinal: stageIndex === 5,
      state: isComplete ? 'Completed' : stageIndex === 0 ? 'Completed' : 'NotDrawn',
      isDrawn: isComplete || stageIndex === 0,
      isComplete: isComplete || stageIndex === 0,
      drawChecksum: isComplete || stageIndex === 0 ? 'draw' : '',
      groupsCompleted: 0,
      roundsPlayed: 0,
      totalRounds: 16,
      standingsCount: 0,
    })),
  };
}

function finalGroup(complete: boolean, provisional: boolean): ChaosCupGroup {
  return {
    saveId: 'save-1',
    sourceSeasonNumber: 1,
    sourceSeasonId: 9,
    stageIndex: 5,
    groupNumber: 1,
    isFinal: true,
    state: complete ? 'Completed' : 'InProgress',
    roundsPlayed: complete ? 16 : 4,
    totalRounds: 16,
    isComplete: complete,
    standingsProvisional: provisional,
    drawChecksum: 'draw',
    entrants: [],
    standings: Array.from({ length: 32 }, (_, index) => ({
      saveAthleteId: 100 + index,
      name: `Athlete ${100 + index}`,
      groupRank: index + 1,
      groupScoreThousandths: 32000 - index * 100,
      baseScoreThousandths: 30000 - index * 100,
      roundWins: 1,
      isAdvanced: false,
    })),
    cutoff: null,
    rounds: [],
  };
}

describe('MSS-112 Chaos edition model', () => {
  it('renders the 1024→…→32 strip and per-stage entrants', () => {
    assert.equal(chaosStageStrip(), '1024→512→256→128→64→32');
    assert.deepEqual(
      [0, 1, 2, 3, 4, 5].map((stage) => chaosEntrantsForStage(stage)),
      [1024, 512, 256, 128, 64, 32],
    );
    assert.throws(() => chaosEntrantsForStage(6));
  });

  it('expands group numbers without a giant entrant table', () => {
    assert.equal(chaosGroupNumbers(0).length, 32);
    assert.deepEqual(chaosGroupNumbers(5), [1]);
    assert.equal(chaosGroupNumbers(2).length, 8);
  });

  it('explains the first-draw color balance and later random draws', () => {
    assert.ok(chaosDrawRuleLabel(0).includes('4 per sporting color'), 'first draw balanced');
    assert.ok(chaosDrawRuleLabel(1).includes('pure random'), 'later draws random');
    assert.ok(chaosDrawRuleLabel(1).includes('no color or tier seeding'), 'no seeding claim');
    assert.ok(chaosDrawRuleLabel(5).includes('pure random'), 'final random');
  });

  it('gates the official Final on completion, not on partial progress', () => {
    assert.equal(isFinalOfficial(edition(true), finalGroup(true, false)), true, 'completed Final is official');
    assert.equal(isFinalOfficial(edition(false), finalGroup(false, true)), false, 'partial edition never official');
    assert.equal(isFinalOfficial(edition(false), null), false, 'missing group never official');
    assert.equal(isFinalOfficial(edition(true), null), false, 'missing Final never official');
    assert.equal(
      isFinalOfficial(edition(true), finalGroup(true, true)),
      false,
      'provisional standings never official',
    );
    assert.equal(isFinalOfficial(null, finalGroup(true, false)), false, 'missing edition never official');
  });

  it('resolves survival stages by index', () => {
    const survival = {
      saveId: 'save-1',
      sourceSeasonNumber: 1,
      sourceSeasonId: 9,
      rulesVersion: 3,
      chaosFormatVersion: 1,
      fieldSize: 1024,
      isComplete: false,
      stages: [
        {
          stageIndex: 0,
          groupCount: 32,
          entrantCount: 1024,
          isFinal: false,
          isDrawn: true,
          isComplete: false,
          isProvisional: true,
          initialCount: 1024,
          advancedCount: 16,
          eliminatedCount: 16,
          pendingCount: 992,
          finalistCount: 0,
          byColor: [],
          byDivision: [],
        },
      ],
    } as unknown as ChaosCupSurvival;
    assert.equal(survivalStageFor(survival, 0)?.initialCount, 1024);
    assert.equal(survivalStageFor(survival, 1), null, 'undrawn stage has no survival row');
    assert.equal(survivalStageFor(null, 0), null, 'missing survival never fabricates');
  });
});
