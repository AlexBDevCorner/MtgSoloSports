import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { projectRevealedTeamStandings } from './teamStandingsProjection.ts';

const before = [
  { teamName: 'Red', rank: null, scoreThousandths: 50000 },
  { teamName: 'Green', rank: null, scoreThousandths: 40000 },
  { teamName: 'Blue', rank: null, scoreThousandths: 40000 },
];
const members = [
  { athleteId: 1, teamName: 'Red' },
  { athleteId: 2, teamName: 'Green' },
  { athleteId: 3, teamName: 'Green' },
  { athleteId: 4, teamName: 'Blue' },
];

describe('revealed team standings', () => {
  it('shows the totals before the round while nothing is revealed', () => {
    assert.deepEqual(projectRevealedTeamStandings(before, members, []), [
      { teamName: 'Red', roundThousandths: 0, scoreThousandths: 50000 },
      { teamName: 'Blue', roundThousandths: 0, scoreThousandths: 40000 },
      { teamName: 'Green', roundThousandths: 0, scoreThousandths: 40000 },
    ]);
  });

  it('adds each revealed athlete to its team and re-sorts', () => {
    const one = projectRevealedTeamStandings(before, members, [{ athleteId: 2, finalThousandths: 28000 }]);
    assert.deepEqual(one[0], { teamName: 'Green', roundThousandths: 28000, scoreThousandths: 68000 });

    const two = projectRevealedTeamStandings(before, members, [
      { athleteId: 2, finalThousandths: 28000 },
      { athleteId: 3, finalThousandths: 1500 },
    ]);
    assert.deepEqual(two[0], { teamName: 'Green', roundThousandths: 29500, scoreThousandths: 69500 });
  });

  it('stepping back removes the points again', () => {
    const forward = projectRevealedTeamStandings(before, members, [{ athleteId: 2, finalThousandths: 28000 }]);
    const back = projectRevealedTeamStandings(before, members, []);
    assert.equal(forward.find((row) => row.teamName === 'Green')?.scoreThousandths, 68000);
    assert.equal(back.find((row) => row.teamName === 'Green')?.scoreThousandths, 40000);
  });

  it('ignores athletes outside the selected field', () => {
    const rows = projectRevealedTeamStandings(before, members, [{ athleteId: 99, finalThousandths: 9000 }]);
    assert.deepEqual(rows.map((row) => row.roundThousandths), [0, 0, 0]);
  });
});
