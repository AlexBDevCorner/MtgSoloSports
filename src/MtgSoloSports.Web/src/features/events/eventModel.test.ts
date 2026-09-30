import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isEventKey, isTeamEvent, progressLabel, resultsTarget, roundLabel } from './eventModel.ts';

describe('postseason event model', () => {
  it('recognises the four event keys only', () => {
    for (const key of ['qualifier', 'color-cup-individual', 'color-cup-team', 'type-cup-team']) {
      assert.equal(isEventKey(key), true, key);
    }
    assert.equal(isEventKey('league'), false);
    assert.equal(isEventKey('toString'), false);
    assert.equal(isEventKey(null), false);
  });

  it('flags team events', () => {
    assert.equal(isTeamEvent('color-cup-team'), true);
    assert.equal(isTeamEvent('type-cup-team'), true);
    assert.equal(isTeamEvent('qualifier'), false);
  });

  it('labels progress for single-stage and grouped events', () => {
    assert.equal(progressLabel({ roundsPlayed: 5, totalRounds: 16, groupCount: 1, roundsPerGroup: 16 }), 'Round 5 / 16');
    assert.equal(progressLabel({ roundsPlayed: 0, totalRounds: 16, groupCount: 1, roundsPerGroup: 16 }), 'Round 0 / 16');
    assert.equal(progressLabel({ roundsPlayed: 11, totalRounds: 32, groupCount: 4, roundsPerGroup: 8 }), 'Group 2 · Round 3 / 8');
    assert.equal(progressLabel({ roundsPlayed: 8, totalRounds: 32, groupCount: 4, roundsPerGroup: 8 }), 'Group 1 · Round 8 / 8');
    assert.equal(progressLabel({ roundsPlayed: 0, totalRounds: 32, groupCount: 4, roundsPerGroup: 8 }), 'Group 1 · Round 0 / 8');
    assert.equal(progressLabel({ roundsPlayed: 32, totalRounds: 32, groupCount: 4, roundsPerGroup: 8 }), 'Complete');
  });

  it('labels a single round', () => {
    assert.equal(roundLabel(null, 7), 'Round 7');
    assert.equal(roundLabel(3, 2), 'Group 3 · Round 2');
  });

  it('points results to standings for the qualifier and Cups otherwise', () => {
    assert.equal(resultsTarget('qualifier'), 'standings');
    assert.equal(resultsTarget('type-cup-team'), 'cups');
  });
});
