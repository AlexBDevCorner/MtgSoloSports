import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  colorComposition,
  feeder1Zone,
  feeder2Zone,
  feeder3Zone,
  feederZone,
  superleagueZone,
  zoneForRank,
  zoneLabelForRank,
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
