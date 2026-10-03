import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  activeAthleteFilterChips,
  buildAthleteSearchString,
  DEFAULT_ATHLETE_SEARCH,
  isDefaultAthleteSearch,
  parseAthleteSearchParams,
  toAthleteSearchRequest,
} from './athleteSearchParams.ts';

describe('athleteSearchParams', () => {
  it('round-trips a full search through the URL', () => {
    const parsed = parseAthleteSearchParams(
      '?q=faerie&colours=0,5&types=Faerie,Wizard&current=Superleague&minNonPool=5&minHonours=1&hasTitle=only&highest=superleague&bestFinishMax=3&minSuperSeasons=1&cup=podium&sort=honours&dir=desc&take=50&skip=50',
    );
    assert.equal(parsed.q, 'faerie');
    assert.deepEqual(parsed.colours, [0, 5]);
    assert.deepEqual(parsed.types, ['Faerie', 'Wizard']);
    assert.deepEqual(parsed.current, ['Superleague']);
    assert.equal(parsed.minNonPool, 5);
    assert.equal(parsed.minHonours, 1);
    assert.equal(parsed.hasTitle, 'only');
    assert.equal(parsed.highest, 'superleague');
    assert.equal(parsed.bestFinishMax, 3);
    assert.equal(parsed.minSuperSeasons, 1);
    assert.equal(parsed.cup, 'podium');
    assert.equal(parsed.sort, 'honours');
    assert.equal(parsed.dir, 'desc');
    assert.equal(parsed.take, 50);
    assert.equal(parsed.skip, 50);

    const rebuilt = parseAthleteSearchParams(buildAthleteSearchString(parsed));
    assert.deepEqual(rebuilt, parsed);
  });

  it('defaults an empty search and omits defaults from the URL', () => {
    const parsed = parseAthleteSearchParams('');
    assert.deepEqual(parsed, DEFAULT_ATHLETE_SEARCH);
    assert.equal(buildAthleteSearchString(DEFAULT_ATHLETE_SEARCH), '');
    assert.equal(isDefaultAthleteSearch(DEFAULT_ATHLETE_SEARCH), true);
    assert.equal(
      isDefaultAthleteSearch({ ...DEFAULT_ATHLETE_SEARCH, minNonPool: 5 }),
      false,
    );
  });

  it('ignores invalid query values instead of crashing', () => {
    const parsed = parseAthleteSearchParams(
      '?sort=bogus&dir=sideways&colours=99,abc&minNonPool=-3&bestFinishMax=99&hasTitle=maybe&highest=deep&cup=gold&take=9999',
    );
    assert.equal(parsed.sort, 'name');
    assert.equal(parsed.dir, 'asc');
    assert.deepEqual(parsed.colours, []);
    assert.equal(parsed.minNonPool, null);
    assert.equal(parsed.bestFinishMax, null);
    assert.equal(parsed.hasTitle, '');
    assert.equal(parsed.highest, '');
    assert.equal(parsed.cup, '');
    assert.equal(parsed.take, 100);
  });

  it('summarizes active filters for chips', () => {
    assert.deepEqual(activeAthleteFilterChips(DEFAULT_ATHLETE_SEARCH), []);
    const chips = activeAthleteFilterChips({
      ...DEFAULT_ATHLETE_SEARCH,
      q: 'faerie',
      minNonPool: 5,
      hasTitle: 'none',
      cup: 'title',
    });
    const labels = chips.map((chip) => chip.label);
    assert.ok(labels.some((label) => label.includes('faerie')), 'name chip');
    assert.ok(labels.some((label) => label.includes('Non-pool seasons')), 'non-pool chip');
    assert.ok(labels.some((label) => label.includes('No titles')), 'titles chip');
    assert.ok(labels.some((label) => label.includes('Cup:')), 'cup chip');
  });

  it('maps page state to the backend request shape', () => {
    const request = toAthleteSearchRequest({
      ...DEFAULT_ATHLETE_SEARCH,
      q: '  Faerie ',
      colours: [5],
      sort: 'nonPool',
      dir: 'desc',
      skip: 100,
      take: 50,
    });
    assert.equal(request.q, 'Faerie');
    assert.deepEqual(request.colours, [5]);
    assert.equal(request.sort, 'nonPool');
    assert.equal(request.skip, 100);
    assert.equal(request.take, 50);
    assert.equal(request.hasTitle, null);
  });
});
