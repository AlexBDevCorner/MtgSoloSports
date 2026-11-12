import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  clinchBadgeText,
  clinchExplanation,
  computeClinchMap,
  type ClinchPoints,
  type ClinchScoring,
} from './clinch.ts';
import { resolvePostseasonZone, type ZoneContext } from './zones.ts';

const MAX = 77_000;
const MIN = 1_000;
const STAGES = 32;
const SIZE = 32;

function scoring(completed: number, overrides: Partial<ClinchScoring> = {}): ClinchScoring {
  return {
    completedStages: completed,
    stagesPerSeason: STAGES,
    leagueSize: SIZE,
    maxStagePointsThousandths: MAX,
    minStagePointsThousandths: MIN,
    isSeasonComplete: false,
    ...overrides,
  };
}

function pointsList(values: number[]): ClinchPoints[] {
  return values.map((points, index) => ({ athleteId: index + 1, totalChampionshipPointsThousandths: points }));
}

function separated(count = SIZE, gap = 100_000, base = 5_000_000): ClinchPoints[] {
  const rows: ClinchPoints[] = [];
  for (let i = 0; i < count; i += 1) {
    rows.push({ athleteId: i + 1, totalChampionshipPointsThousandths: base - i * gap });
  }
  return rows;
}

const SL: ZoneContext = { leagueKind: 'Superleague', leagueLevel: 'Superleague', feederDivision: 0, seasonNumber: 2 };
const F1: ZoneContext = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 5, hasTieredPyramid: true };
const F2: ZoneContext = { leagueKind: 'Feeder', leagueLevel: 'Feeder2', feederDivision: 2, seasonNumber: 3 };
const F3: ZoneContext = { leagueKind: 'Feeder', leagueLevel: 'Feeder3', feederDivision: 3, seasonNumber: 4 };
const LEGACY: ZoneContext = { leagueKind: 'Feeder', leagueLevel: null, feederDivision: null, seasonNumber: 6 };
const INAUGURAL_F1: ZoneContext = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 1, hasTieredPyramid: true };
const INAUGURAL_F2: ZoneContext = { leagueKind: 'Feeder', leagueLevel: 'Feeder2', feederDivision: 2, seasonNumber: 1 };

describe('MSS-142 champion clinch vs near-clinch', () => {
  it('locks champion when the leader is mathematically uncatchable', () => {
    const totals = pointsList([1_000_000, 900_000, ...Array.from({ length: 30 }, () => 100_000)]);
    const map = computeClinchMap(totals, scoring(31), SL);
    const leader = map.get(1)!;
    assert.equal(leader.bestRank, 1);
    assert.equal(leader.worstRank, 1);
    assert.equal(leader.isChampionLocked, true);
    assert.equal(clinchBadgeText(leader), '✓ Clinched champion');
    assert.match(clinchExplanation(leader, 1) ?? '', /guaranteed champion/i);
  });

  it('withholds champion when a tie or overtake is still reachable', () => {
    // Gap exactly one stage swing (77k-1k): second ceiling ties leader floor.
    const leader = 500_000;
    const second = leader - (MAX - MIN);
    const totals = pointsList([leader, second, ...Array.from({ length: 30 }, () => 100_000)]);
    const map = computeClinchMap(totals, scoring(31), SL);
    const first = map.get(1)!;
    assert.equal(first.isChampionLocked, false);
    assert.equal(first.worstRank > 1, true);
    // A non-champion exact-band lock (here Superleague safety 1–16) may still
    // hold; the requirement is only that no false champion appears.
    if (first.guaranteedZone !== null) {
      assert.equal(first.guaranteedZone.id, 'sl-safe');
    }
  });

  it('locks champion one point above the reachable tie', () => {
    const leader = 500_000;
    const second = leader - (MAX - MIN) - 1;
    const totals = pointsList([leader, second, ...Array.from({ length: 30 }, () => 100_000)]);
    const map = computeClinchMap(totals, scoring(31), SL);
    assert.equal(map.get(1)!.isChampionLocked, true);
  });
});

describe('MSS-142 exact-band guarantees', () => {
  it('locks F2 direct promotion for 1–8 without claiming champion', () => {
    // Top eight separated from the rest by more than one stage swing.
    const top = Array.from({ length: 8 }, () => 500_000);
    const rest = Array.from({ length: 24 }, () => 400_000);
    const totals = pointsList([...top, ...rest]);
    const map = computeClinchMap(totals, scoring(31), F2);
    for (let id = 1; id <= 8; id += 1) {
      const entry = map.get(id)!;
      assert.equal(entry.isChampionLocked, false);
      assert.ok(entry.guaranteedZone !== null, `athlete ${id} locked`);
      assert.equal(entry.guaranteedZone!.id, 'f2-promoted');
      assert.match(clinchBadgeText(entry) ?? '', /Promoted/);
    }
    // Ninth cannot be promoted: its ceiling cannot reach the eighth floor.
    const ninth = map.get(9)!;
    assert.equal(ninth.guaranteedZone, null);
  });

  it('locks F1 qualifier challenger 2–4 only when first is out of reach', () => {
    const totals = pointsList([
      1_000_000, 500_000, 500_000, 500_000, 400_000,
      ...Array.from({ length: 27 }, () => 100_000),
    ]);
    const map = computeClinchMap(totals, scoring(31), F1);
    for (const id of [2, 3, 4]) {
      const entry = map.get(id)!;
      assert.equal(entry.isChampionLocked, false);
      assert.equal(entry.guaranteedZone?.id, 'f1-qualifier-up');
    }
  });

  it('never labels a top-four F1 athlete who could still finish first as guaranteed qualifier', () => {
    const totals = pointsList([
      500_000, 500_000 - 10_000, 100_000, 100_000, 100_000,
      ...Array.from({ length: 27 }, () => 50_000),
    ]);
    const map = computeClinchMap(totals, scoring(31), F1);
    const second = map.get(2)!;
    // Best rank 1 (champion zone) but worst rank inside 2–4: spans bands.
    assert.equal(second.bestRank, 1);
    assert.ok(second.worstRank >= 2 && second.worstRank <= 4);
    assert.equal(second.guaranteedZone, null);
    assert.equal(second.isChampionLocked, false);
  });

  it('locks both qualifier directions and distinguishes challenger from incumbent', () => {
    const totals = pointsList([
      1_000_000,
      ...Array.from({ length: 7 }, () => 800_000), // 2–8 safe-ish
      ...Array.from({ length: 8 }, () => 600_000), // 9–16 challenger
      ...Array.from({ length: 8 }, () => 400_000), // 17–24 incumbent
      ...Array.from({ length: 8 }, () => 100_000), // 25–32 relegated
    ]);
    // Widen separation so each octet is isolated by more than one swing.
    const isolated = totals.map((row, index) => ({
      athleteId: row.athleteId,
      totalChampionshipPointsThousandths: 2_000_000 - Math.floor(index / 8) * 200_000,
    }));
    const map = computeClinchMap(isolated, scoring(31), F2);
    assert.equal(map.get(9)!.guaranteedZone?.id, 'f2-qualifier-up');
    assert.equal(map.get(16)!.guaranteedZone?.id, 'f2-qualifier-up');
    assert.equal(map.get(17)!.guaranteedZone?.id, 'f2-qualifier-hold');
    assert.equal(map.get(24)!.guaranteedZone?.id, 'f2-qualifier-hold');
    assert.notEqual(
      map.get(9)!.guaranteedZone?.id,
      map.get(17)!.guaranteedZone?.id,
    );
  });

  it('locks safety and relegation only inside their exact retention bands', () => {
    const isolated = separated(SIZE, 100_000, 5_000_000);
    const sl = computeClinchMap(isolated, scoring(31), SL);
    // Exact single-rank intervals agree with the zone table.
    assert.equal(sl.get(16)!.guaranteedZone?.id, 'sl-safe');
    assert.equal(sl.get(17)!.guaranteedZone?.id, 'sl-qualifier');
    assert.equal(sl.get(24)!.guaranteedZone?.id, 'sl-qualifier');
    assert.equal(sl.get(25)!.guaranteedZone?.id, 'sl-relegated');
    // Spanning the 16/17 boundary is no lock.
    const cross = pointsList([...Array.from({ length: 32 }, (_, i) => 500_000 - (i < 16 ? 0 : 0))]);
    void cross;
    const tight = pointsList([
      ...Array.from({ length: 15 }, () => 600_000),
      500_000, 500_000,
      ...Array.from({ length: 15 }, () => 100_000),
    ]);
    const crossMap = computeClinchMap(tight, scoring(31), SL);
    // Athletes 16/17 tie inside the same octet but the boundary pair spans
    // safe vs qualifier only if constructed to; here equal points span many
    // bands, so neither locks.
    assert.equal(crossMap.get(16)!.guaranteedZone, null);
  });

  it('never labels F3 as relegated under normal rules', () => {
    const isolated = separated(SIZE, 100_000, 5_000_000);
    const map = computeClinchMap(isolated, scoring(31), F3);
    assert.equal(map.get(8)!.guaranteedZone?.id, 'f3-promoted');
    assert.equal(map.get(9)!.guaranteedZone?.id, 'f3-qualifier-up');
    assert.equal(map.get(17)!.guaranteedZone?.id, 'f3-safe');
    assert.equal(map.get(32)!.guaranteedZone?.id, 'f3-safe');
    for (const [, entry] of map) {
      assert.notEqual(entry.guaranteedZone?.movement, 'relegated');
    }
  });
});

describe('MSS-142 boundaries agree with the season zone table', () => {
  it('matches every exact rank for Superleague 16/17/24/25', () => {
    const map = computeClinchMap(separated(), scoring(31), SL);
    for (const [rank, want] of [[16, 'sl-safe'], [17, 'sl-qualifier'], [24, 'sl-qualifier'], [25, 'sl-relegated']] as const) {
      assert.equal(map.get(rank)!.guaranteedZone?.id, want, `rank ${rank}`);
      assert.equal(map.get(rank)!.guaranteedZone?.id, resolvePostseasonZone(rank, SL).id);
    }
  });

  it('matches F1 1/2/4/5/16/17/24/25', () => {
    const map = computeClinchMap(separated(), scoring(31), F1);
    const wants: Array<[number, string]> = [
      [2, 'f1-qualifier-up'], [4, 'f1-qualifier-up'], [5, 'f1-safe'],
      [16, 'f1-safe'], [17, 'f1-qualifier-hold'], [24, 'f1-qualifier-hold'], [25, 'f1-relegated'],
    ];
    for (const [rank, want] of wants) {
      assert.equal(map.get(rank)!.guaranteedZone?.id, want, `F1 rank ${rank}`);
    }
    assert.equal(map.get(1)!.isChampionLocked, true);
  });

  it('matches F2 8/9/16/17/24/25', () => {
    const map = computeClinchMap(separated(), scoring(31), F2);
    const wants: Array<[number, string]> = [
      [8, 'f2-promoted'], [9, 'f2-qualifier-up'], [16, 'f2-qualifier-up'],
      [17, 'f2-qualifier-hold'], [24, 'f2-qualifier-hold'], [25, 'f2-relegated'],
    ];
    for (const [rank, want] of wants) {
      assert.equal(map.get(rank)!.guaranteedZone?.id, want, `F2 rank ${rank}`);
    }
  });

  it('matches F3 8/9/16/17 and inaugural F1 top four', () => {
    const f3 = computeClinchMap(separated(), scoring(31), F3);
    assert.equal(f3.get(8)!.guaranteedZone?.id, 'f3-promoted');
    assert.equal(f3.get(9)!.guaranteedZone?.id, 'f3-qualifier-up');
    assert.equal(f3.get(16)!.guaranteedZone?.id, 'f3-qualifier-up');
    assert.equal(f3.get(17)!.guaranteedZone?.id, 'f3-safe');
    const inaugural = computeClinchMap(separated(), scoring(31), INAUGURAL_F1);
    for (const rank of [1, 2, 3, 4]) {
      if (rank === 1) {
        assert.equal(inaugural.get(rank)!.isChampionLocked, true);
      } else {
        assert.equal(inaugural.get(rank)!.guaranteedZone?.id, 'inaugural-sl', `inaugural rank ${rank}`);
      }
    }
    assert.equal(inaugural.get(5)!.guaranteedZone?.id, 'inaugural-retained');
  });

  it('keeps legacy mapping without tiered relegation', () => {
    const map = computeClinchMap(separated(), scoring(31), LEGACY);
    assert.equal(map.get(1)!.isChampionLocked, true);
    assert.equal(map.get(2)!.guaranteedZone?.id, 'legacy-qualifier');
    assert.equal(map.get(4)!.guaranteedZone?.id, 'legacy-qualifier');
    assert.equal(map.get(5)!.guaranteedZone?.id, 'legacy-safe');
    assert.equal(map.get(17)!.guaranteedZone?.id, 'legacy-safe');
    assert.equal(map.get(25)!.guaranteedZone?.id, 'legacy-safe');
    for (const [, entry] of map) {
      assert.notEqual(entry.guaranteedZone?.movement, 'relegated');
    }
  });
});

describe('MSS-142 uncertainty, completeness and malformed data fail safe', () => {
  it('treats equal points with stages left as fully unresolved', () => {
    const totals = pointsList(Array.from({ length: 32 }, () => 250_000));
    const map = computeClinchMap(totals, scoring(30), SL);
    for (const [, entry] of map) {
      assert.equal(entry.isChampionLocked, false);
      assert.equal(entry.guaranteedZone, null);
      assert.equal(entry.bestRank, 1);
      assert.equal(entry.worstRank, 32);
    }
  });

  it('returns no early marker when zero stages remain or the season is complete', () => {
    const totals = separated();
    assert.deepEqual([...computeClinchMap(totals, scoring(32), SL).values()].filter((e) => e.guaranteedZone !== null || e.isChampionLocked), []);
    assert.deepEqual([...computeClinchMap(totals, scoring(31, { isSeasonComplete: true }), SL).values()].filter((e) => e.guaranteedZone !== null || e.isChampionLocked), []);
    assert.equal(clinchExplanation({ athleteId: 1, bestRank: 1, worstRank: 1, isChampionLocked: true, guaranteedZone: resolvePostseasonZone(1, SL) }, 0), null);
  });

  it('fails safe on malformed scoring, roster and context inputs', () => {
    const totals = separated();
    assert.equal(computeClinchMap(totals, scoring(31, { stagesPerSeason: null }), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31, { leagueSize: null }), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31, { maxStagePointsThousandths: null }), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31, { minStagePointsThousandths: null }), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31, { maxStagePointsThousandths: MIN, minStagePointsThousandths: MAX }), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(99), SL).size, 0);
    assert.equal(computeClinchMap(totals.slice(0, 31), scoring(31), SL).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31), { ...SL, seasonNumber: 0 }).size, 0);
    assert.equal(computeClinchMap(totals, scoring(31), { ...SL, leagueKind: '' }).size, 0);
    assert.equal(computeClinchMap([], scoring(31), SL).size, 0);
    const dup = separated();
    dup[1] = { ...dup[0] };
    assert.equal(computeClinchMap(dup, scoring(31), SL).size, 0);
  });

  it('suppresses vacuous whole-table retention (inaugural F2/F3)', () => {
    const totals = separated();
    const f2 = computeClinchMap(totals, scoring(31), INAUGURAL_F2);
    // Retention covers the whole table (1–32), so no exact-band retention lock
    // is meaningful. An exact rank-1 lock may still hold as champion; every
    // other card must show no guaranteed zone.
    for (const [id, entry] of f2) {
      if (id === 1) {
        assert.equal(entry.isChampionLocked, true);
      } else {
        assert.equal(entry.guaranteedZone, null);
        assert.equal(entry.isChampionLocked, false);
      }
    }
  });

  it('uses saved rules variants instead of hardcoded 77/1/32', () => {
    const custom = scoring(9, {
      stagesPerSeason: 10,
      maxStagePointsThousandths: 10_000,
      minStagePointsThousandths: 5_000,
    });
    // One custom-stage swing is 5_000; a 5_001 gap locks champion.
    const locked = pointsList([100_000, 100_000 - 5_001, ...Array.from({ length: 30 }, () => 10_000)]);
    assert.equal(computeClinchMap(locked, custom, SL).get(1)!.isChampionLocked, true);
    const tied = pointsList([100_000, 100_000 - 5_000, ...Array.from({ length: 30 }, () => 10_000)]);
    assert.equal(computeClinchMap(tied, custom, SL).get(1)!.isChampionLocked, false);
  });

  it('is pure, order-independent and league-specific', () => {
    const totals = separated();
    const first = computeClinchMap(totals, scoring(31), SL);
    const shuffled = [...totals].reverse();
    const second = computeClinchMap(shuffled, scoring(31), SL);
    for (const row of totals) {
      assert.deepEqual(second.get(row.athleteId), first.get(row.athleteId));
    }
    // Same points in another league produce that league's zones, not stale SL zones.
    const f1 = computeClinchMap(totals, scoring(31), F1);
    assert.notEqual(f1.get(25)!.guaranteedZone?.id, first.get(25)!.guaranteedZone?.id);
    assert.equal(f1.get(25)!.guaranteedZone?.id, 'f1-relegated');
  });
});
