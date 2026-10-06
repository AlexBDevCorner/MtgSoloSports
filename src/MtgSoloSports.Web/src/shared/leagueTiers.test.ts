import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  groupLeaguesByTier,
  leagueLevelLabel,
  movementKindLabel,
  movementProvenance,
  qualifierBoundaryFromSegment,
  qualifierBoundaryLabel,
  qualifierBoundarySegment,
  tierBonusLabel,
  TIER_BONUS_SCALE_LINE,
} from './leagueTiers.ts';

describe('leagueLevelLabel', () => {
  it('labels tiers from data without parsing names', () => {
    assert.equal(leagueLevelLabel('Superleague', 0, 'Superleague'), 'Superleague');
    assert.equal(leagueLevelLabel('Feeder1', 1, 'Feeder'), 'Feeder 1');
    assert.equal(leagueLevelLabel('Feeder2', 2, 'Feeder'), 'Feeder 2');
    assert.equal(leagueLevelLabel('Feeder3', 3, 'Feeder'), 'Feeder 3');
  });

  it('keeps the original single-feeder label for historical v1 rows', () => {
    assert.equal(leagueLevelLabel('Feeder1', 0, 'Feeder'), 'Feeder');
    assert.equal(leagueLevelLabel(null, 0, 'Feeder'), 'Feeder');
    assert.equal(leagueLevelLabel(null, null, 'Feeder'), 'Feeder');
    assert.equal(leagueLevelLabel(null, null, 'Superleague'), 'Superleague');
  });
});

describe('tierBonusLabel', () => {
  it('reports the canonical bonus scale per tier', () => {
    assert.equal(tierBonusLabel('Superleague'), '2×');
    assert.equal(tierBonusLabel('Feeder1'), '1×');
    assert.equal(tierBonusLabel('Feeder2'), '½×');
    assert.equal(tierBonusLabel('Feeder3'), '¼×');
    assert.ok(TIER_BONUS_SCALE_LINE.includes('2×'));
  });
});

describe('qualifier boundaries', () => {
  it('labels every boundary with text, never color alone', () => {
    assert.equal(qualifierBoundaryLabel('Superleague', null), 'Superleague qualifier');
    assert.equal(
      qualifierBoundaryLabel('Feeder1Feeder2', 'White'),
      'Feeder 1 ↔ Feeder 2 · White',
    );
    assert.equal(
      qualifierBoundaryLabel('Feeder2Feeder3', 'Black'),
      'Feeder 2 ↔ Feeder 3 · Black',
    );
  });

  it('round-trips route segments', () => {
    assert.equal(qualifierBoundarySegment('Feeder1Feeder2'), 'f1f2');
    assert.equal(qualifierBoundarySegment('Feeder2Feeder3'), 'f2f3');
    assert.equal(qualifierBoundarySegment('Superleague'), 'superleague');
    assert.equal(qualifierBoundaryFromSegment('f1f2'), 'Feeder1Feeder2');
    assert.equal(qualifierBoundaryFromSegment('f2f3'), 'Feeder2Feeder3');
    assert.equal(qualifierBoundaryFromSegment('superleague'), 'Superleague');
    assert.equal(qualifierBoundaryFromSegment('nope'), null);
  });
});

describe('movement provenance', () => {
  it('separates automatic, qualifier-field and structural kinds', () => {
    assert.equal(movementProvenance('AutomaticPromotion'), 'automatic');
    assert.equal(movementProvenance('FeederAutomaticRelegation'), 'automatic');
    assert.equal(movementProvenance('QualifierChallenger'), 'qualifier-field');
    assert.equal(movementProvenance('FeederQualifierIncumbent'), 'qualifier-field');
    assert.equal(movementProvenance('RebalanceDraw'), 'structural');
    assert.equal(movementProvenance('RebalanceUp'), 'structural');
    assert.equal(movementProvenance('TierUpgradeSeed'), 'structural');
    assert.equal(movementProvenance('InauguralPromotion'), 'inaugural');
  });

  it('labels kinds for display', () => {
    assert.equal(movementKindLabel('FeederAutomaticPromotion'), 'Automatically promoted');
    assert.equal(movementKindLabel('FeederQualifierChallenger'), 'Qualifier challenger');
    assert.equal(movementKindLabel('RebalanceUp'), 'Moved up (structural)');
  });
});

describe('groupLeaguesByTier', () => {
  it('buckets Superleague, F1/F2/F3 and legacy feeders by data', () => {
    const groups = groupLeaguesByTier([
      { leagueId: 9, name: 'White League F2', kind: 'Feeder', feederDivision: 2, leagueLevel: 'Feeder2' },
      { leagueId: 1, name: 'Superleague', kind: 'Superleague', feederDivision: 0, leagueLevel: 'Superleague' },
      { leagueId: 2, name: 'White League', kind: 'Feeder', feederDivision: 1, leagueLevel: 'Feeder1' },
      { leagueId: 3, name: 'Old League', kind: 'Feeder', feederDivision: 0, leagueLevel: 'Feeder1' },
      { leagueId: 4, name: 'Black League F3', kind: 'Feeder', feederDivision: 3, leagueLevel: 'Feeder3' },
    ]);
    assert.equal(groups.superleague.length, 1);
    assert.equal(groups.feeder1.length, 1);
    assert.equal(groups.feeder2.length, 1);
    assert.equal(groups.feeder3.length, 1);
    assert.equal(groups.legacyFeeder.length, 1);
    assert.equal(groups.legacyFeeder[0]!.name, 'Old League');
  });
});
