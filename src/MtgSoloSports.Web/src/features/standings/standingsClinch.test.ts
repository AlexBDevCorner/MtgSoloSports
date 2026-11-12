import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { computeClinchMap } from './clinch.ts';

const here = dirname(fileURLToPath(import.meta.url));
const page = readFileSync(join(here, 'StandingsPage.tsx'), 'utf8');
const api = readFileSync(join(here, 'standingsApi.ts'), 'utf8');

describe('MSS-142 standings clinch wiring', () => {
  it('derives guarantees from official championship totals and save-owned scoring bounds', () => {
    assert.ok(page.includes('computeClinchMap'), 'page computes clinch map');
    assert.ok(
      page.includes('totalChampionshipPointsThousandths'),
      'clinch inputs use official championship totals',
    );
    assert.ok(page.includes('placements.stagesPerSeason'), 'remaining stages come from authoritative placements');
    assert.ok(page.includes('placements.leagueSize'), 'league size comes from save-owned rules');
    assert.ok(page.includes('placements.maxStagePointsThousandths'), 'max award comes from saved scoring config');
    assert.ok(page.includes('placements.minStagePointsThousandths'), 'min award comes from saved scoring config');
    assert.ok(!page.includes('77_000'), 'no hardcoded 77k award in the page');
    assert.ok(!page.includes('77, 67'), 'no hardcoded scoring table in the page');
  });

  it('keys guarantees by athlete and never by visual sort order', () => {
    assert.ok(page.includes('clinchMap.get(row.athleteId'), 'badge lookup is keyed by athlete id');
    assert.ok(!page.includes('clinchMap.get(index'), 'badge never uses render index');
    assert.ok(page.includes('resolvePostseasonZone(totals.seasonRank'), 'zone still uses official rank');
  });

  it('renders a compact confirmed status distinct from the provisional zone color', () => {
    assert.ok(page.includes('<th scope="col">Locked</th>'), 'Locked column exists');
    assert.ok(page.includes('clinch-badge'), 'clinched badge has its own styling');
    assert.ok(page.includes('clinchExplanation'), 'accessible explanation or tooltip renders');
    assert.ok(page.includes('visually-hidden'), 'screen-reader text accompanies badges');
    assert.ok(page.includes('✓'), 'text glyph carries meaning without color');
  });

  it('shows no early marker once the season is officially complete', () => {
    assert.ok(
      page.includes('zoneIsComplete ? undefined : clinchMap.get'),
      'completed seasons suppress early clinch badges',
    );
    assert.ok(api.includes('stagesPerSeason'), 'placements contract carries season size');
    assert.ok(api.includes('maxStagePointsThousandths'), 'placements contract carries scoring max');
    assert.ok(api.includes('minStagePointsThousandths'), 'placements contract carries scoring min');
  });

  it('keeps clinch computation pure across reload and league switch', () => {
    const totals = Array.from({ length: 32 }, (_, i) => ({
      athleteId: i + 1,
      totalChampionshipPointsThousandths: 5_000_000 - i * 100_000,
    }));
    const scoring = {
      completedStages: 31,
      stagesPerSeason: 32,
      leagueSize: 32,
      maxStagePointsThousandths: 77_000,
      minStagePointsThousandths: 1_000,
      isSeasonComplete: false,
    };
    const sl = { leagueKind: 'Superleague', leagueLevel: 'Superleague', feederDivision: 0, seasonNumber: 2 };
    const f1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 2, hasTieredPyramid: true };
    const first = computeClinchMap(totals, scoring, sl);
    const second = computeClinchMap(totals, scoring, sl);
    assert.deepEqual([...second.entries()], [...first.entries()]);
    const otherLeague = computeClinchMap(totals, scoring, f1);
    assert.notDeepEqual([...otherLeague.entries()], [...first.entries()]);
  });
});
