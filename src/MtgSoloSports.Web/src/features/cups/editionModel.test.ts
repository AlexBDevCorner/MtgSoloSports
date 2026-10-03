import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { fromColorTeamResult, fromTypeTeamResult, legsByGroup } from './editionModel.ts';

const here = dirname(fileURLToPath(import.meta.url));

const audit = {
  saveId: 's',
  sourceSeasonNumber: 1,
  sourceSeasonId: 1,
  groupCount: 4,
  groupRounds: 8,
  checksum: 'abc',
  rngBeforeState: 0,
  rngBeforeStream: 0,
  rngAfterState: 0,
  rngAfterStream: 0,
};

describe('edition team result normalisation', () => {
  it('maps a Color result to team keys and names', () => {
    const result = fromColorTeamResult({
      ...audit,
      teamCount: 8,
      championSportingColor: 3,
      championTeamName: 'Red',
      teams: [
        { sportingColor: 3, teamName: 'Red', teamRank: 1, teamScoreThousandths: 5000, teamBaseThousandths: 4000, groupWins: 2, roundWins: 9, medal: 'Gold' },
      ],
      legs: [
        { athleteId: 7, name: 'Shock', imageUrl: null, sportingColor: 'Red', selectionRank: 1, groupNumber: 1, groupRank: 2, groupScoreThousandths: 1200, baseScoreThousandths: 1000, roundWins: 3 },
      ],
    });
    assert.equal(result.groupRounds, 8);
    assert.deepEqual(result.teams[0], {
      teamKey: 'red', teamName: 'Red', teamRank: 1, teamScoreThousandths: 5000, teamBaseThousandths: 4000, groupWins: 2, roundWins: 9, medal: 'Gold',
    });
    assert.equal(result.legs[0].teamKey, 'red');
    assert.equal(result.legs[0].teamName, 'Red');
    assert.equal(result.legs[0].imageUrl, null);
  });

  it('maps a Type result keeping the creature type as key', () => {
    const result = fromTypeTeamResult({
      ...audit,
      teamCount: 2,
      championCreatureType: 'Time Lord',
      championTeamName: 'Time Lord',
      teams: [
        { creatureType: 'Time Lord', teamName: 'Time Lord', teamRank: 1, teamScoreThousandths: 1, teamBaseThousandths: 1, groupWins: 0, roundWins: 0, medal: 'Gold' },
      ],
      legs: [
        { athleteId: 9, name: 'Doctor', imageUrl: 'https://img.test/9.jpg', creatureType: 'Time Lord', selectionRank: 2, groupNumber: 2, groupRank: 1, groupScoreThousandths: 10, baseScoreThousandths: 9, roundWins: 1 },
      ],
    });
    assert.equal(result.teams[0].teamKey, 'Time Lord');
    assert.equal(result.legs[0].teamKey, 'Time Lord');
    assert.equal(result.legs[0].imageUrl, 'https://img.test/9.jpg');
  });

  it('groups legs by group number in rank order', () => {
    const leg = (groupNumber: number, groupRank: number) => ({
      athleteId: groupNumber * 10 + groupRank, name: 'a', imageUrl: null, teamKey: 'k', teamName: 'k', selectionRank: groupNumber,
      groupNumber, groupRank, groupScoreThousandths: 0, roundWins: 0,
    });
    const groups = legsByGroup([leg(2, 2), leg(1, 2), leg(2, 1), leg(1, 1)]);
    assert.deepEqual(groups.map((g) => g.groupNumber), [1, 2]);
    assert.deepEqual(groups[1].legs.map((l) => l.groupRank), [1, 2]);
  });
});

describe('cup history API client', () => {
  const api = readFileSync(join(here, 'cupHistoryApi.ts'), 'utf8');

  it('encodes the team key in the request URL', () => {
    assert.ok(api.includes('encodeURIComponent(teamKey)'), 'creature types with spaces reach the API intact');
    assert.ok(api.includes('/cups/editions'));
    assert.ok(api.includes('/history'));
  });

  it('treats only 404 as "not there yet"', () => {
    assert.ok(api.includes('failure.status === 404'));
  });
});
