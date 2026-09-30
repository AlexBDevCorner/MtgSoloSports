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
