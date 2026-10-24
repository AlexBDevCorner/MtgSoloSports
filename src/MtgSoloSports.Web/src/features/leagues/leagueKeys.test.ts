import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  isProfileKeySegment,
  leagueKeyFor,
  leagueKeyLabel,
  parseLeagueKey,
  SUPERLEAGUE_KEY,
} from './leagueKeys.ts';

describe('MSS-085 stable league keys', () => {
  it('collapses the Superleague to one key regardless of stored color', () => {
    assert.equal(leagueKeyFor({ kind: 'Superleague', feederDivision: 0, sportingColor: 0 }), 'superleague');
    assert.equal(leagueKeyFor({ kind: 'Superleague', feederDivision: 0, sportingColor: 5 }), 'superleague');
    assert.equal(SUPERLEAGUE_KEY, 'superleague');
  });

  it('keeps every feeder division and color distinct', () => {
    assert.equal(
      leagueKeyFor({ kind: 'Feeder', feederDivision: 1, sportingColor: 0 }),
      'feeder-1-white',
    );
    assert.equal(
      leagueKeyFor({ kind: 'Feeder', feederDivision: 2, sportingColor: 1 }),
      'feeder-2-blue',
    );
    assert.equal(
      leagueKeyFor({ kind: 'Feeder', feederDivision: 3, sportingColor: 7 }),
      'feeder-3-colorless',
    );
  });

  it('keeps legacy v1 feeders distinct from tiered feeder-1 rows', () => {
    const legacy = leagueKeyFor({ kind: 'Feeder', feederDivision: 0, sportingColor: 0 });
    const tiered = leagueKeyFor({ kind: 'Feeder', feederDivision: 1, sportingColor: 0 });
    assert.equal(legacy, 'feeder-white');
    assert.equal(tiered, 'feeder-1-white');
    assert.notEqual(legacy, tiered);
  });

  it('returns null instead of a fabricated URL for unknown identity', () => {
    assert.equal(leagueKeyFor({ kind: 'Qualifier', feederDivision: 1, sportingColor: 0 }), null);
    assert.equal(leagueKeyFor({ kind: 'Feeder', feederDivision: 1, sportingColor: null }), null);
    assert.equal(leagueKeyFor({ kind: '', feederDivision: 1, sportingColor: 0 }), null);
  });

  it('round-trips keys back to the sporting triple', () => {
    assert.deepEqual(parseLeagueKey('superleague'), {
      kind: 'Superleague',
      feederDivision: 0,
      sportingColor: null,
    });
    assert.deepEqual(parseLeagueKey('feeder-1-white'), {
      kind: 'Feeder',
      feederDivision: 1,
      sportingColor: 0,
    });
    assert.deepEqual(parseLeagueKey('feeder-3-colorless'), {
      kind: 'Feeder',
      feederDivision: 3,
      sportingColor: 7,
    });
    assert.deepEqual(parseLeagueKey('feeder-white'), {
      kind: 'Feeder',
      feederDivision: 0,
      sportingColor: 0,
    });
    assert.deepEqual(parseLeagueKey('Feeder-2-Blue'), {
      kind: 'Feeder',
      feederDivision: 2,
      sportingColor: 1,
    });
  });

  it('rejects pool, numeric and malformed segments', () => {
    for (const raw of [null, '', 'pool', 'feeder', 'feeder-4-white', 'feeder-1-gold', '12', 'superleague-x']) {
      assert.equal(parseLeagueKey(raw), null, `rejects ${String(raw)}`);
    }
  });

  it('treats numeric segments as standings aliases, never profiles', () => {
    assert.equal(isProfileKeySegment('12'), false);
    assert.equal(isProfileKeySegment('superleague'), true);
    assert.equal(isProfileKeySegment('feeder-1-white'), true);
    assert.equal(isProfileKeySegment(''), false);
    assert.equal(isProfileKeySegment(null), false);
  });

  it('labels keys for loading states without backend data', () => {
    assert.equal(leagueKeyLabel('superleague'), 'Superleague');
    assert.equal(leagueKeyLabel('feeder-1-white'), 'Feeder 1 · White');
    assert.equal(leagueKeyLabel('feeder-white'), 'Feeder · White');
    assert.equal(leagueKeyLabel('nope'), 'League');
  });
});
