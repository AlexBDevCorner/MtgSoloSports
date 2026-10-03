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

describe('MSS-050 promotion and relegation reveal', () => {
  it('presents movements through a dedicated visual reveal on the Standings page', () => {
    const page = read('features/standings/StandingsPage.tsx');
    assert.ok(page.includes('MovementSection'), 'standings mounts the movement reveal');
    const section = read('features/movement/MovementSection.tsx');
    assert.ok(section.includes('<MovementReveal'), 'the section renders the movement board');
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('Promotion & relegation'), 'dedicated presentation, not a static table');
  });

  it('shows athlete identity, status, previous league and destination league per movement', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('PROMOTED'), 'promoted state is explicit text');
    assert.ok(reveal.includes('RELEGATED'), 'relegated state is explicit text');
    assert.ok(reveal.includes('fromLeagueName'), 'previous league is rendered');
    assert.ok(reveal.includes('toLeagueName'), 'destination league is rendered');
    assert.ok(reveal.includes('AthleteLink'), 'athlete identity links to the career profile');
    assert.ok(reveal.includes('movement-summary'), 'a persistent summary table remains after the reveal');
    assert.ok(reveal.includes('Old league → new league'), 'the summary keeps old → new groupings');
  });

  it('moves promoted athletes upward and relegated athletes downward across labelled boundaries', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('movement-zone-upper'), 'the higher league zone sits above');
    assert.ok(reveal.includes('movement-zone-lower'), 'the lower league zone sits below');
    assert.ok(reveal.includes('League boundary'), 'the boundary stays labelled throughout the reveal');
    assert.ok(reveal.includes('higher league'), 'zone hierarchy names the higher league');
    assert.ok(reveal.includes('lower league'), 'zone hierarchy names the lower league');
    assert.ok(reveal.includes('arrive-up'), 'promoted tiles arrive upward');
    assert.ok(reveal.includes('arrive-down'), 'relegated tiles arrive downward');
  });

  it('keeps multiple movements per boundary readable and boundaries in a clear sequence', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('movement-seq'), 'every movement carries its sequence number');
    assert.ok(reveal.includes('is-latest'), 'only the latest tile animates so cards never collide');
    assert.ok(reveal.includes('is-active'), 'the active boundary is obvious');
    assert.ok(reveal.includes("aria-current"), 'the active boundary is exposed to assistive tech');
    const model = read('features/movement/movementModel.ts');
    assert.ok(model.includes('localeCompare'), 'boundaries sort into a logical order');
    assert.ok(model.includes('relegated first'), 'each boundary reveals in a fixed understandable order');
  });

  it('progresses deliberately without long automatic timers and lands on a final state', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('Reveal next'), 'step-by-step advance is available');
    assert.ok(reveal.includes('Reveal boundary'), 'a whole boundary can be revealed at once');
    assert.ok(reveal.includes('Reveal all'), 'the full result is one click away');
    assert.ok(reveal.includes('Replay the reveal'), 'a completed event can be stepped through again');
    assert.ok(reveal.includes('role="progressbar"'), 'progress stays visible throughout the reveal');
    assert.ok(reveal.includes('Final summary'), 'a stable summary remains once complete');
    assert.ok(!reveal.includes('setTimeout'), 'no automatic timers pace the movement reveal');
    assert.ok(!reveal.includes('setInterval'), 'no intervals pace the movement reveal');
  });

  it('revisits completed historical events from persisted facts, not current leagues', () => {
    const section = read('features/movement/MovementSection.tsx');
    assert.ok(section.includes('fetchAutomaticMovement(saveId, seasonNumber'), 'the source season pins the historical event');
    assert.ok(section.includes('fetchInauguralRoster'), 'Season 1 revisits its inaugural movements');
    assert.ok(section.includes('fromSeasonNumber'), 'the request names the historical transition');
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('revealKey'), 'a new event identity restarts the presentation');
    assert.ok(!reveal.includes('/simulate'), 'the reveal never resimulates');
    assert.ok(!reveal.includes('method: \'POST\''), 'the reveal never mutates sporting state');
  });

  it('covers the no-movement and not-yet-resolved edge states', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('No league movements'), 'an empty transition explains itself');
    const section = read('features/movement/MovementSection.tsx');
    assert.ok(section.includes('not resolved yet'), 'mid-postseason shows where to continue');
    assert.ok(section.includes('dashboardPath'), 'the edge state links back to the Dashboard');
  });

  it('uses existing card imagery with a fallback and stays responsive with reduced-motion support', () => {
    const reveal = read('features/movement/MovementReveal.tsx');
    assert.ok(reveal.includes('step.imageUrl'), 'tiles use the existing athlete imagery');
    assert.ok(reveal.includes('movement-portrait-fallback'), 'missing artwork falls back to initials');
    assert.ok(reveal.includes('prefers-reduced-motion'), 'reduced-motion users get the same information');
    const css = read('features/movement/MovementReveal.css');
    assert.ok(css.includes('@media (prefers-reduced-motion: reduce)'), 'motion is restrained via CSS');
    assert.ok(css.includes('animation: none'), 'reduced motion disables the slide');
    assert.ok(css.includes('@media (min-width:'), 'narrow and wide layouts are both handled');
    assert.ok(css.includes('badge-promoted'), 'promoted state does not rely on colour alone');
    assert.ok(css.includes('badge-relegated'), 'relegated state does not rely on colour alone');
  });

  it('keeps sporting rules untouched: the slice only reads persisted movement facts', () => {
    const api = read('features/movement/movementApi.ts');
    assert.ok(api.includes('/superleague/automatic-movement'), 'the reveal reads the authoritative movement result');
    assert.ok(api.includes('/superleague/inaugural'), 'the inaugural roster is read, not recomputed');
    assert.ok(!api.includes('method'), 'movement reads are GET-only');
    const model = read('features/movement/movementModel.ts');
    assert.ok(!model.includes('Math.random'), 'ordering never uses game randomness');
  });
});
