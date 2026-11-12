import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolvePostseasonZone } from './zones.ts';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'StandingsPage.tsx'), 'utf8');

describe('MSS-141 standings zones source wiring', () => {
  it('resolves zones from the official season rank, not the visual row index', () => {
    assert.ok(
      page.includes('resolvePostseasonZone(totals.seasonRank'),
      'zone uses totals.seasonRank (official rank), never the sorted row index',
    );
    assert.ok(
      !page.includes('resolvePostseasonZone(index'),
      'zone never resolves from a render index',
    );
  });

  it('derives zones from tier identity and season phase, not names or colors', () => {
    assert.ok(page.includes('leagueKind'), 'zone context carries league kind');
    assert.ok(page.includes('feederDivision'), 'zone context carries persisted feeder division');
    assert.ok(page.includes('seasonNumber'), 'zone context carries season number');
    assert.ok(page.includes('hasTieredPyramid'), 'zone context carries season shape (F2/F3 presence)');
    assert.ok(!page.includes('leagueName.includes'), 'no display-name parsing for zones');
    assert.ok(!page.includes('leagueName.toLowerCase'), 'no display-name parsing for zones');
  });

  it('keeps sporting values, rank, links and selection while adding zones', () => {
    assert.ok(page.includes('<th scope="col">Zone</th>'), 'Zone column exists');
    assert.ok(page.includes('Champ pts'), 'championship points remain');
    assert.ok(page.includes('AthleteLink'), 'athlete links remain');
    assert.ok(page.includes('totals.isChampion'), 'champion title treatment remains');
    assert.ok(page.includes('zoneLegendFor'), 'legend renders from the single zone source');
    assert.ok(page.includes('zoneProvisionalNote'), 'provisional vs final wording renders');
  });

  it('labels final vs provisional without claiming guarantees early', () => {
    assert.ok(page.includes('if the season ended now'), 'provisional rows say “if the season ended now”');
    assert.ok(page.includes('nothing is guaranteed'), 'provisional rows disclaim guarantees');
    assert.ok(page.includes('· final'), 'completed tables stay labeled final');
  });
});

describe('MSS-141 rank drives zones under reordering', () => {
  it('returns the same zone for a rank regardless of render order', () => {
    const f1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 2, hasTieredPyramid: true };
    // Simulate a table sorted by points where rank 25 renders first and rank 1 renders last.
    const renderOrder = [25, 1];
    const zones = renderOrder.map((rank) => resolvePostseasonZone(rank, f1).id);
    assert.deepEqual(zones, ['f1-relegated', 'f1-champion']);
  });

  it('treats persisted v1 division-1 rows without F2/F3 as legacy, not tiered F1', () => {
    const persistedV1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 6, hasTieredPyramid: false };
    assert.equal(resolvePostseasonZone(17, persistedV1).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(25, persistedV1).id, 'legacy-safe');
    const tieredF1 = { ...persistedV1, hasTieredPyramid: true };
    assert.equal(resolvePostseasonZone(17, tieredF1).id, 'f1-qualifier-hold');
    assert.equal(resolvePostseasonZone(25, tieredF1).id, 'f1-relegated');
  });
});
