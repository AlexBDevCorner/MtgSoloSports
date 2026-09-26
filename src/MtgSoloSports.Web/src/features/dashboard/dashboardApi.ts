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
