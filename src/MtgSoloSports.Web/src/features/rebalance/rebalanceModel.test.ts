import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  balancedText,
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
