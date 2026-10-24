import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { leaguePath, leaguesPath, parseRoute } from '../routing/routes.ts';

const SAVE = '11111111-1111-1111-1111-111111111111';

describe('MSS-085 leagues routes', () => {
  it('builds stable overview and profile paths', () => {
    assert.equal(leaguesPath(SAVE), `/saves/${SAVE}/leagues`);
    assert.equal(leaguePath(SAVE, 'superleague'), `/saves/${SAVE}/leagues/superleague`);
    assert.equal(leaguePath(SAVE, 'feeder-1-white'), `/saves/${SAVE}/leagues/feeder-1-white`);
  });

  it('parses the overview with the correct save', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/leagues`, ''), {
      name: 'leagues',
      saveId: SAVE,
      leagueKey: null,
    });
  });

  it('parses stable profile keys and normalizes case', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/leagues/superleague`, ''), {
      name: 'leagues',
      saveId: SAVE,
      leagueKey: 'superleague',
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/leagues/Feeder-1-White`, ''), {
      name: 'leagues',
      saveId: SAVE,
      leagueKey: 'feeder-1-white',
    });
  });

  it('keeps numeric league segments as standings aliases', () => {
    const route = parseRoute(`/saves/${SAVE}/leagues/42`, '');
    assert.equal(route.name, 'standings');
    assert.equal((route as { leagueId: number }).leagueId, 42);
    const detail = parseRoute(`/saves/${SAVE}/leagues/42/standings`, '?season=3&view=matrix');
    assert.equal(detail.name, 'standings');
    assert.equal((detail as { leagueId: number }).leagueId, 42);
    assert.equal((detail as { season: number }).season, 3);
  });
});
