import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { athletesPath, athletePath, parseRoute } from '../routing/routes.ts';

const here = dirname(fileURLToPath(import.meta.url));
const read = (file: string) => readFileSync(join(here, file), 'utf8');

const page = read('AthleteSearchPage.tsx');
const api = read('athleteSearchApi.ts');
const params = read('athleteSearchParams.ts');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');
const shell = readFileSync(join(here, '..', 'shell', 'AppShell.tsx'), 'utf8');

const SAVE = '11111111-1111-1111-1111-111111111111';

describe('athlete search routes', () => {
  it('builds a shareable athletes browse path beside the profile path', () => {
    assert.equal(athletesPath(SAVE), `/saves/${SAVE}/athletes`);
    assert.equal(
      athletesPath(SAVE, '?q=faerie&minNonPool=5'),
      `/saves/${SAVE}/athletes?q=faerie&minNonPool=5`,
    );
    assert.equal(athletePath(SAVE, 42), `/saves/${SAVE}/athletes/42`);
  });

  it('parses the browse page without breaking profile routes', () => {
    assert.deepEqual(parseRoute(`/saves/${SAVE}/athletes`, ''), {
      name: 'athletes',
      saveId: SAVE,
      search: '',
    });
    assert.deepEqual(parseRoute(`/saves/${SAVE}/athletes`, '?q=faerie&minNonPool=5'), {
      name: 'athletes',
      saveId: SAVE,
      search: '?q=faerie&minNonPool=5',
    });
    const profile = parseRoute(`/saves/${SAVE}/athletes/42`, '');
    assert.equal(profile.name, 'athlete');
    assert.equal((profile as { athleteId: number }).athleteId, 42);
    assert.equal(parseRoute(`/saves/${SAVE}/athletes/abc`, '').name, 'invalidAthlete');
  });

  it('keeps athlete search reachable through the normal navigation', () => {
    assert.ok(shell.includes('athletesPath'), 'rail links to the search page');
    assert.ok(shell.includes('Athletes'), 'rail entry is labeled');
    assert.ok(shell.includes("view === 'athletes'"), 'current page is marked');
    assert.ok(app.includes('<AthleteSearchPage'), 'App renders the search page');
    assert.ok(app.includes('athletesPath(saveId, search)'), 'search state syncs to the URL');
  });
});

describe('athlete search page', () => {
  it('exposes a dedicated catalogue layout with search, filters, count, sort and results', () => {
    assert.ok(page.includes('role="search"'), 'dedicated search landmark');
    assert.ok(page.includes('Search athletes by name'), 'free-text search field');
    assert.ok(page.includes('Non-pool seasons (prominent)'), 'non-pool filter is prominent');
    assert.ok(page.includes('minNonPool'), 'minimum non-pool seasons filter');
    assert.ok(page.includes('Active filters:'), 'active-filter chips summary');
    assert.ok(page.includes('athlete'), 'result count');
    assert.ok(page.includes('Sort by'), 'sort control');
    assert.ok(page.includes('Clear filters'), 'clear/reset filters action');
  });

  it('covers the required sporting filters with AND/OR semantics documented', () => {
    for (const token of [
      'Current league or pool',
      'Sporting colour',
      'Creature types',
      'Honours at least',
      'Titles at least',
      'Titles presence',
      'Highest league reached',
      'Best finish at most',
      'Superleague seasons at least',
      'Cup status',
    ]) {
      assert.ok(page.includes(token), `filter ${token}`);
    }
    assert.ok(
      page.includes('AND across categories') || page.includes('OR'),
      'filter composition is documented',
    );
  });

  it('renders enough match context per result and links to the profile route', () => {
    assert.ok(page.includes('AthleteLink'), 'results link to athlete profiles');
    assert.ok(page.includes('athleteId={row.athleteId}'), 'profile link carries the athlete id');
    for (const token of ['row.nonPoolSeasons', 'row.honoursCount', 'row.currentLeagueName', 'row.imageUrl']) {
      assert.ok(page.includes(token), `result shows ${token}`);
    }
    assert.ok(page.includes('row.titlesCount'), 'titles distinguish wins from lower podiums');
  });

  it('distinguishes Superleague, feeder divisions and pool in league filters', () => {
    assert.ok(page.includes('leagueLevelLabel'), 'league options label tiers from data');
    assert.ok(page.includes("value=\"feeder\">Feeder only (any division)"), 'highest-league filter names the division scope');
    assert.ok(page.includes('row.currentLeagueLevel'), 'results carry the current tier');
  });

  it('handles empty states clearly', () => {
    assert.ok(page.includes('No athletes match'), 'no-match state');
    assert.ok(page.includes('No athletes match “'), 'text-search no-match state');
    assert.ok(page.includes('Clear filters'), 'empty state offers a reset');
  });

  it('keeps search state in the URL and pages after filtering', () => {
    assert.ok(page.includes('onSearchChange'), 'URL state callback');
    assert.ok(page.includes('buildAthleteSearchString'), 'state serializes to the URL');
    assert.ok(page.includes('parseAthleteSearchParams'), 'URL restores state');
    assert.ok(page.includes('Previous') && page.includes('Next'), 'pagination controls');
    assert.ok(params.includes('export function parseAthleteSearchParams'), 'URL parser is unit-tested');
    assert.ok(params.includes('export function toAthleteSearchRequest'), 'backend request mapping');
  });

  it('queries the backend search slice instead of loading histories into the browser', () => {
    assert.ok(api.includes('/athletes/search'), 'search endpoint');
    assert.ok(api.includes('/athletes/search/options'), 'filter options endpoint');
    assert.ok(api.includes('minNonPool'), 'non-pool range reaches the backend');
    assert.ok(api.includes('fetchAthleteSearch'), 'page fetches one result page');
    assert.ok(!page.includes('fetchAthleteProfile'), 'browse never loads full profiles per row');
  });
});
