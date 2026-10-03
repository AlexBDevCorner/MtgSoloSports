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

describe('MSS-051 feeder-league rebalance reveal', () => {
  it('presents rebalancing through a dedicated visual reveal on the Standings page', () => {
    const page = read('features/standings/StandingsPage.tsx');
    assert.ok(page.includes('RebalanceSection'), 'standings mounts the rebalance reveal');
    const section = read('features/rebalance/RebalanceSection.tsx');
    assert.ok(section.includes('<RebalanceReveal'), 'the section renders the rebalance board');
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('Feeder rebalance'), 'dedicated presentation, not a static table');
    assert.ok(reveal.includes('rebalance-league'), 'leagues stay grouped one at a time');
  });

  it('follows the sporting sequence with explicit source, destination and reason per athlete', () => {
    const model = read('features/rebalance/rebalanceModel.ts');
    assert.ok(model.includes('departed'), 'departures to Superleague are modelled');
    assert.ok(model.includes('returned'), 'returns from Superleague are modelled');
    assert.ok(model.includes('displaced'), 'overflow to the pool is modelled');
    assert.ok(model.includes('drawn'), 'pool draws are modelled');
    assert.ok(model.includes('TO SUPERLEAGUE'), 'departures carry an explicit label');
    assert.ok(model.includes('RETURNING'), 'returns carry an explicit label');
    assert.ok(model.includes('TO COMMON POOL'), 'pool outflow carries an explicit label');
    assert.ok(model.includes('DRAWN FROM POOL'), 'pool inflow carries an explicit label');
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('MovementBadge'), 'tiles carry a movement badge with the reason');
    assert.ok(reveal.includes('fromLeagueName'), 'source league is rendered');
    assert.ok(reveal.includes('toLeagueName'), 'destination league is rendered');
    assert.ok(reveal.includes('AthleteLink'), 'athlete identity links to the career profile');
    assert.ok(reveal.includes('rebalance-summary'), 'a persistent summary table remains after the reveal');
  });

  it('keeps roster counts visible from start through the roster check to balanced', () => {
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('32 / 32'), 'the starting size stays visible');
    assert.ok(reveal.includes('rosterCheckText'), 'the over/underfilled check is explicit');
    assert.ok(reveal.includes('BALANCED'), 'the final state is an explicit 32 / 32 balanced state');
    assert.ok(reveal.includes('After Superleague'), 'the count change after Superleague movement is shown');
    const model = read('features/rebalance/rebalanceModel.ts');
    assert.ok(model.includes('provisionalCount'), 'the provisional count drives the pool story');
    assert.ok(model.includes('finalCount'), 'the final count must be 32');
  });

  it('focuses the reveal on changed athletes with a visible common pool', () => {
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('Common Pool'), 'the pool is a visible destination/source');
    assert.ok(reveal.includes('only affected athletes shown'), 'the pool stays compact, not a full roster');
    assert.ok(reveal.includes('No changes'), 'unchanged leagues render a compact state');
    assert.ok(reveal.includes('hasChanges'), 'unchanged leagues skip progressive steps');
    assert.ok(reveal.includes('No pool adjustment'), 'balanced-after-movement leagues skip pool steps cleanly');
    assert.ok(reveal.includes('Face down'), 'drawn identities hide briefly before the reveal');
  });

  it('progresses deliberately without long automatic timers and lands on a final state', () => {
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('Reveal next'), 'step-by-step advance is available');
    assert.ok(reveal.includes('Reveal league'), 'a whole league can be revealed at once');
    assert.ok(reveal.includes('Reveal all'), 'the full result is one click away');
    assert.ok(reveal.includes('Replay the reveal'), 'a completed event can be stepped through again');
    assert.ok(reveal.includes('role="progressbar"'), 'progress stays visible throughout the reveal');
    assert.ok(reveal.includes('Final summary'), 'a stable summary remains once complete');
    assert.ok(reveal.includes('Per-league counts'), 'per-league movement counts persist');
    assert.ok(!reveal.includes('setTimeout'), 'no automatic timers pace the rebalance reveal');
    assert.ok(!reveal.includes('setInterval'), 'no intervals pace the rebalance reveal');
  });

  it('revisits completed historical events from persisted facts, not current leagues', () => {
    const section = read('features/rebalance/RebalanceSection.tsx');
    assert.ok(
      section.includes('fetchRebalanceResult(saveId, seasonNumber'),
      'the source season pins the historical event',
    );
    assert.ok(section.includes('fromSeasonNumber'), 'the request names the historical transition');
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('revealKey'), 'a new event identity restarts the presentation');
    assert.ok(!reveal.includes('/simulate'), 'the reveal never resimulates');
    assert.ok(!reveal.includes("method: 'POST'"), 'the reveal never mutates sporting state');
    const api = read('features/rebalance/rebalanceApi.ts');
    assert.ok(api.includes('/superleague/rebalance'), 'the reveal reads the authoritative rebalance result');
    assert.ok(api.includes('fromSeason'), 'historical draws stay pinned to their season');
    assert.ok(!api.includes('Math.random'), 'the frontend never rerolls pool draws');
    const model = read('features/rebalance/rebalanceModel.ts');
    assert.ok(!model.includes('Math.random'), 'ordering never uses game randomness');
  });

  it('uses existing card imagery with a fallback and stays responsive with reduced-motion support', () => {
    const reveal = read('features/rebalance/RebalanceReveal.tsx');
    assert.ok(reveal.includes('step.imageUrl'), 'tiles use the existing athlete imagery');
    assert.ok(reveal.includes('rebalance-portrait-fallback'), 'missing artwork falls back to initials');
    assert.ok(reveal.includes('prefers-reduced-motion'), 'reduced-motion users get the same information');
    const css = read('features/rebalance/RebalanceReveal.css');
    assert.ok(css.includes('@media (prefers-reduced-motion: reduce)'), 'motion is restrained via CSS');
    assert.ok(css.includes('animation: none'), 'reduced motion disables the slide');
    assert.ok(css.includes('@media (min-width:'), 'narrow and wide layouts are both handled');
    assert.ok(css.includes('badge-rebalance-to-superleague'), 'departures do not rely on colour alone');
    assert.ok(css.includes('badge-rebalance-drawn'), 'draws do not rely on colour alone');
    assert.ok(css.includes('arrive-up'), 'departures and draws arrive upward');
    assert.ok(css.includes('arrive-down'), 'returns and pool outflow arrive downward');
  });
});
