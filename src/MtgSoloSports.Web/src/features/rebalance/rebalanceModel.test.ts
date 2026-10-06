import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  balancedText,
  buildRebalanceCascades,
  buildRebalanceLeagues,
  buildRebalanceRevealOrder,
  churnLine,
  churnSummary,
  movementGlyph,
  movementLabel,
  rosterCheckText,
  stepDescription,
} from './rebalanceModel.ts';
import type { RebalanceMovementMember, RebalanceResult } from './rebalanceApi.ts';

function member(overrides: Partial<RebalanceMovementMember> & { athleteId: number }): RebalanceMovementMember {
  return {
    name: `Card ${overrides.athleteId}`,
    sportingColor: 'White',
    fromLeagueId: 1,
    fromLeagueName: 'White League',
    toLeagueId: 9,
    toLeagueName: 'Superleague',
    kind: 'SuperleagueDeparture',
    fromSeasonRank: 1,
    imageUrl: null,
    ...overrides,
  };
}

function result(overrides: Partial<RebalanceResult> = {}): RebalanceResult {
  return {
    saveId: 'save-1',
    fromSeasonNumber: 2,
    toSeasonNumber: 3,
    colors: [
      {
        leagueId: 11,
        leagueName: 'White League',
        sportingColor: 'White',
        startingCount: 32,
        departedCount: 2,
        returnedCount: 1,
        provisionalCount: 31,
        displacedCount: 0,
        drawnCount: 1,
        finalCount: 32,
      },
      {
        leagueId: 12,
        leagueName: 'Blue League',
        sportingColor: 'Blue',
        startingCount: 32,
        departedCount: 1,
        returnedCount: 3,
        provisionalCount: 34,
        displacedCount: 2,
        drawnCount: 0,
        finalCount: 32,
      },
      {
        leagueId: 13,
        leagueName: 'Red League',
        sportingColor: 'Red',
        startingCount: 32,
        departedCount: 1,
        returnedCount: 1,
        provisionalCount: 32,
        displacedCount: 0,
        drawnCount: 0,
        finalCount: 32,
      },
      {
        leagueId: 14,
        leagueName: 'Green League',
        sportingColor: 'Green',
        startingCount: 32,
        departedCount: 0,
        returnedCount: 0,
        provisionalCount: 32,
        displacedCount: 0,
        drawnCount: 0,
        finalCount: 32,
      },
    ],
    draws: [
      member({
        athleteId: 101,
        name: 'White Draw',
        sportingColor: 'White',
        fromLeagueId: 0,
        fromLeagueName: 'Common Pool',
        toLeagueId: 11,
        toLeagueName: 'White League',
        kind: 'RebalanceDraw',
        fromSeasonRank: 0,
      }),
    ],
    displaced: [
      member({
        athleteId: 201,
        name: 'Blue Low',
        sportingColor: 'Blue',
        fromLeagueId: 12,
        fromLeagueName: 'Blue League',
        toLeagueId: 0,
        toLeagueName: 'Common Pool',
        kind: 'RebalanceDisplacement',
        fromSeasonRank: 32,
      }),
      member({
        athleteId: 202,
        name: 'Blue Lower',
        sportingColor: 'Blue',
        fromLeagueId: 12,
        fromLeagueName: 'Blue League',
        toLeagueId: 0,
        toLeagueName: 'Common Pool',
        kind: 'RebalanceDisplacement',
        fromSeasonRank: 31,
      }),
    ],
    departed: [
      member({ athleteId: 1, name: 'White Champ', fromSeasonRank: 1 }),
      member({ athleteId: 2, name: 'White Second', fromSeasonRank: 2 }),
      member({
        athleteId: 3,
        name: 'Blue Champ',
        sportingColor: 'Blue',
        fromLeagueId: 2,
        fromLeagueName: 'Blue League',
        fromSeasonRank: 1,
      }),
      member({
        athleteId: 4,
        name: 'Red Champ',
        sportingColor: 'Red',
        fromLeagueId: 3,
        fromLeagueName: 'Red League',
        toLeagueId: 9,
        fromSeasonRank: 1,
      }),
    ],
    returned: [
      member({
        athleteId: 5,
        name: 'White Return',
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        toLeagueId: 11,
        toLeagueName: 'White League',
        kind: 'SuperleagueReturn',
        fromSeasonRank: 25,
      }),
      member({
        athleteId: 6,
        name: 'Blue Return A',
        sportingColor: 'Blue',
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        toLeagueId: 12,
        toLeagueName: 'Blue League',
        kind: 'SuperleagueReturn',
        fromSeasonRank: 26,
      }),
      member({
        athleteId: 7,
        name: 'Blue Return B',
        sportingColor: 'Blue',
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        toLeagueId: 12,
        toLeagueName: 'Blue League',
        kind: 'SuperleagueReturn',
        fromSeasonRank: 27,
      }),
      member({
        athleteId: 8,
        name: 'Blue Return C',
        sportingColor: 'Blue',
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        toLeagueId: 12,
        toLeagueName: 'Blue League',
        kind: 'SuperleagueReturn',
        fromSeasonRank: 28,
      }),
      member({
        athleteId: 9,
        name: 'Red Return',
        sportingColor: 'Red',
        fromLeagueId: 9,
        fromLeagueName: 'Superleague',
        toLeagueId: 13,
        toLeagueName: 'Red League',
        kind: 'SuperleagueReturn',
        fromSeasonRank: 25,
      }),
    ],
    totalDrawn: 1,
    totalDisplaced: 2,
    totalDeparted: 4,
    totalReturned: 5,
    poolCount: 1760,
    movementCount: 3,
    ...overrides,
  };
}

describe('rebalance leagues', () => {
  it('groups every movement type inside its own feeder league context', () => {
    const leagues = buildRebalanceLeagues(result());
    assert.deepEqual(
      leagues.map((league) => league.key),
      ['Blue League', 'Green League', 'Red League', 'White League'],
    );
    const white = leagues.find((league) => league.key === 'White League')!;
    assert.equal(white.departed.length, 2);
    assert.equal(white.returned.length, 1);
    assert.equal(white.drawn.length, 1);
    assert.equal(white.displaced.length, 0);
    const blue = leagues.find((league) => league.key === 'Blue League')!;
    assert.equal(blue.departed.length, 1);
    assert.equal(blue.returned.length, 3);
    assert.equal(blue.displaced.length, 2);
    // No athlete leaks between league contexts.
    assert.deepEqual(
      white.steps.map((step) => step.athleteId).sort((a, b) => a - b),
      [1, 2, 5, 101],
    );
  });

  it('reveals the sporting sequence inside each league', () => {
    const leagues = buildRebalanceLeagues(result());
    const white = leagues.find((league) => league.key === 'White League')!;
    assert.deepEqual(
      white.steps.map((step) => step.movementType),
      ['to-superleague', 'to-superleague', 'returning', 'drawn'],
    );
    const blue = leagues.find((league) => league.key === 'Blue League')!;
    assert.deepEqual(
      blue.steps.map((step) => step.movementType),
      ['to-superleague', 'returning', 'returning', 'returning', 'to-pool', 'to-pool'],
    );
  });

  it('keeps underfilled leagues drawing and overfilled leagues displacing, never both', () => {
    const leagues = buildRebalanceLeagues(result());
    const white = leagues.find((league) => league.key === 'White League')!;
    assert.equal(white.provisionalCount, 31);
    assert.equal(rosterCheckText(white), '31 / 32');
    assert.equal(white.drawn.length, 1);
    assert.equal(white.displaced.length, 0);
    const blue = leagues.find((league) => league.key === 'Blue League')!;
    assert.equal(blue.provisionalCount, 34);
    assert.equal(rosterCheckText(blue), '34 / 32');
    assert.equal(blue.displaced.length, 2);
    assert.equal(blue.drawn.length, 0);
    for (const league of leagues) {
      assert.ok(league.displaced.length === 0 || league.drawn.length === 0);
      assert.equal(balancedText(league), '32 / 32 — BALANCED');
    }
  });

  it('orders overflow athletes worst-ranked first and keeps draws in backend order', () => {
    const leagues = buildRebalanceLeagues(result());
    const blue = leagues.find((league) => league.key === 'Blue League')!;
    assert.deepEqual(
      blue.displaced.map((step) => step.fromSeasonRank),
      [32, 31],
    );
    const white = leagues.find((league) => league.key === 'White League')!;
    assert.deepEqual(
      white.drawn.map((step) => step.athleteId),
      [101],
    );
  });

  it('handles exactly-balanced and unchanged leagues without extra steps', () => {
    const leagues = buildRebalanceLeagues(result());
    const red = leagues.find((league) => league.key === 'Red League')!;
    assert.equal(red.provisionalCount, 32);
    assert.equal(red.needsPoolAdjustment, false);
    assert.equal(red.cleanlyBalanced, true);
    assert.equal(red.hasChanges, true);
    assert.deepEqual(
      red.steps.map((step) => step.movementType),
      ['to-superleague', 'returning'],
    );
    const green = leagues.find((league) => league.key === 'Green League')!;
    assert.equal(green.hasChanges, false);
    assert.equal(green.total, 0);
    assert.deepEqual(green.steps, []);
    assert.deepEqual(buildRebalanceRevealOrder(leagues).length, 4 + 5 + 1 + 2);
  });

  it('flattens leagues into one stable global reveal order', () => {
    const leagues = buildRebalanceLeagues(result());
    const order = buildRebalanceRevealOrder(leagues);
    // Blue first (alphabetical), then Red, then White; Green contributes nothing.
    assert.ok(order.length > 0);
    assert.equal(order[0]!.name, 'Blue Champ');
    const whiteStart = order.findIndex((step) => step.name === 'White Champ');
    const whiteDraw = order.findIndex((step) => step.name === 'White Draw');
    assert.ok(whiteStart >= 0 && whiteDraw > whiteStart);
    // Revisiting the same persisted result reveals the same athletes.
    assert.deepEqual(
      buildRebalanceRevealOrder(buildRebalanceLeagues(result())).map((step) => step.key),
      order.map((step) => step.key),
    );
  });

  it('labels every movement type with text and glyphs, never colour alone', () => {
    assert.equal(movementLabel('to-superleague'), 'TO SUPERLEAGUE');
    assert.equal(movementLabel('returning'), 'RETURNING');
    assert.equal(movementLabel('to-pool'), 'TO COMMON POOL');
    assert.equal(movementLabel('drawn'), 'DRAWN FROM POOL');
    assert.equal(movementGlyph('to-superleague'), '▲');
    assert.equal(movementGlyph('returning'), '▼');
    assert.equal(
      stepDescription({
        key: 'SuperleagueDeparture:1',
        athleteId: 1,
        name: 'Card 1',
        sportingColor: 'White',
        fromLeagueName: 'White League',
        toLeagueName: 'Superleague',
        fromSeasonRank: 1,
        kind: 'SuperleagueDeparture',
        movementType: 'to-superleague',
        imageUrl: null,
      }),
      'Card 1 to Superleague from White League',
    );
  });

  it('summarises overall churn per league and in total', () => {
    const input = result();
    const leagues = buildRebalanceLeagues(input);
    const churn = churnSummary(input, leagues);
    assert.equal(churn.totalAffected, 12);
    assert.equal(churn.changedLeagueCount, 3);
    const line = churnLine(churn);
    assert.ok(line.includes('12 affected athletes'));
    assert.ok(line.includes('4 to Superleague'));
    assert.ok(line.includes('5 returning'));
    assert.ok(line.includes('2 to pool'));
    assert.ok(line.includes('1 drawn'));
  });
});

function tieredResult(): RebalanceResult {
  const base = result();
  return {
    ...base,
    colors: [
      { leagueId: 21, leagueName: 'White League', sportingColor: 'White', startingCount: 32, departedCount: 1, returnedCount: 1, provisionalCount: 32, displacedCount: 0, drawnCount: 0, finalCount: 32, feederDivision: 1, rebalancedUpIn: 1, rebalancedUpOut: 0, rebalancedDownIn: 0, rebalancedDownOut: 2 },
      { leagueId: 22, leagueName: 'White League F2', sportingColor: 'White', startingCount: 32, departedCount: 0, returnedCount: 0, provisionalCount: 32, displacedCount: 0, drawnCount: 0, finalCount: 32, feederDivision: 2, rebalancedUpIn: 2, rebalancedUpOut: 1, rebalancedDownIn: 2, rebalancedDownOut: 2 },
      { leagueId: 23, leagueName: 'White League F3', sportingColor: 'White', startingCount: 32, departedCount: 0, returnedCount: 0, provisionalCount: 32, displacedCount: 1, drawnCount: 1, finalCount: 32, feederDivision: 3, rebalancedUpIn: 0, rebalancedUpOut: 2, rebalancedDownIn: 2, rebalancedDownOut: 0 },
    ],
    departed: [
      member({ athleteId: 1, name: 'White Champ', fromLeagueId: 21, fromLeagueName: 'White League', toLeagueId: 9, toLeagueName: 'Superleague', kind: 'SuperleagueDeparture', fromSeasonRank: 1 }),
    ],
    returned: [
      member({ athleteId: 2, name: 'White Back', fromLeagueId: 9, fromLeagueName: 'Superleague', toLeagueId: 21, toLeagueName: 'White League', kind: 'SuperleagueReturn', fromSeasonRank: 25 }),
    ],
    displaced: [
      member({ athleteId: 3, name: 'White Low', fromLeagueId: 23, fromLeagueName: 'White League F3', toLeagueId: 0, toLeagueName: 'Common Pool', kind: 'RebalanceDisplacement', fromSeasonRank: 32 }),
    ],
    draws: [
      member({ athleteId: 4, name: 'White Draw', fromLeagueId: 0, fromLeagueName: 'Common Pool', toLeagueId: 23, toLeagueName: 'White League F3', kind: 'RebalanceDraw', fromSeasonRank: 0 }),
    ],
    rebalancedUp: [
      member({ athleteId: 5, name: 'White Up', fromLeagueId: 22, fromLeagueName: 'White League F2', toLeagueId: 21, toLeagueName: 'White League', kind: 'RebalanceUp', fromSeasonRank: 1 }),
    ],
    rebalancedDown: [
      member({ athleteId: 6, name: 'White Down', fromLeagueId: 21, fromLeagueName: 'White League', toLeagueId: 22, toLeagueName: 'White League F2', kind: 'RebalanceDown', fromSeasonRank: 32 }),
    ],
    totalDeparted: 1,
    totalReturned: 1,
    totalDisplaced: 1,
    totalDrawn: 1,
    totalRebalancedUp: 1,
    totalRebalancedDown: 1,
  };
}

describe('tiered rebalance cascades (MSS-060)', () => {
  it('attributes Superleague moves to F1 and pool moves to F3 only', () => {
    const leagues = buildRebalanceLeagues(tieredResult());
    assert.equal(leagues.length, 3);
    const f1 = leagues.find((league) => league.leagueName === 'White League')!;
    assert.equal(f1.feederDivision, 1);
    assert.equal(f1.departed.length, 1);
    assert.equal(f1.returned.length, 1);
    assert.equal(f1.displaced.length, 0);
    assert.equal(f1.drawn.length, 0);
    const f3 = leagues.find((league) => league.leagueName === 'White League F3')!;
    assert.equal(f3.departed.length, 0);
    assert.equal(f3.returned.length, 0);
    assert.equal(f3.displaced.length, 1);
    assert.equal(f3.drawn.length, 1);
  });

  it('shows structural up/down arrivals and departures per division', () => {
    const leagues = buildRebalanceLeagues(tieredResult());
    const f1 = leagues.find((league) => league.leagueName === 'White League')!;
    assert.equal(f1.upIn, 1);
    assert.equal(f1.downOut, 2);
    assert.deepEqual(f1.structuralIn.map((step) => step.movementType), ['up']);
    assert.deepEqual(f1.structuralOut.map((step) => step.movementType), ['down']);
    const f2 = leagues.find((league) => league.leagueName === 'White League F2')!;
    assert.deepEqual(f2.structuralOut.map((step) => step.name), ['White Up']);
    assert.deepEqual(f2.structuralIn.map((step) => step.name), ['White Down']);
  });

  it('groups divisions into per-color cascades ordered F1 → F2 → F3', () => {
    const cascades = buildRebalanceCascades(tieredResult());
    assert.equal(cascades.length, 1);
    assert.equal(cascades[0]!.sportingColor, 'White');
    assert.deepEqual(
      cascades[0]!.divisions.map((league) => league.leagueName),
      ['White League', 'White League F2', 'White League F3'],
    );
    // Structural moves touch two divisions but count once per color.
    assert.equal(cascades[0]!.total, 6);
  });

  it('reveals each structural athlete once across the cascade', () => {
    const leagues = buildRebalanceLeagues(tieredResult());
    const order = buildRebalanceRevealOrder(leagues);
    assert.equal(order.length, 6);
    assert.deepEqual(
      order.map((step) => step.athleteId).sort((a, b) => a - b),
      [1, 2, 3, 4, 5, 6],
    );
  });

  it('labels structural moves with text and counts them in churn', () => {
    assert.equal(movementLabel('up'), 'UP (STRUCTURAL)');
    assert.equal(movementLabel('down'), 'DOWN (STRUCTURAL)');
    const input = tieredResult();
    const churn = churnSummary(input, buildRebalanceLeagues(input));
    assert.equal(churn.totalUp, 1);
    assert.equal(churn.totalDown, 1);
    assert.equal(churn.totalAffected, 6);
    assert.ok(churnLine(churn).includes('1 up / 1 down the cascade (structural)'));
  });
});
