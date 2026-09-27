import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  colorComposition,
  feederZone,
  superleagueZone,
  zoneForRank,
  zoneLabelForRank,
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
