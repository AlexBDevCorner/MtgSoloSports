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

describe('MSS-040 navigational links', () => {
  it('exposes real distinct URLs from the main shell with current-page semantics', () => {
    const shell = read('features/shell/AppShell.tsx');
    assert.ok(shell.includes('<Link'), 'shell navigates with semantic anchors');
    assert.ok(shell.includes('ariaCurrent'), 'active page keeps aria-current semantics');
    assert.ok(!shell.includes('onNavigate('), 'button-only onNavigate is gone');
    assert.ok(!shell.includes('<button'), 'navigational buttons are replaced by links');
    for (const token of ['savesPath', 'dashboardPath', 'livePath', 'historyPath', 'recordsPath', 'cupsPath']) {
      assert.ok(shell.includes(token), `shell links to ${token}`);
    }
  });

  it('opens athlete profiles with correct-save links that support new tabs', () => {
    for (const file of [
      'features/live/LivePage.tsx',
      'features/history/HistoryPage.tsx',
      'features/dashboard/DashboardPage.tsx',
      'features/records/RecordsPage.tsx',
      'features/cups/ColorCupPage.tsx',
      'features/cups/TypeCupPage.tsx',
      'features/reveal/RoundReveal.tsx',
      'features/reveal/RevealBoard.tsx',
    ]) {
      const source = read(file);
      assert.ok(
        source.includes('AthleteLink') || source.includes('athletePath'),
        `${file} links to athlete profiles`,
      );
      assert.ok(!source.includes('window.open'), `${file} never uses window.open`);
    }
    const board = read('features/reveal/RevealBoard.tsx');
    assert.ok(!board.includes('onSelectAthlete'), 'reveal board no longer uses callback-only opens');
    const reveal = read('features/reveal/RoundReveal.tsx');
    assert.ok(!reveal.includes('onSelectAthlete'), 'round reveal no longer uses callback-only opens');
  });

  it('keeps buttons for actions and selectors, not navigation', () => {
    const live = read('features/live/LivePage.tsx');
    assert.ok(live.includes('Next Round'), 'simulation stays a button action');
    assert.ok(live.includes('Complete Stage'), 'stage completion stays a button action');
    assert.ok(live.includes('<select'), 'league selection stays a selector control');
    const router = read('features/routing/router.tsx');
    assert.ok(router.includes('metaKey'), 'plain clicks route SPA-style');
    assert.ok(router.includes('ctrlKey'), 'Ctrl/Cmd-click keeps native new-tab behavior');
    assert.ok(router.includes('event.button !== 0'), 'middle-click keeps native behavior');
  });

  it('uses route save ids authoritatively without cross-tab leakage', () => {
    const app = read('App.tsx');
    assert.ok(app.includes('useBrowserRoute'), 'address bar drives the displayed page');
    assert.ok(app.includes('routeSaveId'), 'route save id is authoritative for requests');
    assert.ok(app.includes('readLastSelectedSave'), 'last-selected is only an entry fallback');
    assert.ok(!app.includes("addEventListener('storage'"), 'no storage listener redirects other tabs');
    assert.ok(!app.includes('window.open'), 'no window.open substitute for links');
    const routes = read('features/routing/routes.ts');
    assert.ok(routes.includes('localStorage'), 'route docs explain the storage fallback boundary');
  });

  it('leaves API routes and static assets to the backend fallback', () => {
    const program = readFileSync(
      join(webRoot, '..', '..', 'MtgSoloSports', 'Program.cs'),
      'utf8',
    );
    assert.ok(program.includes('MapFallbackToFile("index.html")'), 'SPA entry serves UI routes');
    assert.ok(
      program.includes('MapFallback("/api/{*path}"'),
      'unknown /api routes stay JSON 404s',
    );
  });
});
