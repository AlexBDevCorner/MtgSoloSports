import type { AutomaticMovement, InauguralRosterMember, MovementMember } from './movementApi';

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
      };
    });
}

/** Global reveal order: boundaries in order, relegated before promoted inside each. */
export function buildRevealOrder(boundaries: readonly MovementBoundary[]): MovementStep[] {
  return boundaries.flatMap((boundary) => boundary.steps);
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
