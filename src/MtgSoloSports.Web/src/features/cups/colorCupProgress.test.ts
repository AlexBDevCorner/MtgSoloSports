import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { advancingLabel, cutoffSummary, isPhasedProgress, isPhasedResult, totalRoundsFor } from './colorCupProgress.ts';
import type { ColorCupIndividualProgress, ColorCupIndividualResult } from './colorCupApi.ts';

function result(overrides: Partial<ColorCupIndividualResult>): ColorCupIndividualResult {
  return {
    saveId: 's',
    sourceSeasonNumber: 1,
    sourceSeasonId: 1,
    cupSize: 32,
    rounds: 32,
    checksum: 'abc',
    rngBeforeState: 0,
    rngBeforeStream: 0,
    rngAfterState: 0,
    rngAfterStream: 0,
    championAthleteId: 1,
    championName: 'Champ',
    standings: [],
    ...overrides,
  };
}

function progress(overrides: Partial<ColorCupIndividualProgress>): ColorCupIndividualProgress {
  return {
    saveId: 's',
    sourceSeasonNumber: 1,
    sourceSeasonId: 1,
    formatVersion: 1,
    isPhased: true,
    totalRounds: 32,
    roundsPlayed: 17,
    isComplete: false,
    qualifyingRounds: 16,
    cutoffRank: 16,
    finalFieldSize: 16,
    qualifyingOrderAthleteIds: [],
    qualifierAthleteIds: [],
    eliminatedAthleteIds: [],
    tieDrawConsumed: false,
    isProvisionalCutoff: false,
    standings: [],
    carryover: null,
    ...overrides,
  };
}

describe('color cup phased helpers', () => {
  it('treats phased results as 32 rounds and legacy as 16', () => {
    assert.equal(isPhasedResult(result({ isPhased: true, rounds: 32 })), true);
    assert.equal(isPhasedResult(result({ isPhased: false, rounds: 16 })), false);
    assert.equal(totalRoundsFor(result({ rounds: 32 })), 32);
    assert.equal(totalRoundsFor(result({ rounds: 16 })), 16);
  });

  it('falls back to rounds when the additive format is absent', () => {
    assert.equal(isPhasedResult(result({ rounds: 32, isPhased: undefined, formatVersion: undefined })), true);
    assert.equal(isPhasedResult(result({ rounds: 16, isPhased: undefined, formatVersion: undefined })), false);
  });

  it('labels qualified and eliminated without inventing legacy cutoffs', () => {
    assert.equal(advancingLabel('Qualified'), 'Qualified');
    assert.equal(advancingLabel('Eliminated'), 'Eliminated');
    assert.equal(advancingLabel('ProvisionalQualified'), 'Provisional top 16');
    assert.equal(advancingLabel('ProvisionalEliminated'), 'Provisional bottom 16');
    assert.equal(advancingLabel(null), '—');
  });

  it('summarises the top-16 boundary and tie draw', () => {
    assert.ok(cutoffSummary(16, false, false).includes('top 16 qualify'));
    assert.ok(cutoffSummary(16, true, false).includes('yes'));
    assert.ok(cutoffSummary(16, false, true).includes('provisional'));
  });

  it('detects phased progress from the additive format', () => {
    assert.equal(isPhasedProgress(progress({})), true);
    assert.equal(isPhasedProgress(progress({ isPhased: false, formatVersion: 0, totalRounds: 16 })), false);
  });
});
