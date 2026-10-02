import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  colorCupToBoard,
  describeCupProgress,
  isCurrentResponse,
  typeCupToBoard,
  type TeamLiveBoardData,
} from './teamLive.ts';
import type { ColorCupTeamLive, TypeCupTeamLive } from './teamLiveApi.ts';

function typeLive(overrides: Partial<TypeCupTeamLive> = {}): TypeCupTeamLive {
  return {
    saveId: 'save-1',
    sourceSeasonNumber: 2,
    sourceSeasonId: 7,
    teamCount: 2,
    groupCount: 4,
    groupRounds: 8,
    completedRounds: 0,
    totalRounds: 32,
    currentGroupNumber: 1,
    currentRoundNumber: 1,
    isComplete: false,
    isProvisional: true,
    championCreatureType: '',
    checksum: '',
    lastCompletedGroupNumber: 0,
    lastCompletedRoundNumber: 0,
    teams: [
      {
        creatureType: 'Dwarf',
        teamName: 'Dwarf',
        teamRank: 1,
        teamScoreThousandths: 0,
        teamBaseThousandths: 0,
        medal: 'None',
      },
      {
        creatureType: 'Elf',
        teamName: 'Elf',
        teamRank: 2,
        teamScoreThousandths: 0,
        teamBaseThousandths: 0,
        medal: 'None',
      },
    ],
    ...overrides,
  };
}

describe('typeCupToBoard', () => {
  it('keeps every team at zero before any round with no medals', () => {
    const board = typeCupToBoard(typeLive());
    assert.equal(board.rows.length, 2);
    assert.deepEqual(
      board.rows.map((row) => row.scoreThousandths),
      [0, 0],
    );
    assert.deepEqual(
      board.rows.map((row) => row.medal),
      [null, null],
    );
    assert.equal(board.isProvisional, true);
    assert.equal(board.championName, null);
  });

  it('preserves backend order and exposes medals only when final', () => {
    const board = typeCupToBoard(
      typeLive({
        completedRounds: 32,
        isComplete: true,
        isProvisional: false,
        championCreatureType: 'Elf',
        teams: [
          {
            creatureType: 'Elf',
            teamName: 'Elf',
            teamRank: 1,
            teamScoreThousandths: 5000,
            teamBaseThousandths: 4900,
            medal: 'Gold',
          },
          {
            creatureType: 'Dwarf',
            teamName: 'Dwarf',
            teamRank: 2,
            teamScoreThousandths: 4000,
            teamBaseThousandths: 3900,
            medal: 'Silver',
          },
        ],
      }),
    );
    assert.deepEqual(
      board.rows.map((row) => row.key),
      ['Elf', 'Dwarf'],
    );
    assert.deepEqual(
      board.rows.map((row) => row.medal),
      ['Gold', 'Silver'],
    );
    assert.equal(board.championName, 'Elf');
  });

  it('hides provisional medal strings even when the payload names one', () => {
    const board = typeCupToBoard(
      typeLive({
        completedRounds: 1,
        teams: [
          {
            creatureType: 'Elf',
            teamName: 'Elf',
            teamRank: 1,
            teamScoreThousandths: 120,
            teamBaseThousandths: 100,
            medal: 'Gold',
          },
          {
            creatureType: 'Dwarf',
            teamName: 'Dwarf',
            teamRank: 2,
            teamScoreThousandths: 90,
            teamBaseThousandths: 80,
            medal: 'None',
          },
        ],
      }),
    );
    assert.deepEqual(
      board.rows.map((row) => row.medal),
      [null, null],
    );
  });
});

describe('colorCupToBoard', () => {
  it('keys rows by sporting color and keeps all eight teams', () => {
    const live: ColorCupTeamLive = {
      saveId: 'save-1',
      sourceSeasonNumber: 1,
      sourceSeasonId: 3,
      teamCount: 8,
      groupCount: 4,
      groupRounds: 8,
      completedRounds: 9,
      totalRounds: 32,
      currentGroupNumber: 2,
      currentRoundNumber: 2,
      isComplete: false,
      isProvisional: true,
      championSportingColor: -1,
      championTeamName: '',
      checksum: '',
      lastCompletedGroupNumber: 2,
      lastCompletedRoundNumber: 1,
      teams: [0, 1, 2, 3, 4, 5, 6, 7].map((color) => ({
        sportingColor: color,
        teamName: `Color${color}`,
        teamRank: 8 - color,
        teamScoreThousandths: color * 100,
        teamBaseThousandths: color * 90,
        medal: 'None',
      })),
    };
    const board = colorCupToBoard(live);
    assert.equal(board.rows.length, 8);
    assert.deepEqual(
      board.rows.map((row) => row.key),
      ['0', '1', '2', '3', '4', '5', '6', '7'],
    );
  });
});

describe('describeCupProgress', () => {
  it('labels zero, mid-group and complete states', () => {
    const zero = typeCupToBoard(typeLive());
    assert.match(describeCupProgress(zero), /No rounds yet/);

    const mid: TeamLiveBoardData = {
      ...zero,
      completedRounds: 9,
      currentGroupNumber: 2,
      currentRoundNumber: 2,
    };
    assert.equal(describeCupProgress(mid), 'Group 2 · Round 2/8 (9/32 rounds)');

    const done: TeamLiveBoardData = { ...zero, completedRounds: 32, isComplete: true };
    assert.match(describeCupProgress(done), /Complete/);
  });
});

describe('isCurrentResponse', () => {
  it('rejects older requests and other saves', () => {
    assert.equal(isCurrentResponse('a', 3, 'a', 3), true);
    assert.equal(isCurrentResponse('a', 3, 'a', 2), false);
    assert.equal(isCurrentResponse('a', 3, 'b', 3), false);
  });
});
