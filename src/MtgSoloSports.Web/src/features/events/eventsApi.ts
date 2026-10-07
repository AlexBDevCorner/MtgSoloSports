import { fetchJson } from '../../shared/api/http';
import type { HistoryRoundPlacement } from '../history/historyApi';
import type { EventKey } from './eventModel';

/** Progress of the round-based event that is the next lifecycle step (from season status). */
export interface EventProgress {
  event: EventKey;
  sourceSeasonNumber: number;
  roundsPlayed: number;
  totalRounds: number;
  groupCount: number;
  roundsPerGroup: number;
  group: number | null;
  roundInGroup: number | null;
  /** Type Cup tournament stage (MSS-062 additive): phase, qual group, group count and label. */
  tournamentPhase?: number | null;
  qualificationGroup?: number | null;
  qualificationGroupCount?: number | null;
  tournamentStage?: string | null;
}

export interface EventRoundView {
  seasonNumber: number;
  event: EventKey;
  title: string;
  group: number | null;
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
  placements: HistoryRoundPlacement[];
}

export interface PlayRoundResult {
  round: EventRoundView;
  roundsPlayed: number;
  totalRounds: number;
  isComplete: boolean;
  /** Type Cup tournament stage (MSS-062 additive, present for type-cup-team). */
  tournamentStage?: string | null;
  tournamentPhase?: number | null;
  qualificationGroup?: number | null;
  rankGroup?: number | null;
  roundInGroup?: number | null;
}

export interface SeasonEventSummary {
  event: EventKey;
  title: string;
  roundsPlayed: number;
  totalRounds: number;
  groupCount: number;
  roundsPerGroup: number;
  isComplete: boolean;
}

export interface PlayedRound {
  group: number | null;
  round: number;
}

export interface EventTeamRow {
  teamName: string;
  rank: number | null;
  scoreThousandths: number;
}

export interface EventTeamMember {
  athleteId: number;
  teamName: string;
}

export interface EventTeamStandings {
  isFinal: boolean;
  groupsCompleted: number;
  teams: EventTeamRow[];
  /** Which team each athlete of the selected field competes for. */
  members: EventTeamMember[];
}

const STEP_ROUTES: Record<EventKey, string> = {
  qualifier: 'superleague/qualifier',
  'color-cup-individual': 'cups/color/individual',
  'color-cup-team': 'cups/color/team',
  'type-cup-team': 'cups/type/team',
};

/** Simulates exactly one round of the event on the backend. */
export function playEventRound(saveId: string, key: EventKey): Promise<PlayRoundResult> {
  return fetchJson<PlayRoundResult>(`/api/saves/${saveId}/${STEP_ROUTES[key]}/rounds/next`, { method: 'POST' });
}

/** Existing one-shot endpoint: plays all remaining rounds and completes the event. */
export function runEventRemaining(saveId: string, key: EventKey): Promise<unknown> {
  return fetchJson<unknown>(`/api/saves/${saveId}/${STEP_ROUTES[key]}`, { method: 'POST' });
}

export async function fetchSeasonEvents(saveId: string, season: number, signal?: AbortSignal): Promise<SeasonEventSummary[]> {
  const body = await fetchJson<{ events: SeasonEventSummary[] }>(
    `/api/saves/${saveId}/history/seasons/${season}/events`,
    { signal },
  );
  return body.events;
}

export async function fetchEventRounds(
  saveId: string,
  season: number,
  key: EventKey,
  signal?: AbortSignal,
): Promise<PlayedRound[]> {
  const body = await fetchJson<{ rounds: PlayedRound[] }>(
    `/api/saves/${saveId}/history/seasons/${season}/events/${key}/rounds`,
    { signal },
  );
  return body.rounds;
}

export function fetchEventRound(
  saveId: string,
  season: number,
  key: EventKey,
  round: number,
  group: number | null,
  signal?: AbortSignal,
): Promise<EventRoundView> {
  const query = group === null ? '' : `?group=${group}`;
  return fetchJson<EventRoundView>(
    `/api/saves/${saveId}/history/seasons/${season}/events/${key}/rounds/${round}${query}`,
    { signal },
  );
}

export function fetchEventTeamStandings(
  saveId: string,
  season: number,
  key: EventKey,
  signal?: AbortSignal,
  /** Limits the totals to the rounds played ahead of this round. */
  before?: { group: number; round: number },
): Promise<EventTeamStandings> {
  const query = before ? `?beforeGroup=${before.group}&beforeRound=${before.round}` : '';
  return fetchJson<EventTeamStandings>(
    `/api/saves/${saveId}/history/seasons/${season}/events/${key}/team-standings${query}`,
    { signal },
  );
}
