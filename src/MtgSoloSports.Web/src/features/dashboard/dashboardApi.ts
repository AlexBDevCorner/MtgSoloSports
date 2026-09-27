import { fetchJson } from '../../shared/api/http';
import type { SaveDetail } from '../saves/savesApi';

export interface SeasonProgressLeague {
  leagueId: number;
  leagueName: string;
  currentStage: number | null;
  completedStages: number;
  isLeagueComplete: boolean;
}

export interface SeasonProgress {
  saveId: string;
  seasonNumber: number;
  globalStage: number;
  isSeasonComplete: boolean;
  leagues: SeasonProgressLeague[];
}

export interface SeasonStatus {
  saveId: string;
  currentSeasonNumber: number;
  persistedPhase: string;
  computedPhase: string;
  sourceSeasonNumber: number | null;
  nextSeasonNumber: number | null;
  isInauguralTransition: boolean;
  globalStage: number;
  isCurrentSeasonComplete: boolean;
  seasonComplete: boolean;
  movementResolved: boolean;
  qualifierResolved: boolean;
  rebalanced: boolean;
  cupSelectionResolved: boolean;
  cupIndividualResolved: boolean;
  cupTeamResolved: boolean;
  cupComplete: boolean;
  readyToStartNextSeason: boolean;
  expectedCup: string;
  legalNextActions: string[];
  nextActionDetail: string;
}

export interface AdvanceNextEventResult {
  saveId: string;
  executedAction: string;
  executedDetail: string;
  currentSeasonNumber: number;
  computedPhase: string;
  persistedPhase: string;
  sourceSeasonNumber: number | null;
  nextSeasonNumber: number | null;
  isInauguralTransition: boolean;
  globalStage: number;
  isCurrentSeasonComplete: boolean;
  seasonComplete: boolean;
  movementResolved: boolean;
  qualifierResolved: boolean;
  rebalanced: boolean;
  cupSelectionResolved: boolean;
  cupIndividualResolved: boolean;
  cupTeamResolved: boolean;
  cupComplete: boolean;
  readyToStartNextSeason: boolean;
  expectedCup: string;
  legalNextActions: string[];
  nextActionDetail: string;
}

export interface Season1RosterAthlete {
  name: string;
  drawIndex: number;
}

export interface Season1LeagueRoster {
  leagueName: string;
  sportingColor: string;
  leagueId: number;
  athletes: Season1RosterAthlete[];
}

export interface Season1PoolCount {
  sportingColor: string;
  count: number;
}

export interface Season1Leagues {
  saveId: string;
  seasonNumber: number;
  hasSuperleague: boolean;
  drawChecksum: string;
  activeAthletes: number;
  poolAthletes: number;
  leagues: Season1LeagueRoster[];
  poolCounts: Season1PoolCount[];
}

export async function fetchSaveDetail(saveId: string, signal?: AbortSignal): Promise<SaveDetail> {
  return fetchJson<SaveDetail>(`/api/saves/${saveId}`, { signal });
}

export async function fetchSeasonProgress(
  saveId: string,
  seasonNumber: number,
  signal?: AbortSignal,
): Promise<SeasonProgress> {
  return fetchJson<SeasonProgress>(`/api/saves/${saveId}/seasons/${seasonNumber}/progress`, {
    signal,
  });
}

export async function fetchSeason1Leagues(
  saveId: string,
  signal?: AbortSignal,
): Promise<Season1Leagues> {
  return fetchJson<Season1Leagues>(`/api/saves/${saveId}/seasons/1/leagues`, { signal });
}

export async function fetchSeasonStatus(
  saveId: string,
  signal?: AbortSignal,
): Promise<SeasonStatus> {
  return fetchJson<SeasonStatus>(`/api/saves/${saveId}/season-status`, { signal });
}

export async function advanceToNextEvent(
  saveId: string,
  signal?: AbortSignal,
): Promise<AdvanceNextEventResult> {
  return fetchJson<AdvanceNextEventResult>(`/api/saves/${saveId}/advance-next-event`, {
    method: 'POST',
    signal,
  });
}

export async function startNextSeason(
  saveId: string,
  signal?: AbortSignal,
): Promise<unknown> {
  return fetchJson<unknown>(`/api/saves/${saveId}/seasons/start-next`, {
    method: 'POST',
    signal,
  });
}
