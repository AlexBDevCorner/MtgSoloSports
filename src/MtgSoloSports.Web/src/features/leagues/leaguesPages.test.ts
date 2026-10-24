import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = join(here, '..', '..');

function read(rel: string): string {
  return readFileSync(join(webRoot, rel), 'utf8');
}

describe('MSS-085 leagues pages', () => {
  it('aggregates once per page instead of fetching every season separately', () => {
    const api = read('features/leagues/leaguesApi.ts');
    assert.ok(api.includes('fetchLeagueProfiles'), 'overview uses one aggregated request');
    assert.ok(api.includes('fetchLeagueProfile'), 'profile uses one aggregated request');
    for (const page of [
      'features/leagues/LeaguesOverviewPage.tsx',
      'features/leagues/LeagueProfilePage.tsx',
    ]) {
      const source = read(page);
      assert.ok(!source.includes('fetchHistorySeasonTable'), `${page} avoids per-season table fan-out`);
      assert.ok(!source.includes('fetchSeasonPlacements'), `${page} avoids per-season placement fan-out`);
      assert.ok(!source.includes('fetchHistoryStageStandings'), `${page} avoids stage fan-out`);
    }
  });

  it('keeps athlete names as true profile links on every league panel', () => {
    for (const page of [
      'features/leagues/LeaguesOverviewPage.tsx',
      'features/leagues/LeagueProfilePage.tsx',
    ]) {
      const source = read(page);
      assert.ok(source.includes('AthleteLink'), `${page} links to career profiles`);
      assert.ok(!source.includes('window.open'), `${page} never uses window.open`);
    }
    const profile = read('features/leagues/LeagueProfilePage.tsx');
    assert.ok(profile.includes('standingsLeaguePath'), 'podium seasons link to exact standings');
  });

  it('labels all-time totals explicitly and never mixes career or bonus data', () => {
    const profile = read('features/leagues/LeagueProfilePage.tsx');
    assert.ok(profile.includes('Total league points'), 'leaderboard uses the explicit total label');
    assert.ok(profile.includes('Hall of Fame'), 'profile disclaims the weighted career score');
    assert.ok(profile.includes('Cup'), 'profile disclaims cup results');
  });

  it('marks the ongoing season separately and never counts it', () => {
    const profile = read('features/leagues/LeagueProfilePage.tsx');
    assert.ok(profile.includes('currentSeason'), 'ongoing season renders in its own panel');
    assert.ok(profile.includes('not yet final'), 'ongoing panel is clearly marked provisional');
    assert.ok(profile.includes('does not count'), 'provisional exclusion is explicit');
  });

  it('stays usable on desktop and narrow viewports with loading/empty/error states', () => {
    const overview = read('features/leagues/LeaguesOverviewPage.tsx');
    assert.ok(overview.includes('Loading'), 'overview has a loading state');
    assert.ok(overview.includes('Notice'), 'overview has empty/error states');
    assert.ok(overview.includes('league-cards'), 'overview grid scrolls within the page');
    const profile = read('features/leagues/LeagueProfilePage.tsx');
    assert.ok(profile.includes('Loading'), 'profile has a loading state');
    assert.ok(profile.includes('Notice'), 'profile has empty/error states');
    assert.ok(profile.includes('table-wrap'), 'profile scrolls tables inside their container');
    assert.ok(profile.includes('numeric'), 'profile aligns numeric columns');
    const css = read('features/leagues/Leagues.css');
    assert.ok(css.includes('.league-cards'), 'overview grid is styled');
    assert.ok(css.includes('.league-card'), 'league cards are styled');
  });

  it('guards payloads by save and league so universe switches never leak data', () => {
    const overview = read('features/leagues/LeaguesOverviewPage.tsx');
    assert.ok(overview.includes('data.saveId === saveId'), 'overview drops stale saves');
    const profile = read('features/leagues/LeagueProfilePage.tsx');
    assert.ok(
      profile.includes('data.leagueKey === leagueKey'),
      'profile drops stale leagues and saves',
    );
  });

  it('exposes Leagues alongside Cups with current-page semantics', () => {
    const shell = read('features/shell/AppShell.tsx');
    assert.ok(shell.includes('leaguesPath'), 'shell links to the leagues overview');
    assert.ok(shell.includes("view === 'leagues'"), 'active leagues page keeps current semantics');
    const app = read('App.tsx');
    assert.ok(app.includes('LeaguesOverviewPage'), 'overview route renders');
    assert.ok(app.includes('LeagueProfilePage'), 'profile route renders');
  });

  it('discovers league profiles from standings without changing standings behavior', () => {
    const standings = read('features/standings/StandingsPage.tsx');
    assert.ok(standings.includes('League profile'), 'standings links to the league profile');
    assert.ok(standings.includes('leagueKeyFor'), 'link derives from backend tier identity');
    assert.ok(standings.includes('feederDivision'), 'link uses the persisted division, not names');
    assert.ok(standings.includes('sportingColor'), 'link uses the persisted color, not names');
  });
});
