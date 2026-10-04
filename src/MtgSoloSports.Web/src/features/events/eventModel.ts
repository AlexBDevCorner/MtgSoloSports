/** Keys of postseason events that are played in rounds (mirrors backend PostseasonEvents). */
export type EventKey = 'qualifier' | 'color-cup-individual' | 'color-cup-team' | 'type-cup-team';

export const EVENT_TITLES: Record<EventKey, string> = {
  qualifier: 'Superleague qualifier',
  'color-cup-individual': 'Color Cup — individual',
  'color-cup-team': 'Color Cup — team',
  'type-cup-team': 'Type Cup — team',
};

export function isEventKey(value: string | null | undefined): value is EventKey {
  return value !== null && value !== undefined && Object.prototype.hasOwnProperty.call(EVENT_TITLES, value);
}

export function isTeamEvent(key: EventKey): boolean {
  return key === 'color-cup-team' || key === 'type-cup-team';
}

/** Where an event's results appear once it completes. */
export function resultsTarget(key: EventKey): 'standings' | 'cups' {
  return key === 'qualifier' ? 'standings' : 'cups';
}

/** Keys of the Cup squad selections: single-step events shown as a reveal on Live. */
export type SelectionKey = 'color-cup-selection' | 'type-cup-selection';
export const SELECTION_TITLES: Record<SelectionKey, string> = {
  'color-cup-selection': 'Color Cup — squad selection',
  'type-cup-selection': 'Type Cup — squad selection',
};

export function isSelectionKey(value: string | null | undefined): value is SelectionKey {
  return value !== null && value !== undefined && Object.prototype.hasOwnProperty.call(SELECTION_TITLES, value);
}

/**
 * Keys of the postseason transition reveals (MSS-053). Unlike round-based
 * `EventKey` competitions, these are single persisted results presented
 * progressively on Live: `movement` covers both the Season 1 inaugural
 * Superleague formation and later promotion/relegation, `rebalance` covers
 * the feeder-league rebalance. The reveal only reads persisted facts.
 */
export type TransitionKey = 'movement' | 'rebalance';

export const TRANSITION_TITLES: Record<TransitionKey, string> = {
  movement: 'Promotion & relegation',
  rebalance: 'Feeder rebalance',
};

export function isTransitionKey(value: string | null | undefined): value is TransitionKey {
  return value !== null && value !== undefined && Object.prototype.hasOwnProperty.call(TRANSITION_TITLES, value);
}

/** The transition reveal a lifecycle action resolves, or null for every other action. */
export function transitionForAction(action: string | null | undefined): TransitionKey | null {
  if (action === 'ResolveInauguralMovement' || action === 'ResolveAutomaticMovement') {
    return 'movement';
  }
  return action === 'RebalanceFeeders' ? 'rebalance' : null;
}

/** The selection a lifecycle action resolves, or null for every other action. */
export function selectionForAction(action: string | null | undefined): SelectionKey | null {
  if (action === 'SelectColorCup') {
    return 'color-cup-selection';
  }
  return action === 'SelectTypeCup' ? 'type-cup-selection' : null;
}

/** Odd seasons end with the Color Cup, even seasons with the Type Cup. */
export function selectionForSeason(season: number): SelectionKey {
  return season % 2 === 1 ? 'color-cup-selection' : 'type-cup-selection';
}

export interface ProgressShape {
  roundsPlayed: number;
  totalRounds: number;
  groupCount: number;
  roundsPerGroup: number;
}

/** Last played position: "Round 5 / 16", "Group 2 · Round 3 / 8", or "Complete". */
export function progressLabel(progress: ProgressShape): string {
  if (progress.roundsPlayed >= progress.totalRounds) {
    return 'Complete';
  }
  if (progress.groupCount <= 1) {
    return `Round ${progress.roundsPlayed} / ${progress.totalRounds}`;
  }
  if (progress.roundsPlayed === 0) {
    return `Group 1 · Round 0 / ${progress.roundsPerGroup}`;
  }
  const group = Math.floor((progress.roundsPlayed - 1) / progress.roundsPerGroup) + 1;
  const round = ((progress.roundsPlayed - 1) % progress.roundsPerGroup) + 1;
  return `Group ${group} · Round ${round} / ${progress.roundsPerGroup}`;
}

export function roundLabel(group: number | null, round: number): string {
  return group === null ? `Round ${round}` : `Group ${group} · Round ${round}`;
}
