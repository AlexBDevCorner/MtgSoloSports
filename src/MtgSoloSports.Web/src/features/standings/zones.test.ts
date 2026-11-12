import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  colorComposition,
  feeder1Zone,
  feeder2Zone,
  feeder3Zone,
  feederZone,
  resolvePostseasonZone,
  superleagueZone,
  zoneForRank,
  zoneLabelForRank,
  zoneLegendFor,
  zoneProvisionalNote,
  zoneStatusPrefix,
  zoneSummaryLine,
} from './zones.ts';

describe('superleague zones', () => {
  it('maps 1-16 safe, 17-24 qualifier, 25-32 relegation', () => {
    assert.equal(superleagueZone(1), 'safe');
    assert.equal(superleagueZone(16), 'safe');
    assert.equal(superleagueZone(17), 'qualifier');
    assert.equal(superleagueZone(24), 'qualifier');
    assert.equal(superleagueZone(25), 'relegation');
    assert.equal(superleagueZone(32), 'relegation');
  });
});

describe('feeder zones', () => {
  it('maps champion, qualifier and safe without quotas', () => {
    assert.equal(feederZone(1), 'champion');
    assert.equal(feederZone(2), 'qualifier');
    assert.equal(feederZone(4), 'qualifier');
    assert.equal(feederZone(5), 'safe');
    assert.equal(feederZone(32), 'safe');
  });
});

describe('zoneForRank', () => {
  it('selects Superleague vs feeder mapping by kind', () => {
    assert.equal(zoneForRank(1, 'Superleague'), 'safe');
    assert.equal(zoneForRank(20, 'Superleague'), 'qualifier');
    assert.equal(zoneForRank(1, 'Feeder'), 'champion');
    assert.equal(zoneForRank(3, 'Feeder'), 'qualifier');
  });

  it('labels zones for display', () => {
    assert.match(zoneLabelForRank(1, 'Superleague'), /Safe/);
    assert.match(zoneLabelForRank(20, 'Superleague'), /Qualifier/);
    assert.match(zoneLabelForRank(30, 'Superleague'), /Relegation/);
    assert.match(zoneLabelForRank(1, 'Feeder'), /auto-promoted/);
  });

  it('keeps the legacy single-feeder mapping without tier data', () => {
    assert.equal(zoneForRank(5, 'Feeder'), 'safe');
    assert.equal(zoneForRank(32, 'Feeder'), 'safe');
    assert.equal(zoneForRank(1, 'Feeder', 'Feeder1'), 'champion');
    assert.equal(zoneForRank(2, 'Feeder', 'Feeder1'), 'qualifier-up');
  });
});

describe('tiered feeder zones', () => {
  it('maps Feeder 1 bands: champion, challengers, safe, incumbents, relegated', () => {
    assert.equal(feeder1Zone(1), 'champion');
    assert.equal(feeder1Zone(2), 'qualifier-up');
    assert.equal(feeder1Zone(4), 'qualifier-up');
    assert.equal(feeder1Zone(5), 'safe');
    assert.equal(feeder1Zone(16), 'safe');
    assert.equal(feeder1Zone(17), 'qualifier-hold');
    assert.equal(feeder1Zone(24), 'qualifier-hold');
    assert.equal(feeder1Zone(25), 'relegated');
    assert.equal(feeder1Zone(32), 'relegated');
    assert.equal(zoneForRank(25, 'Feeder', 'Feeder1'), 'relegated');
  });

  it('maps Feeder 2 bands: promoted, challengers, incumbents, relegated', () => {
    assert.equal(feeder2Zone(1), 'promoted');
    assert.equal(feeder2Zone(8), 'promoted');
    assert.equal(feeder2Zone(9), 'qualifier-up');
    assert.equal(feeder2Zone(16), 'qualifier-up');
    assert.equal(feeder2Zone(17), 'qualifier-hold');
    assert.equal(feeder2Zone(24), 'qualifier-hold');
    assert.equal(feeder2Zone(25), 'relegated');
    assert.equal(zoneForRank(9, 'Feeder', 'Feeder2'), 'qualifier-up');
  });

  it('maps Feeder 3 bands: promoted, challengers, safe', () => {
    assert.equal(feeder3Zone(1), 'promoted');
    assert.equal(feeder3Zone(8), 'promoted');
    assert.equal(feeder3Zone(9), 'qualifier-up');
    assert.equal(feeder3Zone(16), 'qualifier-up');
    assert.equal(feeder3Zone(17), 'safe');
    assert.equal(feeder3Zone(32), 'safe');
    assert.equal(zoneForRank(32, 'Feeder', 'Feeder3'), 'safe');
  });

  it('summarizes zone bands per tier', () => {
    assert.match(zoneSummaryLine('Superleague', 'Superleague'), /1–16 safe/);
    assert.match(zoneSummaryLine('Feeder', 'Feeder1'), /25–32 relegated/);
    assert.match(zoneSummaryLine('Feeder', 'Feeder2'), /1–8 promoted/);
    assert.match(zoneSummaryLine('Feeder', 'Feeder3'), /17–32 safe/);
    assert.match(zoneSummaryLine('Feeder', null), /2–4 qualifier/);
  });
});

describe('colorComposition', () => {
  it('counts per color without applying quotas', () => {
    const rows = [
      { sportingColorName: 'White' },
      { sportingColorName: 'White' },
      { sportingColorName: 'Blue' },
    ];
    assert.deepEqual(colorComposition(rows), [
      { color: 'White', count: 2 },
      { color: 'Blue', count: 1 },
    ]);
  });
});

describe('resolvePostseasonZone post-inaugural', () => {
  it('maps Superleague 1/16/17/24/25/32 with direction and target', () => {
    const ctx = { leagueKind: 'Superleague', leagueLevel: 'Superleague', feederDivision: 0, seasonNumber: 2 };
    assert.equal(resolvePostseasonZone(1, ctx).id, 'sl-safe');
    assert.equal(resolvePostseasonZone(16, ctx).id, 'sl-safe');
    assert.equal(resolvePostseasonZone(17, ctx).id, 'sl-qualifier');
    assert.equal(resolvePostseasonZone(24, ctx).id, 'sl-qualifier');
    assert.equal(resolvePostseasonZone(25, ctx).id, 'sl-relegated');
    assert.equal(resolvePostseasonZone(32, ctx).id, 'sl-relegated');
    assert.match(resolvePostseasonZone(25, ctx).label, /Feeder 1/);
  });

  it('maps Feeder 1 boundaries 1/2/4/5/16/17/24/25/32 for each color division', () => {
    const ctx = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 5, hasTieredPyramid: true };
    assert.equal(resolvePostseasonZone(1, ctx).id, 'f1-champion');
    assert.equal(resolvePostseasonZone(2, ctx).id, 'f1-qualifier-up');
    assert.equal(resolvePostseasonZone(4, ctx).id, 'f1-qualifier-up');
    assert.equal(resolvePostseasonZone(5, ctx).id, 'f1-safe');
    assert.equal(resolvePostseasonZone(16, ctx).id, 'f1-safe');
    assert.equal(resolvePostseasonZone(17, ctx).id, 'f1-qualifier-hold');
    assert.equal(resolvePostseasonZone(24, ctx).id, 'f1-qualifier-hold');
    assert.equal(resolvePostseasonZone(25, ctx).id, 'f1-relegated');
    assert.equal(resolvePostseasonZone(32, ctx).id, 'f1-relegated');
  });

  it('maps Feeder 2 boundaries 1/8/9/16/17/24/25/32', () => {
    const ctx = { leagueKind: 'Feeder', leagueLevel: 'Feeder2', feederDivision: 2, seasonNumber: 3 };
    assert.equal(resolvePostseasonZone(1, ctx).id, 'f2-promoted');
    assert.equal(resolvePostseasonZone(8, ctx).id, 'f2-promoted');
    assert.equal(resolvePostseasonZone(9, ctx).id, 'f2-qualifier-up');
    assert.equal(resolvePostseasonZone(16, ctx).id, 'f2-qualifier-up');
    assert.equal(resolvePostseasonZone(17, ctx).id, 'f2-qualifier-hold');
    assert.equal(resolvePostseasonZone(24, ctx).id, 'f2-qualifier-hold');
    assert.equal(resolvePostseasonZone(25, ctx).id, 'f2-relegated');
    assert.equal(resolvePostseasonZone(32, ctx).id, 'f2-relegated');
  });

  it('maps Feeder 3 boundaries 1/8/9/16/17/32 with no Pool relegation', () => {
    const ctx = { leagueKind: 'Feeder', leagueLevel: 'Feeder3', feederDivision: 3, seasonNumber: 4 };
    assert.equal(resolvePostseasonZone(1, ctx).id, 'f3-promoted');
    assert.equal(resolvePostseasonZone(8, ctx).id, 'f3-promoted');
    assert.equal(resolvePostseasonZone(9, ctx).id, 'f3-qualifier-up');
    assert.equal(resolvePostseasonZone(16, ctx).id, 'f3-qualifier-up');
    assert.equal(resolvePostseasonZone(17, ctx).id, 'f3-safe');
    assert.equal(resolvePostseasonZone(32, ctx).id, 'f3-safe');
    assert.match(resolvePostseasonZone(32, ctx).label, /no ordinary relegation/);
  });

  it('keeps legacy single-tier mapping and never asserts tiered relegation', () => {
    const legacy = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 0, seasonNumber: 6 };
    assert.equal(resolvePostseasonZone(1, legacy).id, 'legacy-champion');
    assert.equal(resolvePostseasonZone(4, legacy).id, 'legacy-qualifier');
    assert.equal(resolvePostseasonZone(5, legacy).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(17, legacy).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(25, legacy).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(32, legacy).id, 'legacy-safe');
    const bare = { leagueKind: 'Feeder', leagueLevel: null, feederDivision: null, seasonNumber: 6 };
    assert.equal(resolvePostseasonZone(25, bare).id, 'legacy-safe');
    // Persisted v1 single-feeder rows carry division 1 after migration, so
    // division alone must not promote them to tiered F1: without F2/F3 in the
    // season shape they stay legacy with no qualifier-hold or relegation.
    const persistedV1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 6, hasTieredPyramid: false };
    assert.equal(resolvePostseasonZone(1, persistedV1).id, 'legacy-champion');
    assert.equal(resolvePostseasonZone(4, persistedV1).id, 'legacy-qualifier');
    assert.equal(resolvePostseasonZone(5, persistedV1).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(17, persistedV1).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(25, persistedV1).id, 'legacy-safe');
    assert.equal(resolvePostseasonZone(32, persistedV1).id, 'legacy-safe');
    const unknownShape = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 6 };
    assert.equal(resolvePostseasonZone(25, unknownShape).id, 'legacy-safe');
    const loadingShape = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 6, hasTieredPyramid: null };
    assert.equal(resolvePostseasonZone(25, loadingShape).id, 'legacy-safe');
  });
});

describe('resolvePostseasonZone Season 1 inaugural', () => {
  it('maps F1 1–4 to inaugural Superleague slots and retains the rest', () => {
    const ctx = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 1, hasTieredPyramid: true };
    assert.equal(resolvePostseasonZone(1, ctx).id, 'inaugural-sl');
    assert.equal(resolvePostseasonZone(4, ctx).id, 'inaugural-sl');
    assert.match(resolvePostseasonZone(1, ctx).label, /Inaugural Superleague/);
    assert.equal(resolvePostseasonZone(5, ctx).id, 'inaugural-retained');
    assert.equal(resolvePostseasonZone(17, ctx).id, 'inaugural-retained');
    assert.equal(resolvePostseasonZone(25, ctx).id, 'inaugural-retained');
    // Persisted v1 Season 1 rows also carry division 1: same inaugural slots.
    const v1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 1, hasTieredPyramid: false };
    assert.equal(resolvePostseasonZone(1, v1).id, 'inaugural-sl');
    assert.equal(resolvePostseasonZone(4, v1).id, 'inaugural-sl');
    assert.equal(resolvePostseasonZone(5, v1).id, 'inaugural-retained');
  });

  it('never shows ordinary qualifiers or demotions for F2/F3 in Season 1', () => {
    const f2 = { leagueKind: 'Feeder', leagueLevel: 'Feeder2', feederDivision: 2, seasonNumber: 1 };
    const f3 = { leagueKind: 'Feeder', leagueLevel: 'Feeder3', feederDivision: 3, seasonNumber: 1 };
    for (const rank of [1, 8, 9, 16, 17, 24, 25, 32]) {
      assert.equal(resolvePostseasonZone(rank, f2).id, 'inaugural-retained', `F2 rank ${rank}`);
      assert.equal(resolvePostseasonZone(rank, f3).id, 'inaugural-retained', `F3 rank ${rank}`);
    }
  });

  it('returns neutral for Superleague rows in Season 1 and unknown tiers', () => {
    const sl1 = { leagueKind: 'Superleague', leagueLevel: 'Superleague', feederDivision: 0, seasonNumber: 1 };
    assert.equal(resolvePostseasonZone(1, sl1).id, 'neutral');
    const unknown = { leagueKind: 'Mystery', leagueLevel: null, feederDivision: null, seasonNumber: 2 };
    // Unknown feeder kind falls back to legacy-safe only for Feeder; other kinds are neutral.
    assert.equal(resolvePostseasonZone(1, unknown).id, 'neutral');
    assert.equal(resolvePostseasonZone(99, { leagueKind: 'Feeder', seasonNumber: 2 }).id, 'neutral');
  });

  it('builds a compact legend in rank order and distinguishes provisional wording', () => {
    const f1 = { leagueKind: 'Feeder', leagueLevel: 'Feeder1', feederDivision: 1, seasonNumber: 2, hasTieredPyramid: true };
    assert.deepEqual(zoneLegendFor(f1).map((z) => z.id), [
      'f1-champion',
      'f1-qualifier-up',
      'f1-safe',
      'f1-qualifier-hold',
      'f1-relegated',
    ]);
    assert.equal(zoneStatusPrefix(false), 'Currently in');
    assert.equal(zoneStatusPrefix(true), 'Final:');
    assert.match(zoneProvisionalNote(false), /if the season ended now/);
    assert.match(zoneProvisionalNote(true), /Final positions/);
  });
});
