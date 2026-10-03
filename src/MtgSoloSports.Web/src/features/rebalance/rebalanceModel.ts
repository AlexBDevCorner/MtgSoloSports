import type { RebalanceMovementMember, RebalanceResult } from './rebalanceApi.ts';

/**
 * Pure presentation model for the feeder-rebalance board.
 *
 * The backend stays authoritative for who moves: this slice only groups the
 * persisted `departed`/`returned`/`displaced`/`draws` facts by feeder league
 * and orders the reveal in the sporting sequence (departures, returns, pool
 * outflow, pool inflow). No sporting math, no RNG, DOM-free so unit-testable.
 */

export type RebalanceMovementType = 'to-superleague' | 'returning' | 'to-pool' | 'drawn';

export interface RebalanceStep {
  /** Stable identity for React keys and aria references. */
  key: string;
  athleteId: number;
  name: string;
  sportingColor: string;
  fromLeagueName: string;
  toLeagueName: string;
  fromSeasonRank: number;
  kind: string;
  movementType: RebalanceMovementType;
  imageUrl: string | null;
}

export interface RebalanceLeague {
  /** Feeder league name, stable across seasons (e.g. "White League"). */
  key: string;
  leagueName: string;
  sportingColor: string;
  startingCount: number;
  departedCount: number;
  returnedCount: number;
  provisionalCount: number;
  displacedCount: number;
  drawnCount: number;
  finalCount: number;
  departed: RebalanceStep[];
  returned: RebalanceStep[];
  displaced: RebalanceStep[];
  drawn: RebalanceStep[];
  /** Reveal order inside this league: the sporting sequence. */
  steps: RebalanceStep[];
  total: number;
  /** True when at least one athlete moved for this league. */
  hasChanges: boolean;
  /** True when the league needed a pool draw or displacement. */
  needsPoolAdjustment: boolean;
  /** True when the league reached exactly 32 without pool help. */
  cleanlyBalanced: boolean;
}

function toMovementType(kind: string): RebalanceMovementType {
  if (kind === 'SuperleagueDeparture') {
    return 'to-superleague';
  }
  if (kind === 'SuperleagueReturn') {
    return 'returning';
  }
  if (kind === 'RebalanceDisplacement') {
    return 'to-pool';
  }
  return 'drawn';
}

function toStep(member: RebalanceMovementMember): RebalanceStep {
  const movementType = toMovementType(member.kind);
  return {
    key: `${member.kind}:${member.athleteId}`,
    athleteId: member.athleteId,
    name: member.name,
    sportingColor: member.sportingColor,
    fromLeagueName: member.fromLeagueName,
    toLeagueName: member.toLeagueName,
    fromSeasonRank: member.fromSeasonRank,
    kind: member.kind,
    movementType,
    imageUrl: member.imageUrl,
  };
}

function sortByRankThenName(steps: RebalanceStep[], descending = false): RebalanceStep[] {
  return [...steps].sort((a, b) => {
    if (a.fromSeasonRank !== b.fromSeasonRank) {
      return descending ? b.fromSeasonRank - a.fromSeasonRank : a.fromSeasonRank - b.fromSeasonRank;
    }
    return a.name.localeCompare(b.name) || a.athleteId - b.athleteId;
  });
}

/**
 * Groups rebalance facts by feeder league.
 *
 * The `colors` array is the source of truth for the league list (eight
 * feeders, one per sporting color). Movements carry the same sporting color
 * as their feeder, so grouping by color keeps every athlete inside its own
 * league context even though source/next league row ids differ per season.
 * Leagues sort alphabetically so multi-league events read in a logical order;
 * inside a league the sporting sequence is departures, returns, pool outflow,
 * then pool inflow. Draws keep the backend's persisted order (the
 * authoritative draw result) instead of being resorted.
 */
export function buildRebalanceLeagues(result: RebalanceResult): RebalanceLeague[] {
  const departedByColor = new Map<string, RebalanceStep[]>();
  const returnedByColor = new Map<string, RebalanceStep[]>();
  const displacedByColor = new Map<string, RebalanceStep[]>();
  const drawnByColor = new Map<string, RebalanceStep[]>();

  for (const member of result.departed) {
    const list = departedByColor.get(member.sportingColor) ?? [];
    list.push(toStep(member));
    departedByColor.set(member.sportingColor, list);
  }
  for (const member of result.returned) {
    const list = returnedByColor.get(member.sportingColor) ?? [];
    list.push(toStep(member));
    returnedByColor.set(member.sportingColor, list);
  }
  for (const member of result.displaced) {
    const list = displacedByColor.get(member.sportingColor) ?? [];
    list.push(toStep(member));
    displacedByColor.set(member.sportingColor, list);
  }
  for (const member of result.draws) {
    const list = drawnByColor.get(member.sportingColor) ?? [];
    list.push(toStep(member));
    drawnByColor.set(member.sportingColor, list);
  }

  return [...result.colors]
    .sort((a, b) => a.leagueName.localeCompare(b.leagueName))
    .map((color): RebalanceLeague => {
      const departed = sortByRankThenName(departedByColor.get(color.sportingColor) ?? []);
      const returned = sortByRankThenName(returnedByColor.get(color.sportingColor) ?? []);
      // Overflow athletes leave worst-ranked first so the lowest-ranked rule reads clearly.
      const displaced = sortByRankThenName(displacedByColor.get(color.sportingColor) ?? [], true);
      const drawn = drawnByColor.get(color.sportingColor) ?? [];
      const steps = [...departed, ...returned, ...displaced, ...drawn];
      const total = steps.length;
      return {
        key: color.leagueName,
        leagueName: color.leagueName,
        sportingColor: color.sportingColor,
        startingCount: color.startingCount,
        departedCount: color.departedCount,
        returnedCount: color.returnedCount,
        provisionalCount: color.provisionalCount,
        displacedCount: color.displacedCount,
        drawnCount: color.drawnCount,
        finalCount: color.finalCount,
        departed,
        returned,
        displaced,
        drawn,
        steps,
        total,
        hasChanges: total > 0,
        needsPoolAdjustment: color.displacedCount > 0 || color.drawnCount > 0,
        cleanlyBalanced: total > 0 && color.displacedCount === 0 && color.drawnCount === 0,
      };
    });
}

/** Global reveal order: leagues in order, sporting sequence inside each. */
export function buildRebalanceRevealOrder(leagues: readonly RebalanceLeague[]): RebalanceStep[] {
  return leagues.flatMap((league) => league.steps);
}

/** "TO SUPERLEAGUE" / "RETURNING" / "TO COMMON POOL" / "DRAWN FROM POOL": text labels, never colour alone. */
export function movementLabel(movementType: RebalanceMovementType): string {
  if (movementType === 'to-superleague') {
    return 'TO SUPERLEAGUE';
  }
  if (movementType === 'returning') {
    return 'RETURNING';
  }
  if (movementType === 'to-pool') {
    return 'TO COMMON POOL';
  }
  return 'DRAWN FROM POOL';
}

/** Direction glyph paired with the text label: up leaves the league, down enters it. */
export function movementGlyph(movementType: RebalanceMovementType): string {
  if (movementType === 'to-superleague') {
    return '▲';
  }
  if (movementType === 'returning') {
    return '▼';
  }
  if (movementType === 'to-pool') {
    return '▼';
  }
  return '▲';
}

/** Arrival animation class: departures/displaced leave upward-or-down story, arrivals land from their origin. */
export function arrivalClass(movementType: RebalanceMovementType): string {
  if (movementType === 'to-superleague' || movementType === 'drawn') {
    return 'arrive-up';
  }
  return 'arrive-down';
}

export function stepDescription(step: RebalanceStep): string {
  if (step.movementType === 'to-superleague') {
    return `${step.name} to Superleague from ${step.fromLeagueName}`;
  }
  if (step.movementType === 'returning') {
    return `${step.name} returning from Superleague to ${step.toLeagueName}`;
  }
  if (step.movementType === 'to-pool') {
    const rank = step.fromSeasonRank > 0 ? ` (P${step.fromSeasonRank} last season)` : '';
    return `${step.name} to the common pool from ${step.fromLeagueName}${rank}`;
  }
  return `${step.name} drawn from the common pool to ${step.toLeagueName}`;
}

/** "30 / 32": the roster check after Superleague movement. */
export function rosterCheckText(league: RebalanceLeague): string {
  return `${league.provisionalCount} / 32`;
}

/** "32 / 32 — BALANCED": the final state every league must reach. */
export function balancedText(league: RebalanceLeague): string {
  return `${league.finalCount} / 32 — BALANCED`;
}

export interface RebalanceChurn {
  totalAffected: number;
  leagueCount: number;
  changedLeagueCount: number;
  totalDeparted: number;
  totalReturned: number;
  totalDisplaced: number;
  totalDrawn: number;
}

/** Overall churn summary so the amount of movement stays understandable. */
export function churnSummary(result: RebalanceResult, leagues: readonly RebalanceLeague[]): RebalanceChurn {
  return {
    totalAffected: result.totalDeparted + result.totalReturned + result.totalDisplaced + result.totalDrawn,
    leagueCount: leagues.length,
    changedLeagueCount: leagues.filter((league) => league.hasChanges).length,
    totalDeparted: result.totalDeparted,
    totalReturned: result.totalReturned,
    totalDisplaced: result.totalDisplaced,
    totalDrawn: result.totalDrawn,
  };
}

export function churnLine(churn: RebalanceChurn): string {
  return (
    `${churn.totalAffected} affected athletes across ${churn.changedLeagueCount}/${churn.leagueCount} leagues · ` +
    `${churn.totalDeparted} to Superleague · ${churn.totalReturned} returning · ` +
    `${churn.totalDisplaced} to pool · ${churn.totalDrawn} drawn`
  );
}
