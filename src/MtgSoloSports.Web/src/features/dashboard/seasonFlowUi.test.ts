import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const component = readFileSync(join(here, 'SeasonFlow.tsx'), 'utf8');
const page = readFileSync(join(here, 'DashboardPage.tsx'), 'utf8');
const css = readFileSync(join(here, 'DashboardPage.css'), 'utf8');
const hook = readFileSync(join(here, 'useDashboard.ts'), 'utf8');
const live = readFileSync(join(here, '..', 'live', 'LivePage.tsx'), 'utf8');

describe('season flow panel', () => {
  it('runs the next lifecycle event through the existing endpoint, once at a time', () => {
    assert.ok(component.includes('advanceToNextEvent(saveId)'), 'uses the lifecycle endpoint');
    assert.ok(component.includes('if (running)'), 'duplicate submissions are blocked');
    assert.ok(component.includes('aria-busy={running}'), 'running state is announced');
    assert.ok(component.includes('onAdvanced()'), 'dashboard refreshes after each step');
    assert.ok(component.includes('Try again'), 'failures offer a retry');
    assert.ok(component.includes("can't be undone"), 'irreversibility is stated');
  });

  it('renders the steps as an ordered list with the current step marked', () => {
    assert.ok(component.includes('<ol className="flow-steps"'));
    assert.ok(component.includes("aria-current={step.state === 'current' ? 'step' : undefined}"));
    assert.match(css, /\.flow-step\.is-current\s*\{/);
    assert.match(css, /\.flow-step\.is-done\s*\{/);
  });

  it('replaces the developer-facing next action text on the dashboard', () => {
    assert.ok(page.includes('<SeasonFlow'), 'dashboard mounts the flow');
    assert.ok(!page.includes('describeNextAction'), 'old headline is gone');
    assert.ok(!hook.includes('/api/saves/{saveId}'), 'no backend routes in user-facing copy');
  });

  it('offers Play on Live and Run all rounds for round-based events', () => {
    assert.ok(component.includes('Play on Live'));
    assert.ok(component.includes('Run all rounds'));
    assert.ok(component.includes('livePath(saveId, { event: next.liveEvent, season: flow.seasonNumber })'));
  });

  it('points Live to the dashboard once league play is over', () => {
    assert.ok(live.includes('Continue the postseason on the Dashboard'));
  });
});
