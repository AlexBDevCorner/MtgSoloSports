import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseRoute, livePath, qualifierLivePath } from '../routing/routes.ts';
import {
  canonicalQualifierLiveParams,
  nextQualifierLiveParam,
  parseQualifierLiveParam,
  qualifierLiveParam,
} from '../qualifiers/qualifierModel.ts';

const SAVE = '11111111-1111-1111-1111-111111111111';
const here = dirname(fileURLToPath(import.meta.url));
const view = readFileSync(join(here, 'LiveQualifierView.tsx'), 'utf8');
const eventView = readFileSync(join(here, 'LiveEventView.tsx'), 'utf8');
const app = readFileSync(join(here, '..', '..', 'App.tsx'), 'utf8');

describe('MSS-069 qualifier Live identity', () => {
  it('keeps 17 distinct Live URLs: season + qualifier identity + round', () => {
    assert.equal(canonicalQualifierLiveParams().length, 17);
    assert.equal(canonicalQualifierLiveParams()[0], 'superleague');
    assert.equal(canonicalQualifierLiveParams()[1], 'f1f2-white');
    assert.equal(canonicalQualifierLiveParams()[8], 'f1f2-colorless');
    assert.equal(canonicalQualifierLiveParams()[9], 'f2f3-white');
    assert.equal(qualifierLiveParam('Feeder1Feeder2', 'White'), 'f1f2-white');
    assert.equal(qualifierLiveParam('Superleague', null), 'superleague');
    assert.deepEqual(parseQualifierLiveParam('f1f2-white'), {
      boundary: 'Feeder1Feeder2',
      color: 'White',
    });
    assert.equal(nextQualifierLiveParam('superleague'), 'f1f2-white');
    assert.equal(nextQualifierLiveParam('f2f3-colorless'), null);
  });

  it('round-trips qualifier Live URLs without simulating', () => {
    const path = qualifierLivePath(SAVE, 'f1f2-white', 2, 5);
    assert.equal(path, `/saves/${SAVE}/live?round=5&event=qualifier&season=2&qualifier=f1f2-white`);
    assert.deepEqual(parseRoute(`/saves/${SAVE}/live`, '?event=qualifier&qualifier=f1f2-white&season=2&round=5'), {
      name: 'live',
      saveId: SAVE,
      leagueId: null,
      round: 5,
      event: 'qualifier',
      selection: null,
      transition: null,
      eventSeason: 2,
      group: null,
      qualifier: 'f1f2-white',
    });
    assert.equal(
      parseRoute(`/saves/${SAVE}/live`, '?event=qualifier&qualifier=bogus&season=2').name,
      'live',
    );
    const bogus = parseRoute(`/saves/${SAVE}/live`, '?event=qualifier&qualifier=bogus&season=2');
    assert.equal((bogus as { qualifier: string | null }).qualifier, null);
    assert.equal(livePath(SAVE, { event: 'qualifier', season: 2 }), `/saves/${SAVE}/live?event=qualifier&season=2`);
  });

  it('shows each event as Round N / 16, never / 272', () => {
    assert.ok(view.includes('Round ${roundsPlayed} / ${totalRounds}'), 'per-event counter');
    assert.ok(!view.includes('/ 272') || view.includes('272 total rounds') === false, 'no phase total under event title');
    assert.ok(eventView.includes("event === 'qualifier' ? 16"), 'Superleague Live fixed to 16');
    assert.ok(view.includes('Top-8 cutoff') || view.includes('top 8'), 'cutoff visible');
    assert.ok(view.includes('QUALIFIED') && view.includes('ELIMINATED'), 'text badges, never color alone');
    assert.ok(view.includes('8 incumbents vs 8 challengers'), 'incumbent/challenger origins');
  });

  it('reuses the reveal stack and offers Live navigation without Dashboard trips', () => {
    for (const token of ['RoundReveal', 'layout="live"', 'autoPlayOnStart={false}', 'round-pills', 'live-layout']) {
      assert.ok(view.includes(token), token);
    }
    assert.ok(view.includes('playFeederQualifierRound'), 'Next Round persists one round');
    assert.ok(view.includes('runRemainingQualifiers'), 'fast-forward from Live');
    assert.ok(view.includes('Play next qualifier on Live'), 'next legal qualifier');
    assert.ok(view.includes('All 17 qualifiers'), 'overview link');
    assert.ok(view.includes('onMutated()'), 'dashboard refresh after mutation');
    assert.ok(!/new Random|Random\.Shared|Math\.random/.test(view), 'never simulates here');
    assert.ok(app.includes('<LiveQualifierView'), 'App renders feeder Live');
  });

  it('opens pending feeders before running and replays completed ones', () => {
    assert.ok(view.includes('No rounds played yet.'), 'pending opens with 0 rounds');
    assert.ok(view.includes('Press Next Round to simulate round 1'), 'pending can start');
    assert.ok(view.includes('Pick a round to replay it.'), 'completed replay');
    assert.ok(view.includes('fetchQualifierRounds') && view.includes('fetchQualifierRound'), 'read models, never resimulate');
  });
});
