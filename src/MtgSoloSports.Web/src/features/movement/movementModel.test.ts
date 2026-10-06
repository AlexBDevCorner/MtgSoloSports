import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  boundariesForInaugural,
  boundariesForMovement,
  buildMovementBoundaries,
  buildRevealOrder,
  buildTieredMovementBoundaries,
  directionGlyph,
  directionLabel,
  stepDescription,
} from './movementModel.ts';
import type { AutomaticMovement, FeederMovementMember, FeederMovements, InauguralRosterMember, MovementMember } from './movementApi.ts';

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
      member({ athleteId: 2, sportingColor: 'Blue', fromLeagueName: 'Blue League', toLeagueName: 'Superleague' }),
    ],
    relegated: [
      member({
        athleteId: 3,
        sportingColor: 'Blue',
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

  it('labels direction with text and glyphs, never colour alone', () => {    assert.equal(directionLabel('promoted'), 'PROMOTED');
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

function feederMember(overrides: Partial<FeederMovementMember> & { athleteId: number }): FeederMovementMember {
  return {
    name: `Card ${overrides.athleteId}`,
    sportingColor: 0,
    sportingColorName: 'White',
    boundaryId: 1,
    boundary: 'Feeder1Feeder2',
    fromLeagueId: 2,
    fromLeagueName: 'White League F2',
    fromLeagueLevel: 'Feeder2',
    fromSeasonRank: 1,
    toLeagueId: 1,
    toLeagueName: 'White League',
    toLeagueLevel: 'Feeder1',
    movementKind: 'FeederAutomaticPromotion',
    imageUrl: null,
    ...overrides,
  };
}

describe('tiered movement boundaries (MSS-060)', () => {
  it('groups F1↔Superleague movement by color with qualifier designations as notes', () => {
    const sl: AutomaticMovement = {
      ...movement(),
      qualifierIncumbents: [
        member({ athleteId: 10, fromLeagueName: 'Superleague', toLeagueName: 'Superleague', movementKind: 'QualifierIncumbent', sportingColor: 'White' }),
      ],
      qualifierChallengers: [
        member({ athleteId: 11, fromLeagueName: 'White League', toLeagueName: 'White League', movementKind: 'QualifierChallenger', fromSeasonRank: 2, sportingColor: 'White' }),
      ],
    };
    const boundaries = buildTieredMovementBoundaries(sl, null);
    assert.equal(boundaries.length, 2);
    assert.ok(boundaries.every((boundary) => boundary.boundaryLabel === 'Feeder 1 ↔ Superleague'));
    const white = boundaries.find((boundary) => boundary.key === 'Superleague:White')!;
    assert.equal(white.promoted.length, 1);
    assert.equal(white.relegated.length, 2);
    assert.match(white.qualifierNote ?? '', /1 incumbent.*1 challenger/);
    // Qualifier designations never become steps.
    assert.equal(white.steps.length, 3);
  });

  it('groups feeder boundaries by boundary then color from tier identity', () => {
    const feeders: FeederMovements = {
      saveId: 'save-1',
      fromSeasonNumber: 2,
      toSeasonNumber: 3,
      movementCount: 4,
      movements: [
        feederMember({ athleteId: 1 }),
        feederMember({ athleteId: 2, movementKind: 'FeederAutomaticRelegation', fromLeagueId: 1, fromLeagueName: 'White League', fromLeagueLevel: 'Feeder1', fromSeasonRank: 25, toLeagueId: 2, toLeagueName: 'White League F2', toLeagueLevel: 'Feeder2' }),
        feederMember({ athleteId: 3, movementKind: 'FeederQualifierIncumbent', fromLeagueId: 1, fromLeagueName: 'White League', fromLeagueLevel: 'Feeder1', fromSeasonRank: 17, toLeagueId: 1, toLeagueName: 'White League', toLeagueLevel: 'Feeder1' }),
        feederMember({ athleteId: 4, boundaryId: 2, boundary: 'Feeder2Feeder3', movementKind: 'FeederQualifierChallenger', fromLeagueId: 3, fromLeagueName: 'White League F3', fromLeagueLevel: 'Feeder3', fromSeasonRank: 9, toLeagueId: 3, toLeagueName: 'White League F3', toLeagueLevel: 'Feeder3', sportingColorName: 'White' }),
      ],
    };
    const boundaries = buildTieredMovementBoundaries(null, feeders);
    assert.deepEqual(
      boundaries.map((boundary) => boundary.key),
      ['Feeder1Feeder2:White', 'Feeder2Feeder3:White'],
    );
    const f1f2 = boundaries[0]!;
    assert.equal(f1f2.boundaryLabel, 'Feeder 1 ↔ Feeder 2');
    assert.equal(f1f2.superleagueName, 'White League');
    assert.equal(f1f2.feederLeagueName, 'White League F2');
    assert.deepEqual(
      f1f2.steps.map((step) => step.direction),
      ['relegated', 'promoted'],
    );
    assert.match(f1f2.qualifierNote ?? '', /1 incumbent/);
    const f2f3 = boundaries[1]!;
    assert.equal(f2f3.steps.length, 0);
    assert.match(f2f3.qualifierNote ?? '', /1 challenger/);
  });

  it('orders reveal top-down: Superleague boundary before feeder boundaries', () => {
    const boundaries = buildTieredMovementBoundaries(movement(), {
      saveId: 'save-1',
      fromSeasonNumber: 2,
      toSeasonNumber: 3,
      movementCount: 1,
      movements: [feederMember({ athleteId: 1 })],
    });
    assert.deepEqual(
      boundaries.map((boundary) => boundary.boundaryLabel),
      ['Feeder 1 ↔ Superleague', 'Feeder 1 ↔ Superleague', 'Feeder 1 ↔ Feeder 2'],
    );
  });
});
