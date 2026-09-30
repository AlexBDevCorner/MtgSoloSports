import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'LiveEventView.tsx'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('live event mode', () => {
  it('reuses the live layout and the manual live reveal', () => {
    for (const token of ['live-layout', 'live-sidebar', 'live-main', 'layout="live"', 'autoPlayOnStart={false}', 'round-pills']) {
      assert.ok(view.includes(token), token);
    }
  });

  it('plays one round per click and can finish the event', () => {
    assert.ok(view.includes('playEventRound(saveId, event)'), 'Next Round calls the step endpoint');
    assert.ok(view.includes('Next Round'));
    assert.ok(view.includes('runEventRemaining(saveId, event)'), 'Run remaining calls the one-shot endpoint');
    assert.ok(view.includes('Run remaining rounds'));
    assert.ok(view.includes('if (busy)'), 'duplicate submissions are blocked');
    assert.ok(view.includes('onMutated()'), 'dashboard status refreshes after each action');
  });

  it("offers play controls only for the save's next event and season", () => {
    assert.ok(view.includes('const playable = progress !== null && progress.sourceSeasonNumber === season;'));
    assert.ok(view.includes('complete ? ('), 'completion notice first');
    assert.ok(view.includes(') : playable ? ('), 'Next Round and Run remaining only when playable');
    assert.ok(view.includes('Not the next event'), 'otherwise points back to the Dashboard');
  });

  it('shows team standings and a completion notice', () => {
    assert.ok(view.includes('fetchEventTeamStandings'));
    assert.ok(view.includes('Continue on the Dashboard'));
    assert.ok(view.includes('View results'));
  });

  it('stays on the event after it completes because the URL keeps the event', () => {
    assert.ok(app.includes('<LiveEventView'));
    assert.ok(app.includes('route.event ?? dashboard.data?.status?.eventProgress?.event'));
  });
});
