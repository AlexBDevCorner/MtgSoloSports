import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildRevealOrder,
  clampRevealed,
  computeProgressiveStandings,
  indexPlacementsByAthlete,
  nextRevealed,
  speedToIntervalMs,
} from './revealOrder.ts';
import type { RevealPlacement } from './types.ts';

function makePlacement(overrides: Partial<RevealPlacement> & { athleteId: number }): RevealPlacement {
  return {
    name: `Card ${overrides.athleteId}`,
    position: overrides.athleteId,
    baseThousandths: 1000,
    activeBonusThousandths: 0,
    finalThousandths: 1000,
    cumulativeBeforeThousandths: 0,
    cumulativeAfterThousandths: 1000,
    rankBefore: overrides.athleteId,
    rankAfter: overrides.athleteId,
    rankMovement: 0,
    imageUrl: null,
    setCode: null,
    typeLine: 'Creature',
    ...overrides,
  };
}

describe('buildRevealOrder', () => {
  it('reveals the lowest finisher first so the winner lands last', () => {
    const placements = [makePlacement({ athleteId: 1, position: 1 }), makePlacement({ athleteId: 2, position: 2 }), makePlacement({ athleteId: 3, position: 3 })];
    const order = buildRevealOrder(placements);
    assert.deepEqual(
      order.map((row) => row.position),
      [3, 2, 1],
    );
  });

  it('does not mutate the immutable backend payload order', () => {
    const placements = [makePlacement({ athleteId: 1, position: 1 }), makePlacement({ athleteId: 2, position: 2 })];
    const before = placements.map((row) => row.athleteId);
    buildRevealOrder(placements);
    assert.deepEqual(
      placements.map((row) => row.athleteId),
      before,
    );
  });

  it('handles an empty round', () => {
    assert.deepEqual(buildRevealOrder([]), []);
  });
});

describe('computeProgressiveStandings', () => {
  it('shows pre-round scores before anything is revealed', () => {
    const placements = [
      makePlacement({ athleteId: 1, position: 1, cumulativeBeforeThousandths: 5000, cumulativeAfterThousandths: 82000, rankBefore: 2, rankAfter: 1, finalThousandths: 77000 }),
      makePlacement({ athleteId: 2, position: 2, cumulativeBeforeThousandths: 9000, cumulativeAfterThousandths: 76000, rankBefore: 1, rankAfter: 2, finalThousandths: 67000 }),
    ];
    const order = buildRevealOrder(placements);
    const rows = computeProgressiveStandings(indexPlacementsByAthlete(placements), order, 0);
    // Leader before the round still leads: scores are cumulativeBefore verbatim.
    assert.equal(rows[0]!.athleteId, 2);
    assert.equal(rows[0]!.displayedScoreThousandths, 9000);
    assert.equal(rows[0]!.awardedThousandths, 0);
    assert.equal(rows[0]!.baseThousandths, 0);
    assert.equal(rows[0]!.isRevealed, false);
    assert.equal(rows[1]!.athleteId, 1);
  });

  it('moves the standings visibly as cards are revealed', () => {
    const placements = [
      makePlacement({ athleteId: 1, position: 1, cumulativeBeforeThousandths: 5000, cumulativeAfterThousandths: 82000, rankBefore: 2, rankAfter: 1, finalThousandths: 77000 }),
      makePlacement({ athleteId: 2, position: 32, cumulativeBeforeThousandths: 9000, cumulativeAfterThousandths: 10000, rankBefore: 1, rankAfter: 2, finalThousandths: 1000 }),
    ];
    const order = buildRevealOrder(placements);
    // Reveal order is lowest first: athlete 2 (P32) first.
    assert.equal(order[0]!.athleteId, 2);

    const afterOne = computeProgressiveStandings(indexPlacementsByAthlete(placements), order, 1);
    const revealed = afterOne.find((row) => row.athleteId === 2)!;
    assert.equal(revealed.isRevealed, true);
    assert.equal(revealed.awardedThousandths, 1000);
    assert.equal(revealed.displayedScoreThousandths, 10000);

    const full = computeProgressiveStandings(indexPlacementsByAthlete(placements), order, 2);
    assert.equal(full[0]!.athleteId, 1);
    assert.equal(full[0]!.displayedScoreThousandths, 82000);
    assert.equal(full[0]!.currentRank, 1);
    assert.equal(full[0]!.rankDelta, 1);
    assert.equal(full[1]!.athleteId, 2);
    assert.equal(full[1]!.rankDelta, -1);
  });

  it('keeps every sporting value as exact integer thousandths', () => {
    const placements = [
      makePlacement({ athleteId: 7, position: 5, cumulativeBeforeThousandths: 105490, cumulativeAfterThousandths: 148490, baseThousandths: 41000, activeBonusThousandths: 2000, finalThousandths: 43000, rankBefore: 3, rankAfter: 3 }),
    ];
    const order = buildRevealOrder(placements);
    const rows = computeProgressiveStandings(indexPlacementsByAthlete(placements), order, 1);
    assert.equal(rows[0]!.displayedScoreThousandths, 148490);
    assert.equal(rows[0]!.baseThousandths, 41000);
    assert.equal(rows[0]!.awardedThousandths, 43000);
    assert.ok(Number.isInteger(rows[0]!.displayedScoreThousandths));
  });

  it('breaks displayed-score ties deterministically without RNG', () => {
    const placements = [
      makePlacement({ athleteId: 2, position: 2, cumulativeBeforeThousandths: 5000, cumulativeAfterThousandths: 5000, rankBefore: 2, rankAfter: 2 }),
      makePlacement({ athleteId: 1, position: 1, cumulativeBeforeThousandths: 5000, cumulativeAfterThousandths: 5000, rankBefore: 1, rankAfter: 1 }),
    ];
    const order = buildRevealOrder(placements);
    const first = computeProgressiveStandings(indexPlacementsByAthlete(placements), order, 2);
    const second = computeProgressiveStandings(indexPlacementsByAthlete(placements), [...order].reverse(), 2);
    assert.deepEqual(
      first.map((row) => row.athleteId),
      second.map((row) => row.athleteId),
    );
    assert.deepEqual(
      first.map((row) => row.athleteId),
      [1, 2],
    );
  });
});

describe('reveal progress helpers', () => {
  it('clamps revealed counts to the persisted total', () => {
    assert.equal(clampRevealed(-1, 32), 0);
    assert.equal(clampRevealed(33, 32), 32);
    assert.equal(clampRevealed(4, 32), 4);
    assert.equal(clampRevealed(Number.NaN, 32), 0);
    assert.equal(clampRevealed(5, 0), 0);
  });

  it('advances exactly one card per tick', () => {
    assert.equal(nextRevealed(0, 32), 1);
    assert.equal(nextRevealed(32, 32), 32);
  });

  it('maps speeds to distinct positive intervals', () => {
    const slow = speedToIntervalMs('slow');
    const normal = speedToIntervalMs('normal');
    const fast = speedToIntervalMs('fast');
    const turbo = speedToIntervalMs('turbo');
    for (const value of [slow, normal, fast, turbo]) {
      assert.ok(Number.isInteger(value) && value > 0);
    }
    assert.ok(slow > normal && normal > fast && fast > turbo);
  });
});
