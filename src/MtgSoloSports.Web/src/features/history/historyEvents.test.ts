import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'HistoryPage.tsx'), 'utf8');
const view = readFileSync(join(here, 'HistoryEventView.tsx'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('history postseason events', () => {
  it('lists postseason events as a competition group', () => {
    assert.ok(page.includes('<optgroup label="Postseason">'));
    assert.ok(page.includes('fetchSeasonEvents('));
    assert.ok(page.includes('<HistoryEventView'));
  });

  it('replays event rounds with the shared reveal and group selection', () => {
    assert.ok(view.includes('fetchEventRound('));
    assert.ok(view.includes('<RoundReveal'));
    assert.ok(view.includes('<span>Group</span>'));
    assert.ok(view.includes('fetchEventTeamStandings('));
  });

  it('keeps event selections in the shareable URL', () => {
    assert.ok(app.includes('urlEvent={route.event}'));
    assert.ok(app.includes('event: selection.event'));
  });
});
