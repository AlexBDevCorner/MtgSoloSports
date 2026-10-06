import type { AutomaticMovement, FeederMovements, InauguralRosterMember, MovementMember } from './movementApi';

/**
 * Pure presentation model for the promotion/relegation movement board.
 *
 * The backend stays authoritative for who moves: this slice only groups the
 * persisted `promoted`/`relegated` facts by league boundary and orders the
 * reveal. No sporting math, no RNG, DOM-free so it is unit-testable.
 */

export type MovementDirection = 'promoted' | 'relegated';

export interface MovementStep {
  /** Stable identity for React keys and aria references. */
  key: string;
  athleteId: number;
  name: string;
  sportingColor: string;
  fromLeagueName: string;
  fromSeasonRank: number;
  toLeagueName: string;
  direction: MovementDirection;
  imageUrl: string | null;
}

export interface MovementBoundary {
  /** Feeder league name, stable across seasons (e.g. "White League"). */
  key: string;
  feederLeagueName: string;
  superleagueName: string;
  promoted: MovementStep[];
  relegated: MovementStep[];
  /** Reveal order inside this boundary: relegated first, then promoted. */
  steps: MovementStep[];
  total: number;
  /**
   * Tiered-pyramid boundary group label (MSS-060), e.g. "Feeder 1 ↔
   * Superleague". Null for v1 single-feeder boundaries, which render without
   * group headers exactly as before.
   */
  boundaryLabel: string | null;
  /**
   * Tiered-pyramid qualifier designations for this boundary/color
   * (MSS-060), e.g. "8 incumbents + 8 challengers contest the White
   * F1↔F2 qualifier". Null for v1 boundaries and the inaugural roster.
   * Designations are not movements yet: they name the qualifier field,
   * while `steps` holds completed automatic moves.
   */
  qualifierNote: string | null;
}

function toStep(member: MovementMember, direction: MovementDirection): MovementStep {
  return {
    key: `${direction}:${member.athleteId}`,
    athleteId: member.athleteId,
    name: member.name,
    sportingColor: member.sportingColor,
    fromLeagueName: member.fromLeagueName,
    fromSeasonRank: member.fromSeasonRank,
    toLeagueName: member.toLeagueName,
    direction,
    imageUrl: member.imageUrl,
  };
}

function sortSteps(steps: MovementStep[]): MovementStep[] {
  return [...steps].sort(
    (a, b) => a.fromSeasonRank - b.fromSeasonRank || a.name.localeCompare(b.name),
  );
}

/**
 * Groups promoted/relegated facts by promotion/relegation boundary.
 *
 * A boundary pairs one feeder league with the Superleague. League row ids
 * are per-season, so the feeder league *name* (e.g. "White League") is the
 * stable boundary key: promoted members arrive from it, relegated members
 * return to it. Boundaries sort alphabetically so multi-boundary events read
 * in a logical order; inside a boundary relegated athletes (moving down, out
 * of the Superleague) reveal before promoted athletes (moving up, into it).
 */
export function buildMovementBoundaries(
  promoted: readonly MovementMember[],
  relegated: readonly MovementMember[],
  superleagueName: string,
): MovementBoundary[] {
  const byFeeder = new Map<string, { promoted: MovementStep[]; relegated: MovementStep[] }>();

  for (const member of promoted) {
    const key = member.fromLeagueName;
    let entry = byFeeder.get(key);
    if (!entry) {
      entry = { promoted: [], relegated: [] };
      byFeeder.set(key, entry);
    }
    entry.promoted.push(toStep(member, 'promoted'));
  }

  for (const member of relegated) {
    const key = member.toLeagueName;
    let entry = byFeeder.get(key);
    if (!entry) {
      entry = { promoted: [], relegated: [] };
      byFeeder.set(key, entry);
    }
    entry.relegated.push(toStep(member, 'relegated'));
  }

  return [...byFeeder.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([feederLeagueName, entry]) => {
      const relegatedSteps = sortSteps(entry.relegated);
      const promotedSteps = sortSteps(entry.promoted);
      const steps = [...relegatedSteps, ...promotedSteps];
      return {
        key: feederLeagueName,
        feederLeagueName,
        superleagueName,
        promoted: promotedSteps,
        relegated: relegatedSteps,
        steps,
        total: steps.length,
        boundaryLabel: null,
        qualifierNote: null,
      };
    });
}

/** Boundary-grouped steps for one automatic movement transition. */
export function boundariesForMovement(movement: AutomaticMovement): MovementBoundary[] {
  return buildMovementBoundaries(movement.promoted, movement.relegated, movement.superleagueLeagueName);
}

/**
 * Boundary-grouped steps for the inaugural roster. Every member is a
 * promotion from its Season 1 feeder into the Superleague; there are no
 * relegations, so each boundary holds upward movement only.
 */
export function boundariesForInaugural(
  members: readonly InauguralRosterMember[],
  superleagueName: string,
): MovementBoundary[] {
  const byFeeder = new Map<string, MovementStep[]>();
  for (const member of members) {
    const step: MovementStep = {
      key: `promoted:${member.athleteId}`,
      athleteId: member.athleteId,
      name: member.name,
      sportingColor: member.sportingColor,
      fromLeagueName: member.fromLeagueName,
      fromSeasonRank: member.fromSeasonRank,
      toLeagueName: superleagueName,
      direction: 'promoted',
      imageUrl: member.imageUrl,
    };
    const list = byFeeder.get(member.fromLeagueName) ?? [];
    list.push(step);
    byFeeder.set(member.fromLeagueName, list);
  }

  return [...byFeeder.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([feederLeagueName, steps]) => {
      const promoted = sortSteps(steps);
      return {
        key: feederLeagueName,
        feederLeagueName,
        superleagueName,
        promoted,
        relegated: [],
        steps: promoted,
        total: promoted.length,
        boundaryLabel: null,
        qualifierNote: null,
      };
    });
}

/** Global reveal order: boundaries in order, relegated before promoted inside each. */
export function buildRevealOrder(boundaries: readonly MovementBoundary[]): MovementStep[] {
  return boundaries.flatMap((boundary) => boundary.steps);
}

/**
 * Tiered pyramid boundaries for one ordinary transition (MSS-060).
 *
 * Groups competitive movement by boundary — Feeder 1 ↔ Superleague, then
 * Feeder 1 ↔ Feeder 2, then Feeder 2 ↔ Feeder 3 — and by sporting color
 * within each boundary, from backend tier identity (never name parsing).
 * Each boundary holds that color's automatic promotions (up) and relegations
 * (down); qualifier incumbents/challengers are designations, not moves, so
 * they surface as the boundary's `qualifierNote` instead of steps. The
 * existing `MovementReveal` renders these boundaries unchanged: the upper
 * league reads as the Superleague slot and the lower league as the feeder
 * slot, top-down per boundary.
 */
export function buildTieredMovementBoundaries(
  movement: AutomaticMovement | null,
  feeders: FeederMovements | null,
): MovementBoundary[] {
  const boundaries: MovementBoundary[] = [];
  if (movement) {
    boundaries.push(...tieredSuperleagueBoundaries(movement));
  }
  if (feeders) {
    boundaries.push(...tieredFeederBoundaries(feeders));
  }
  return boundaries;
}

const TIER_BOUNDARY_ORDER = ['Feeder1Feeder2', 'Feeder2Feeder3'];

function tieredBoundaryKey(boundary: string, color: string): string {
  return `${boundary}:${color}`;
}

function tieredSuperleagueBoundaries(movement: AutomaticMovement): MovementBoundary[] {
  const byColor = new Map<string, { promoted: MovementStep[]; relegated: MovementStep[]; incumbents: number; challengers: number; upper: string; lower: string }>();
  const upper = movement.superleagueLeagueName;
  for (const member of movement.promoted) {
    const entry = tieredEntry(byColor, member.sportingColor, upper, member.fromLeagueName);
    entry.promoted.push(toStep(member, 'promoted'));
  }
  for (const member of movement.relegated) {
    const entry = tieredEntry(byColor, member.sportingColor, upper, member.toLeagueName);
    entry.relegated.push(toStep(member, 'relegated'));
  }
  for (const member of movement.qualifierIncumbents) {
    const entry = tieredEntry(byColor, member.sportingColor, upper, member.toLeagueName || member.fromLeagueName);
    entry.incumbents += 1;
  }
  for (const member of movement.qualifierChallengers) {
    const entry = tieredEntry(byColor, member.sportingColor, upper, member.fromLeagueName);
    entry.challengers += 1;
  }
  return [...byColor.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([color, entry]) => tieredBoundary(`Superleague:${color}`, entry, 'Feeder 1 ↔ Superleague'));
}

function tieredEntry(
  byColor: Map<string, { promoted: MovementStep[]; relegated: MovementStep[]; incumbents: number; challengers: number; upper: string; lower: string }>,
  color: string,
  upper: string,
  lower: string,
): { promoted: MovementStep[]; relegated: MovementStep[]; incumbents: number; challengers: number; upper: string; lower: string } {
  let entry = byColor.get(color);
  if (!entry) {
    entry = { promoted: [], relegated: [], incumbents: 0, challengers: 0, upper, lower };
    byColor.set(color, entry);
  }
  return entry;
}

function tieredFeederBoundaries(feeders: FeederMovements): MovementBoundary[] {
  const groups = new Map<string, { promoted: MovementStep[]; relegated: MovementStep[]; incumbents: number; challengers: number; upper: string; lower: string; boundary: string; color: string }>();
  for (const member of feeders.movements) {
    const key = tieredBoundaryKey(member.boundary, member.sportingColorName);
    let group = groups.get(key);
    if (!group) {
      group = { promoted: [], relegated: [], incumbents: 0, challengers: 0, upper: '', lower: '', boundary: member.boundary, color: member.sportingColorName };
      groups.set(key, group);
    }
    if (member.movementKind === 'FeederAutomaticPromotion') {
      group.promoted.push(toFeederStep(member, 'promoted'));
      group.upper = member.toLeagueName;
      group.lower = member.fromLeagueName;
    } else if (member.movementKind === 'FeederAutomaticRelegation') {
      group.relegated.push(toFeederStep(member, 'relegated'));
      group.upper = member.fromLeagueName;
      group.lower = member.toLeagueName;
    } else if (member.movementKind === 'FeederQualifierIncumbent') {
      group.incumbents += 1;
      group.upper = group.upper || member.fromLeagueName;
    } else if (member.movementKind === 'FeederQualifierChallenger') {
      group.challengers += 1;
      group.lower = group.lower || member.fromLeagueName;
    }
  }
  return [...groups.entries()]
    .sort(([aKey, a], [bKey, b]) => {
      const order = TIER_BOUNDARY_ORDER.indexOf(a.boundary) - TIER_BOUNDARY_ORDER.indexOf(b.boundary);
      return order !== 0 ? order : aKey.localeCompare(bKey);
    })
    .map(([, group]) =>
      tieredBoundary(
        tieredBoundaryKey(group.boundary, group.color),
        { promoted: group.promoted, relegated: group.relegated, incumbents: group.incumbents, challengers: group.challengers, upper: group.upper, lower: group.lower },
        group.boundary === 'Feeder1Feeder2' ? 'Feeder 1 ↔ Feeder 2' : 'Feeder 2 ↔ Feeder 3',
      ),
    );
}

function toFeederStep(
  member: { athleteId: number; name: string; sportingColorName: string; fromLeagueName: string; fromSeasonRank: number; toLeagueName: string; imageUrl: string | null },
  direction: MovementDirection,
): MovementStep {
  return {
    key: `${direction}:${member.athleteId}`,
    athleteId: member.athleteId,
    name: member.name,
    sportingColor: member.sportingColorName,
    fromLeagueName: member.fromLeagueName,
    fromSeasonRank: member.fromSeasonRank,
    toLeagueName: member.toLeagueName,
    direction,
    imageUrl: member.imageUrl,
  };
}

function tieredBoundary(
  key: string,
  entry: { promoted: MovementStep[]; relegated: MovementStep[]; incumbents: number; challengers: number; upper: string; lower: string },
  boundaryLabel: string,
): MovementBoundary {
  const relegatedSteps = sortSteps(entry.relegated);
  const promotedSteps = sortSteps(entry.promoted);
  const steps = [...relegatedSteps, ...promotedSteps];
  return {
    key,
    feederLeagueName: entry.lower,
    superleagueName: entry.upper,
    promoted: promotedSteps,
    relegated: relegatedSteps,
    steps,
    total: steps.length,
    boundaryLabel,
    qualifierNote:
      entry.incumbents > 0 || entry.challengers > 0
        ? `${entry.incumbents} incumbent${entry.incumbents === 1 ? '' : 's'} + ${entry.challengers} challenger${entry.challengers === 1 ? '' : 's'} contest the qualifier for this boundary — designations, not moves yet.`
        : null,
  };
}

/** "PROMOTED" / "RELEGATED": text label, never colour alone. */
export function directionLabel(direction: MovementDirection): string {
  return direction === 'promoted' ? 'PROMOTED' : 'RELEGATED';
}

/** Direction glyph paired with the text label: up for promotion, down for relegation. */
export function directionGlyph(direction: MovementDirection): string {
  return direction === 'promoted' ? '▲' : '▼';
}

export function stepDescription(step: MovementStep): string {
  const verb = step.direction === 'promoted' ? 'promoted' : 'relegated';
  return `${step.name} ${verb} from ${step.fromLeagueName} to ${step.toLeagueName}`;
}
