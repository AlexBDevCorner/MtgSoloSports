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

describe('MSS-041 standings navigation', () => {
  it('exposes Standings as a prominent save-scoped shell entry', () => {
    const shell = read('features/shell/AppShell.tsx');
    assert.ok(shell.includes('standingsPath'), 'shell links to the standings page');
    assert.ok(shell.includes('Standings'), 'standings entry is labeled');
    assert.ok(shell.includes("view === 'standings'"), 'active standings page keeps current semantics');
  });

  it('links contextually from Live, Dashboard and History', () => {
    const live = read('features/live/LivePage.tsx');
    assert.ok(live.includes('Full standings'), 'live offers a contextual standings entry');
    assert.ok(
      live.includes('standingsLeaguePath') || live.includes('standingsPath'),
      'live links with a shareable standings URL',
    );
    const dashboard = read('features/dashboard/DashboardPage.tsx');
    assert.ok(dashboard.includes('standingsPath'), 'dashboard links to standings');
    const history = read('features/history/HistoryPage.tsx');
    assert.ok(history.includes('Open matrix'), 'history links to the placements matrix');
    assert.ok(history.includes('standingsLeaguePath'), 'history uses a league-scoped standings URL');
  });

  it('keeps athlete names as true profile links in the standings page', () => {
    const page = read('features/standings/StandingsPage.tsx');
    assert.ok(page.includes('AthleteLink'), 'matrix and season rows link to career profiles');
    assert.ok(!page.includes('window.open'), 'standings never uses window.open');
    assert.ok(page.includes('standingsApi') || page.includes('fetchSeasonPlacements'), 'matrix uses one compact aggregate');
    assert.ok(!page.includes('fetchHistoryStageStandings'), 'matrix avoids 32 sequential stage requests');
  });

  it('keeps the matrix dense, sticky and accessible', () => {
    const page = read('features/standings/StandingsPage.tsx');
    assert.ok(page.includes('matrix-wrap'), 'stage region scrolls inside its container');
    assert.ok(page.includes('sticky'), 'identity and totals stay in view toward Stage 32');
    assert.ok(page.includes('Legend'), 'bonus calibration legend is present');
    assert.ok(page.includes('Bonus now'), 'current effective bonus is labeled time-dependent');
    assert.ok(page.includes('never shuffle probability'), 'bonus never implies shuffle changes');
    const css = read('features/standings/StandingsPage.css');
    assert.ok(css.includes('.matrix-wrap'), 'matrix container is styled');
    assert.ok(css.includes('overflow-x'), 'narrow screens scroll the container, not the page');
    assert.ok(css.includes('place-win'), 'P1 wins are scannable with non-color cues');
  });
});
