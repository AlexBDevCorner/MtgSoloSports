import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  boundariesForInaugural,
  boundariesForMovement,
  buildMovementBoundaries,
  buildRevealOrder,
  directionGlyph,
  directionLabel,
  stepDescription,
} from './movementModel.ts';
import type { AutomaticMovement, InauguralRosterMember, MovementMember } from './movementApi.ts';

function member(overrides: Partial<MovementMember> & { athleteId: number }): MovementMember {
  return {
    name: `Card ${overrides.athleteId}`,
    sportingColor: 'White',
    fromLeagueId: 1,
    fromLeagueName: 'White League',
    fromSeasonRank: 1,
    toLeagueId: 9,
    toLeagueName: 'Superleague',
    movementKind: 'AutomaticPromotion',
    imageUrl: null,
    ...overrides,
  };
}

function movement(): AutomaticMovement {
  return {
    saveId: 'save-1',
    fromSeasonNumber: 2,
    toSeasonNumber: 3,
    superleagueLeagueId: 9,
    superleagueLeagueName: 'Superleague',
    safe: [],
    promoted: [
      member({ athleteId: 1, fromLeagueName: 'White League', toLeagueName: 'Superleague' }),
      member({ athleteId: 2, fromLeagueName: 'Blue League', toLeagueName: 'Superleague' }),
    ],
    relegated: [
      member({
        athleteId: 3,
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        fromSeasonRank: 25,
        toLeagueId: 2,
        toLeagueName: 'Blue League',
        movementKind: 'AutomaticRelegation',
      }),
      member({
        athleteId: 4,
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        fromSeasonRank: 25,
        toLeagueId: 1,
        toLeagueName: 'White League',
        movementKind: 'AutomaticRelegation',
      }),
      member({
        athleteId: 5,
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        fromSeasonRank: 26,
        toLeagueId: 1,
        toLeagueName: 'White League',
        movementKind: 'AutomaticRelegation',
      }),
    ],
    qualifierIncumbents: [],
    qualifierChallengers: [],
    poolCount: 1792,
    movementCount: 48,
  };
}

describe('movement boundaries', () => {
  it('groups promoted and relegated athletes by feeder boundary', () => {
    const boundaries = boundariesForMovement(movement());
    assert.deepEqual(
      boundaries.map((boundary) => boundary.key),
      ['Blue League', 'White League'],
    );
    const white = boundaries.find((boundary) => boundary.key === 'White League')!;
    assert.equal(white.promoted.length, 1);
    assert.equal(white.relegated.length, 2);
    assert.equal(white.superleagueName, 'Superleague');
  });

  it('reveals relegated athletes before promoted athletes inside a boundary', () => {
    const boundaries = boundariesForMovement(movement());
    const white = boundaries.find((boundary) => boundary.key === 'White League')!;
    assert.deepEqual(
      white.steps.map((step) => step.direction),
      ['relegated', 'relegated', 'promoted'],
    );
    // Relegated athletes keep source-rank order so the deepest drop reveals first.
    assert.deepEqual(
      white.steps.map((step) => step.athleteId),
      [4, 5, 1],
    );
  });

  it('flattens boundaries into one global reveal order', () => {
    const order = buildRevealOrder(boundariesForMovement(movement()));
    assert.equal(order.length, 5);
    assert.deepEqual(
      order.map((step) => step.athleteId),
      [3, 2, 4, 5, 1],
    );
  });

  it('keeps an empty transition readable as no boundaries', () => {
    assert.deepEqual(buildMovementBoundaries([], [], 'Superleague'), []);
    assert.deepEqual(buildRevealOrder([]), []);
  });

  it('models the inaugural roster as promotions-only boundaries', () => {
    const members: InauguralRosterMember[] = [
      {
        athleteId: 11,
        name: 'White One',
        sportingColor: 'White',
        fromLeagueId: 1,
        fromLeagueName: 'White League',
        fromSeasonRank: 1,
        imageUrl: 'https://img.test/white.jpg',
      },
      {
        athleteId: 12,
        name: 'Blue One',
        sportingColor: 'Blue',
        fromLeagueId: 2,
        fromLeagueName: 'Blue League',
        fromSeasonRank: 2,
        imageUrl: null,
      },
    ];
    const boundaries = boundariesForInaugural(members, 'Superleague');
    assert.equal(boundaries.length, 2);
    for (const boundary of boundaries) {
      assert.equal(boundary.relegated.length, 0);
      assert.equal(boundary.promoted.length, 1);
      assert.equal(boundary.steps[0]!.direction, 'promoted');
      assert.equal(boundary.steps[0]!.toLeagueName, 'Superleague');
    }
    const white = boundaries.find((boundary) => boundary.key === 'White League')!;
    assert.equal(white.steps[0]!.imageUrl, 'https://img.test/white.jpg');
  });

  it('labels direction with text and glyphs, never colour alone', () => {
    assert.equal(directionLabel('promoted'), 'PROMOTED');
    assert.equal(directionLabel('relegated'), 'RELEGATED');
    assert.equal(directionGlyph('promoted'), '▲');
    assert.equal(directionGlyph('relegated'), '▼');
    assert.equal(
      stepDescription({
        key: 'promoted:1',
        athleteId: 1,
        name: 'Card 1',
        sportingColor: 'White',
        fromLeagueName: 'White League',
        fromSeasonRank: 1,
        toLeagueName: 'Superleague',
        direction: 'promoted',
        imageUrl: null,
      }),
      'Card 1 promoted from White League to Superleague',
    );
  });
});
