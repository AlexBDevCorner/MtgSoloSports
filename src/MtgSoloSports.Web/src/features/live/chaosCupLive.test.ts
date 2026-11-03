import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'ChaosCupLiveView.tsx'), 'utf8');
const api = readFileSync(join(here, '..', 'cups', 'chaosCupApi.ts'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('MSS-109 Chaos Cup read-only navigation on Live', () => {
  it('reuses the Live layout and the shared manual reveal', () => {
    for (const token of ['live-layout', 'live-sidebar', 'live-main', 'RoundReveal', 'layout="live"', 'autoPlayOnStart={false}', 'round-pills']) {
      assert.ok(view.includes(token), token);
    }
    assert.ok(view.includes('aria-label="Chaos Cup navigation"'), 'sidebar is a labelled landmark');
    assert.ok(!view.includes('className="dashboard"'), 'no stacked dashboard wrapper on Live');
  });

  it('fetches the edition plus one group and one round, never the whole tournament', () => {
    assert.ok(api.includes('fetchChaosEdition'), 'edition overview without payloads');
    assert.ok(api.includes('fetchChaosGroup'), 'one 32-athlete group on demand');
    assert.ok(api.includes('fetchChaosRound'), 'one persisted round for replay');
    assert.ok(!/PlayNext|FinishGroup|RunChaos|advance-next|complete-season/i.test(view), 'no simulation endpoints on this view');
    assert.ok(!/new Random|Random\.Shared|Math\.random/.test(view), 'never simulates here');
  });

  it('navigates six stages with a compact group grid and locked undrawn stages', () => {
    assert.ok(view.includes('Stages'), 'six-stage strip');
    assert.ok(view.includes('Groups'), 'compact group grid');
    assert.ok(view.includes('not drawn yet'), 'undrawn stages stay locked, never completed');
    assert.ok(view.includes('Follow active stage'), 'mixed-completion replay can return to the active stage');
    assert.ok(view.includes('onSelect('), 'stable stage/group/round selection travels in the URL');
  });

  it('renders 32-athlete standings with athlete links and a prominent P16/P17 cut', () => {
    assert.ok(view.includes('AthleteLink'), 'athlete profiles use correct-save links');
    assert.ok(view.includes('live-qualifying-row'), 'qualifying rows are highlighted');
    assert.ok(view.includes('live-cut-label'), 'cut line is visually prominent, not color alone');
    assert.ok(view.includes('provisional'), 'cutoff is labelled provisional until completion');
    assert.ok(view.includes('official'), 'completed groups show the official cutoff');
    assert.ok(view.includes('Final'), 'the Final carries official places without a cut');
  });

  it('handles loading, missing and failed reads with retry', () => {
    assert.ok(view.includes('Loading Chaos Cup'), 'edition loading state');
    assert.ok(view.includes('Chaos Cup unavailable'), 'edition error state');
    assert.ok(view.includes('Group unavailable') || view.includes('Group not drawn'), 'group states stay distinct');
    assert.ok(view.includes('Round unavailable') || view.includes('No rounds yet'), 'round states stay distinct');
    assert.ok(view.includes('Retry'), 'failed reads can be retried');
  });

  it('is reachable from a stable Live URL without simulation controls', () => {
    assert.ok(app.includes('ChaosCupLiveView'), 'App renders the Chaos view on Live');
    assert.ok(app.includes("route.event === 'chaos-cup'"), 'chaos-cup owns its Live branch');
    assert.ok(!view.includes('Next Round') && !view.includes('Complete Stage') && !view.includes('Run remaining'), 'no simulation buttons');
  });
});
