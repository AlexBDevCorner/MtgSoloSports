import type { EventKey, SelectionKey } from '../events/eventModel';
import type { CupKind } from '../routing/routes';

/**
 * Display wording and key helpers for the Cup history pages. Pure and
 * DOM-free: nothing here re-ranks or re-scores stored results.
 */

const COLOR_KEYS = ['white', 'blue', 'black', 'red', 'green', 'multicolor', 'hybrid', 'colorless'];

/** Display-only projection of fixed-point thousandths (no sporting math). */
export function formatPoints(thousandths: number): string {
  return (thousandths / 1000).toFixed(3);
}

export function medalBadge(medal: string | null | undefined): string {
  if (medal === 'Gold') {
    return '🥇 Gold';
  }
  if (medal === 'Silver') {
    return '🥈 Silver';
  }
  if (medal === 'Bronze') {
    return '🥉 Bronze';
  }
  return '—';
}

export function ordinal(value: number): string {
  const lastTwo = value % 100;
  if (lastTwo >= 11 && lastTwo <= 13) {
    return `${value}th`;
  }
  const suffix = ['th', 'st', 'nd', 'rd'][value % 10] ?? 'th';
  return `${value}${suffix}`;
}

/** "3rd of 8". */
export function rankOf(rank: number, count: number): string {
  return `${ordinal(rank)} of ${count}`;
}

export function cupTitle(cup: CupKind): string {
  return cup === 'color' ? 'Color Cup' : 'Type Cup';
}

/** Maps the backend Cup name ("Color" | "Type") to the route segment. */
export function cupKindOf(cup: string): CupKind {
  return cup === 'Color' ? 'color' : 'type';
}

/** Color keys are lower-case color names; Type keys are the creature type itself. */
export function teamKeyFromName(cup: CupKind, teamName: string): string {
  return cup === 'color' ? teamName.toLowerCase() : teamName;
}

export function teamSwatchClass(cup: CupKind, teamKey: string): string {
  return cup === 'color' && COLOR_KEYS.includes(teamKey) ? `team-swatch team-swatch-${teamKey}` : 'team-swatch';
}

export function teamEventKey(cup: CupKind): EventKey {
  return cup === 'color' ? 'color-cup-team' : 'type-cup-team';
}

export function selectionKeyFor(cup: CupKind): SelectionKey {
  return cup === 'color' ? 'color-cup-selection' : 'type-cup-selection';
}

export function stateLabel(state: string): string {
  if (state === 'Selected') {
    return 'Squads selected';
  }
  return state === 'InProgress' ? 'In progress' : 'Completed';
}

/** Why a Type Cup member represents its type; null when no reason was stored. */
export function reasonLabel(reason: string | null | undefined): string | null {
  switch (reason) {
    case 'Capped':
      return 'Capped to this type';
    case 'OnlyType':
      return 'Only type able to field a team';
    case 'BestRank':
      return 'Ranks highest for this type';
    case 'Balanced':
      return 'Placed here so more teams take part';
    default:
      return null;
  }
}

/** Two-letter stand-in shown when an athlete has no card art. */
export function initials(name: string): string {
  return name.slice(0, 2).toUpperCase();
}
