import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  athletePath,
  cupEditionPath,
  cupsPath,
  cupTeamPath,
  dashboardPath,
  historyPath,
  leagueStandingsPath,
  livePath,
  parseRoute,
  qualifierPath,
  qualifiersPath,
  recordsPath,
  savesPath,
  standingsLeaguePath,
  standingsPath,
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
      view: { kind: 'hub' },
    });
  });

  it('supports a URL-backed Live league and round selection', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?league=7&round=3'), {
      name: 'live',
      saveId: SAVE,
      leagueId: 7,
      round: 3,
      event: null,
      selection: null,
      transition: null,
      eventSeason: null,
      group: null,
      qualifier: null,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, ''), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: null,
      event: null,
      selection: null,
      transition: null,
      eventSeason: null,
      group: null,
      qualifier: null,
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
        event: null,
        group: null,
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
      event: null,
      selection: null,
      transition: null,
      eventSeason: null,
      group: null,
      qualifier: null,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/history`, '?season=0&competition=x'), {
      name: 'history',
      saveId: SAVE,
      season: null,
      competitionId: null,
      stage: null,
      round: null,
      event: null,
      group: null,
    });
  });

  it('round-trips postseason event selections for Live and History', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?event=qualifier&season=2&round=5'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: 5,
      event: 'qualifier',
      selection: null,
      transition: null,
      eventSeason: 2,
      group: null,
      qualifier: null,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?event=bogus&round=5'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: 5,
      event: null,
      selection: null,
      transition: null,
      eventSeason: null,
      group: null,
      qualifier: null,
    });
    assert.equal(
      livePath(SAVE, { event: 'color-cup-team', season: 3, group: 2, round: 4 }),
      `/saves/${SAVE}/live?round=4&event=color-cup-team&season=3&group=2`,
    );
    assert.deepEqual(parseRoute(`/saves/${SAVE}/history`, '?season=3&event=type-cup-team&group=1&round=8'), {
      name: 'history',
      saveId: SAVE,
      season: 3,
      competitionId: null,
      stage: null,
      round: 8,
      event: 'type-cup-team',
      group: 1,
    });
    assert.equal(
      historyPath(SAVE, { season: 3, event: 'type-cup-team', group: 1, round: 8 }),
      `/saves/${SAVE}/history?season=3&round=8&event=type-cup-team&group=1`,
    );
  });

  it('treats unknown paths as notFound without a white screen', () => {
    const route = parseRoute('/nope', '');
    assert.equal(route.name, 'notFound');
    const nested = parseRoute(`/saves/${SAVE}/nope`, '');
    assert.equal(nested.name, 'notFound');
  });

  it('reserves the MSS-041 league standings path convention', () => {
    // MSS-041 owns `/saves/:saveId/leagues/:leagueId` as a Standings alias;
    // MSS-040 only documented the builder.
    assert.equal(leagueStandingsPath(SAVE, 9), `/saves/${SAVE}/leagues/9`);
    const current = parseRoute(leagueStandingsPath(SAVE, 9), '');
    assert.equal(current.name, 'standings');
    assert.equal((current as { leagueId: number | null }).leagueId, 9);
  });

  it('supports the dedicated standings entry and league detail routes', () => {
    assert.equal(standingsPath(SAVE), `/saves/${SAVE}/standings`);
    assert.equal(
      standingsPath(SAVE, { league: 9, season: 2, view: 'matrix' }),
      `/saves/${SAVE}/standings?league=9&season=2&view=matrix`,
    );
    assert.equal(
      standingsLeaguePath(SAVE, 9, { season: 2, view: 'season' }),
      `/saves/${SAVE}/leagues/9/standings?season=2&view=season`,
    );
    assert.deepEqual(parseRoute(`/saves/${SAVE}/standings`, '?league=9&season=2&view=matrix'), {
      name: 'standings',
      saveId: SAVE,
      leagueId: 9,
      season: 2,
      view: 'matrix',
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/leagues/9/standings`, '?season=2&view=season'), {
      name: 'standings',
      saveId: SAVE,
      leagueId: 9,
      season: 2,
      view: 'season',
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/standings`, ''), {
      name: 'standings',
      saveId: SAVE,
      leagueId: null,
      season: null,
      view: null,
    });
  });

  it('ignores invalid standings view values instead of crashing', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/standings`, '?view=bogus'), {
      name: 'standings',
      saveId: SAVE,
      leagueId: null,
      season: null,
      view: null,
    });
  });
});

describe('Cup history routes', () => {
  it('builds edition and team paths', () => {
    assert.equal(cupEditionPath(SAVE, 'color', 3), `/saves/${SAVE}/cups/color/3`);
    assert.equal(cupEditionPath(SAVE, 'type', 4), `/saves/${SAVE}/cups/type/4`);
    assert.equal(cupTeamPath(SAVE, 'color', 'red'), `/saves/${SAVE}/cups/color/teams/red`);
    assert.equal(cupTeamPath(SAVE, 'type', 'Time Lord'), `/saves/${SAVE}/cups/type/teams/Time%20Lord`);
  });

  it('parses editions', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/cups/color/3`, ''), {
      name: 'cups',
      saveId: SAVE,
      view: { kind: 'edition', cup: 'color', season: 3 },
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/cups/type/4/`, ''), {
      name: 'cups',
      saveId: SAVE,
      view: { kind: 'edition', cup: 'type', season: 4 },
    });
  });

  it('round-trips team keys that need encoding', () => {
    for (const teamKey of ['Elf', 'Time Lord', 'Assembly-Worker', "Urza's"]) {
      const path = cupTeamPath(SAVE, 'type', teamKey);
      assert.deepEqual(parseRoute(path, ''), {
        name: 'cups',
        saveId: SAVE,
        view: { kind: 'team', cup: 'type', teamKey },
      });
    }
    assert.deepEqual(parseRoute(cupTeamPath(SAVE, 'color', 'multicolor'), ''), {
      name: 'cups',
      saveId: SAVE,
      view: { kind: 'team', cup: 'color', teamKey: 'multicolor' },
    });
  });

  it('treats malformed Cup paths as not found instead of the hub', () => {
    for (const tail of [
      'cups/color',
      'cups/color/abc',
      'cups/color/0',
      'cups/color/3x',
      'cups/color/teams',
      'cups/gold/3',
      'cups/color/3/extra',
      'cups/color/teams/red/extra',
      'cups/type/teams/%20',
    ]) {
      assert.equal(parseRoute(`/saves/${SAVE}/${tail}`, '').name, 'notFound', tail);
    }
  });
});

describe('MSS-060 qualifier routes', () => {
  it('builds overview and per-event paths with qualifier identity as route data', () => {
    assert.equal(qualifiersPath(SAVE), `/saves/${SAVE}/qualifiers`);
    assert.equal(qualifiersPath(SAVE, { season: 2 }), `/saves/${SAVE}/qualifiers?season=2`);
    assert.equal(
      qualifierPath(SAVE, 'Feeder1Feeder2', 'White', { season: 2 }),
      `/saves/${SAVE}/qualifiers/f1f2/white?season=2`,
    );
    assert.equal(
      qualifierPath(SAVE, 'Superleague', null, { season: 2 }),
      `/saves/${SAVE}/qualifiers/superleague?season=2`,
    );
    assert.equal(
      qualifierPath(SAVE, 'Feeder2Feeder3', 'Azorius, Maybe'),
      `/saves/${SAVE}/qualifiers/f2f3/azorius%2C%20maybe`,
    );
  });

  it('parses overview, Superleague and feeder qualifier routes', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/qualifiers`, '?season=2'), {
      name: 'qualifiers',
      saveId: SAVE,
      boundary: null,
      color: null,
      season: 2,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/qualifiers/superleague`, '?season=2'), {
      name: 'qualifiers',
      saveId: SAVE,
      boundary: 'Superleague',
      color: null,
      season: 2,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/qualifiers/f1f2/white`, '?season=2'), {
      name: 'qualifiers',
      saveId: SAVE,
      boundary: 'Feeder1Feeder2',
      color: 'white',
      season: 2,
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/qualifiers/F2-F3/BLACK`, ''), {
      name: 'qualifiers',
      saveId: SAVE,
      boundary: 'Feeder2Feeder3',
      color: 'BLACK',
      season: null,
    });
  });

  it('treats malformed qualifier paths as not found', () => {
    for (const tail of [
      'qualifiers/gold',
      'qualifiers/f1f2',
      'qualifiers/f1f2/white/extra',
      'qualifiers/superleague/white',
      'qualifiers/f1f2/%20',
    ]) {
      assert.equal(parseRoute(`/saves/${SAVE}/${tail}`, '').name, 'notFound', tail);
    }
  });
});
