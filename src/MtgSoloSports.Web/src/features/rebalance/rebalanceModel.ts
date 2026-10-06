import type { RebalanceMovementMember, RebalanceResult } from './rebalanceApi.ts';

/**
 * Pure presentation model for the feeder-rebalance board.
 *
 * The backend stays authoritative for who moves: this slice only groups the
 * persisted `departed`/`returned`/`displaced`/`draws` facts by feeder league
 * and orders the reveal in the sporting sequence (departures, returns, pool
 * outflow, pool inflow). No sporting math, no RNG, DOM-free so unit-testable.
 */

export type RebalanceMovementType =
  | 'to-superleague'
  | 'returning'
  | 'to-pool'
  | 'drawn'
  | 'up'
  | 'down';

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
  leagueId: number;
  leagueName: string;
  sportingColor: string;
  /** Persisted feeder division (1/2/3 tiered, 0/unknown legacy v1). */
  feederDivision: number | null;
  startingCount: number;
  departedCount: number;
  returnedCount: number;
  provisionalCount: number;
  displacedCount: number;
  drawnCount: number;
  finalCount: number;
  /** Structural cascade counts from persisted facts (tiered saves). */
  upIn: number;
  upOut: number;
  downIn: number;
  downOut: number;
  departed: RebalanceStep[];
  returned: RebalanceStep[];
  displaced: RebalanceStep[];
  drawn: RebalanceStep[];
  /** Structural F1↔F2/F2↔F3 arrivals into this league. */
  structuralIn: RebalanceStep[];
  /** Structural F1↔F2/F2↔F3 departures out of this league. */
  structuralOut: RebalanceStep[];
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

/**
 * One sporting color's cascade through the pyramid (MSS-060): the division
 * entries (F1 → F2 → F3, or the single legacy feeder) with the structural
 * up/down flow between them. Pool moves appear only on the F3 entry.
 */
export interface RebalanceColorCascade {
  sportingColor: string;
  divisions: RebalanceLeague[];
  total: number;
  hasChanges: boolean;
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
  if (kind === 'RebalanceUp') {
    return 'up';
  }
  if (kind === 'RebalanceDown') {
    return 'down';
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
 * feeders v1, up to 24 tiered F1/F2/F3 leagues). Movements join their league
 * by exact league-name match within this response — never by parsing names.
 * When names do not match (historical rows), Superleague and pool moves fall
 * back to the same-color entry. Leagues sort alphabetically so multi-league
 * events read in a logical order; inside a league the sporting sequence is
 * departures, returns, structural moves, pool outflow, then pool inflow.
 * Draws keep the backend's persisted order (the authoritative draw result)
 * instead of being resorted.
 */
export function buildRebalanceLeagues(result: RebalanceResult): RebalanceLeague[] {
  const byLeagueName = new Map<string, number>();
  const leagues = [...result.colors]
    .sort((a, b) => a.leagueName.localeCompare(b.leagueName))
    .map((color, index): { entry: RebalanceLeague; color: string } => {
      byLeagueName.set(color.leagueName, index);
      return {
        color: color.sportingColor,
        entry: {
          key: color.leagueName,
          leagueId: color.leagueId,
          leagueName: color.leagueName,
          sportingColor: color.sportingColor,
          feederDivision: color.feederDivision ?? null,
          startingCount: color.startingCount,
          departedCount: color.departedCount,
          returnedCount: color.returnedCount,
          provisionalCount: color.provisionalCount,
          displacedCount: color.displacedCount,
          drawnCount: color.drawnCount,
          finalCount: color.finalCount,
          upIn: color.rebalancedUpIn ?? 0,
          upOut: color.rebalancedUpOut ?? 0,
          downIn: color.rebalancedDownIn ?? 0,
          downOut: color.rebalancedDownOut ?? 0,
          departed: [],
          returned: [],
          displaced: [],
          drawn: [],
          structuralIn: [],
          structuralOut: [],
          steps: [],
          total: 0,
          hasChanges: false,
          needsPoolAdjustment: color.displacedCount > 0 || color.drawnCount > 0,
          cleanlyBalanced: false,
        },
      };
    });
  const entries = leagues.map((row) => row.entry);

  const byColorFallback = new Map<string, RebalanceLeague[]>();
  for (const entry of entries) {
    const list = byColorFallback.get(entry.sportingColor) ?? [];
    list.push(entry);
    byColorFallback.set(entry.sportingColor, list);
  }
  function targetLeague(leagueName: string, color: string): RebalanceLeague | null {
    const direct = byLeagueName.get(leagueName);
    if (direct !== undefined) {
      return entries[direct]!;
    }
    return byColorFallback.get(color)?.[0] ?? null;
  }

  for (const member of result.departed) {
    targetLeague(member.fromLeagueName, member.sportingColor)?.departed.push(toStep(member));
  }
  for (const member of result.returned) {
    targetLeague(member.toLeagueName, member.sportingColor)?.returned.push(toStep(member));
  }
  for (const member of result.displaced) {
    targetLeague(member.fromLeagueName, member.sportingColor)?.displaced.push(toStep(member));
  }
  for (const member of result.draws) {
    targetLeague(member.toLeagueName, member.sportingColor)?.drawn.push(toStep(member));
  }
  for (const member of result.rebalancedUp ?? []) {
    const step = toStep(member);
    targetLeague(member.toLeagueName, member.sportingColor)?.structuralIn.push(step);
    targetLeague(member.fromLeagueName, member.sportingColor)?.structuralOut.push(step);
  }
  for (const member of result.rebalancedDown ?? []) {
    const step = toStep(member);
    targetLeague(member.toLeagueName, member.sportingColor)?.structuralIn.push(step);
    targetLeague(member.fromLeagueName, member.sportingColor)?.structuralOut.push(step);
  }

  for (const entry of entries) {
    entry.departed = sortByRankThenName(entry.departed);
    entry.returned = sortByRankThenName(entry.returned);
    // Overflow athletes leave worst-ranked first so the lowest-ranked rule reads clearly.
    entry.displaced = sortByRankThenName(entry.displaced, true);
    entry.structuralIn = sortByRankThenName(entry.structuralIn);
    entry.structuralOut = sortByRankThenName(entry.structuralOut, true);
    // Draws keep persisted order.
    entry.steps = [
      ...entry.departed,
      ...entry.returned,
      ...entry.structuralOut,
      ...entry.structuralIn,
      ...entry.displaced,
      ...entry.drawn,
    ];
    entry.total = entry.steps.length;
    entry.hasChanges = entry.total > 0;
    entry.cleanlyBalanced = entry.total > 0 && entry.displaced.length === 0 && entry.drawn.length === 0;
  }
  return entries;
}

/**
 * Groups per-league entries into per-color cascades ordered F1 → F2 → F3
 * (division order, then name). Legacy single-feeder entries form
 * single-division cascades. Pool moves only ever appear on F3 (or legacy)
 * entries; anything else is a backend data error surfaced by the counts.
 */
export function buildRebalanceCascades(result: RebalanceResult): RebalanceColorCascade[] {
  const leagues = buildRebalanceLeagues(result);
  const byColor = new Map<string, RebalanceLeague[]>();
  for (const league of leagues) {
    const list = byColor.get(league.sportingColor) ?? [];
    list.push(league);
    byColor.set(league.sportingColor, list);
  }
  return [...byColor.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([sportingColor, divisions]) => {
      const ordered = [...divisions].sort(
        (a, b) => divisionOrder(a.feederDivision) - divisionOrder(b.feederDivision) || a.leagueName.localeCompare(b.leagueName),
      );
      // Structural moves touch two division entries (out + in); count each
      // athlete once per color via stable step keys.
      const seen = new Set<string>();
      for (const league of ordered) {
        for (const step of league.steps) {
          seen.add(step.key);
        }
      }
      return {
        sportingColor,
        divisions: ordered,
        total: seen.size,
        hasChanges: ordered.some((league) => league.hasChanges),
      };
    });
}

function divisionOrder(division: number | null): number {
  if (division === 1 || division === 2 || division === 3) {
    return division;
  }
  return 1;
}

/** Global reveal order: leagues in order, sporting sequence inside each. Structural moves touch two division entries (out + in) and reveal once. */
export function buildRebalanceRevealOrder(leagues: readonly RebalanceLeague[]): RebalanceStep[] {
  const seen = new Set<string>();
  const order: RebalanceStep[] = [];
  for (const league of leagues) {
    for (const step of league.steps) {
      if (!seen.has(step.key)) {
        seen.add(step.key);
        order.push(step);
      }
    }
  }
  return order;
}

/** Groups built per-league entries into per-color cascades (F1 → F2 → F3). */
export function groupRebalanceCascades(leagues: readonly RebalanceLeague[]): RebalanceColorCascade[] {
  const byColor = new Map<string, RebalanceLeague[]>();
  for (const league of leagues) {
    const list = byColor.get(league.sportingColor) ?? [];
    list.push(league);
    byColor.set(league.sportingColor, list);
  }
  return [...byColor.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([sportingColor, divisions]) => {
      const ordered = [...divisions].sort(
        (a, b) => divisionOrder(a.feederDivision) - divisionOrder(b.feederDivision) || a.leagueName.localeCompare(b.leagueName),
      );
      const seen = new Set<string>();
      for (const league of ordered) {
        for (const step of league.steps) {
          seen.add(step.key);
        }
      }
      return {
        sportingColor,
        divisions: ordered,
        total: seen.size,
        hasChanges: ordered.some((league) => league.hasChanges),
      };
    });
}

/** "TO SUPERLEAGUE" / "RETURNING" / "TO COMMON POOL" / "DRAWN FROM POOL" / "UP (STRUCTURAL)" / "DOWN (STRUCTURAL)": text labels, never colour alone. */
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
  if (movementType === 'up') {
    return 'UP (STRUCTURAL)';
  }
  if (movementType === 'down') {
    return 'DOWN (STRUCTURAL)';
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
  if (movementType === 'up') {
    return '▲';
  }
  if (movementType === 'down') {
    return '▼';
  }
  return '▲';
}

/** Arrival animation class: departures/displaced leave upward-or-down story, arrivals land from their origin. */
export function arrivalClass(movementType: RebalanceMovementType): string {
  if (movementType === 'to-superleague' || movementType === 'drawn' || movementType === 'up') {
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
  if (step.movementType === 'up') {
    return `${step.name} up a tier (structural) from ${step.fromLeagueName} to ${step.toLeagueName}`;
  }
  if (step.movementType === 'down') {
    return `${step.name} down a tier (structural) from ${step.fromLeagueName} to ${step.toLeagueName}`;
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
  totalUp: number;
  totalDown: number;
}

/** Overall churn summary so the amount of movement stays understandable. */
export function churnSummary(result: RebalanceResult, leagues: readonly RebalanceLeague[]): RebalanceChurn {
  const totalUp = result.totalRebalancedUp ?? 0;
  const totalDown = result.totalRebalancedDown ?? 0;
  return {
    totalAffected: result.totalDeparted + result.totalReturned + result.totalDisplaced + result.totalDrawn + totalUp + totalDown,
    leagueCount: leagues.length,
    changedLeagueCount: leagues.filter((league) => league.hasChanges).length,
    totalDeparted: result.totalDeparted,
    totalReturned: result.totalReturned,
    totalDisplaced: result.totalDisplaced,
    totalDrawn: result.totalDrawn,
    totalUp,
    totalDown,
  };
}

export function churnLine(churn: RebalanceChurn): string {
  const structural =
    churn.totalUp > 0 || churn.totalDown > 0
      ? ` · ${churn.totalUp} up / ${churn.totalDown} down the cascade (structural)`
      : '';
  return (
    `${churn.totalAffected} affected athletes across ${churn.changedLeagueCount}/${churn.leagueCount} leagues · ` +
    `${churn.totalDeparted} to Superleague · ${churn.totalReturned} returning · ` +
    `${churn.totalDisplaced} to pool · ${churn.totalDrawn} drawn${structural}`
  );
}
