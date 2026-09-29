import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  athletePath,
  cupsPath,
  dashboardPath,
  historyPath,
  leagueStandingsPath,
  livePath,
  parseRoute,
  recordsPath,
  savesPath,
} from './routes.ts';

const SAVE = '11111111-1111-1111-1111-111111111111';

describe('MSS-040 route shapes', () => {
  it('builds stable readable save-scoped paths', () => {
    assert.equal(savesPath(), '/saves');
    assert.equal(dashboardPath(SAVE), `/saves/${SAVE}/dashboard`);
    assert.equal(livePath(SAVE), `/saves/${SAVE}/live`);
    assert.equal(historyPath(SAVE), `/saves/${SAVE}/history`);
    assert.equal(recordsPath(SAVE), `/saves/${SAVE}/records`);
    assert.equal(cupsPath(SAVE), `/saves/${SAVE}/cups`);
    assert.equal(athletePath(SAVE, 42), `/saves/${SAVE}/athletes/42`);
  });

  it('encodes the save id and athlete id in profile routes', () => {
    const route = parseRoute(`/saves/${SAVE}/athletes/42`, '');
    assert.equal(route.name, 'athlete');
    assert.equal((route as { saveId: string }).saveId, SAVE);
    assert.equal((route as { athleteId: number }).athleteId, 42);
  });

  it('parses root and saves landing', () => {
    assert.equal(parseRoute('/', '').name, 'root');
    assert.equal(parseRoute('/saves', '').name, 'saves');
    assert.equal(parseRoute('/saves/', '').name, 'saves');
  });

  it('parses every main page with the correct save', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/dashboard`, ''), {
      name: 'dashboard',
      saveId: SAVE,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/records`, ''), {
      name: 'records',
      saveId: SAVE,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/cups`, ''), {
      name: 'cups',
      saveId: SAVE,
    });
  });

  it('supports a URL-backed Live league and round selection', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?league=7&round=3'), {
      name: 'live',
      saveId: SAVE,
      leagueId: 7,
      round: 3,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, ''), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: null,
    });
    assert.equal(livePath(SAVE, { league: 7, round: 3 }), `/saves/${SAVE}/live?league=7&round=3`);
    assert.equal(livePath(SAVE, { league: 7 }), `/saves/${SAVE}/live?league=7`);
  });

  it('supports an independently shareable History selection', () => {
    assert.deepEqual(
      parseRoute(`/saves/${SAVE}/history`, '?season=2&competition=5&stage=3&round=7'),
      {
        name: 'history',
        saveId: SAVE,
        season: 2,
        competitionId: 5,
        stage: 3,
        round: 7,
      },
    );
    assert.equal(
      historyPath(SAVE, { season: 2, competition: 5, stage: 3, round: 7 }),
      `/saves/${SAVE}/history?season=2&competition=5&stage=3&round=7`,
    );
  });

  it('rejects malformed athlete ids as recoverable invalid routes', () => {
    const bad = parseRoute(`/saves/${SAVE}/athletes/abc`, '');
    assert.equal(bad.name, 'invalidAthlete');
    const zero = parseRoute(`/saves/${SAVE}/athletes/0`, '');
    assert.equal(zero.name, 'invalidAthlete');
    const trailing = parseRoute(`/saves/${SAVE}/athletes/12abc`, '');
    assert.equal(trailing.name, 'invalidAthlete');
  });

  it('ignores invalid query values instead of crashing', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?league=abc&round=-2'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: null,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/history`, '?season=0&competition=x'), {
      name: 'history',
      saveId: SAVE,
      season: null,
      competitionId: null,
      stage: null,
      round: null,
    });
  });

  it('treats unknown paths as notFound without a white screen', () => {
    const route = parseRoute('/nope', '');
    assert.equal(route.name, 'notFound');
    const nested = parseRoute(`/saves/${SAVE}/nope`, '');
    assert.equal(nested.name, 'notFound');
  });

  it('reserves the MSS-041 league standings path convention', () => {
    // MSS-041 owns `/saves/:saveId/leagues/:leagueId`; MSS-040 only documents
    // the builder and keeps the address free for that task.
    assert.equal(leagueStandingsPath(SAVE, 9), `/saves/${SAVE}/leagues/9`);
    const current = parseRoute(leagueStandingsPath(SAVE, 9), '');
    assert.equal(current.name, 'notFound');
  });
});
