import { fetchJson } from '../../shared/api/http';

export interface Honour {
  seasonNumber: number;
  seasonId: number;
  leagueId: number;
  leagueName: string;
  leagueKind: number;
  honourKind: string;
  athleteId: number;
  athleteName: string;
}

export interface Honours {
  saveId: string;
  honours: Honour[];
}

export interface RecordHolder {
  athleteId: number;
  athleteName: string;
}

export interface CareerRecord {
  recordKey: string;
  label: string;
  value: number;
  valueDisplay: string;
  isBonus: boolean;
  isVacant: boolean;
  holders: RecordHolder[];
}

export interface RecordHistoryItem {
  athleteId: number;
  athleteName: string;
  recordKey: string;
  value: number;
  priorValue: number;
  seasonNumber: number;
  text: string;
}

export interface Records {
  saveId: string;
  records: CareerRecord[];
  recentHistory: RecordHistoryItem[];
}

export interface HallOfFameLeader {
  rank: number;
  athleteId: number;
  athleteName: string;
  sportingColor: number;
  sportingColorName: string;
  feederTitles: number;
  superleagueTitles: number;
  totalTitles: number;
  stageWins: number;
  roundWins: number;
  superleagueAppearances: number;
  totalAppearances: number;
  longestSuperleagueTenure: number;
  promotions: number;
  relegations: number;
  currentEffectiveBonusThousandths: number;
  longestTitleStreak: number;
  longestStageWinStreak: number;
}

export interface HallOfFame {
  saveId: string;
  take: number;
  totalAthletes: number;
  leaders: HallOfFameLeader[];
}

export async function fetchHonours(
  saveId: string,
  signal?: AbortSignal,
): Promise<Honours> {
  return fetchJson<Honours>(`/api/saves/${saveId}/honours`, { signal });
}

export async function fetchRecords(
  saveId: string,
  signal?: AbortSignal,
): Promise<Records> {
  return fetchJson<Records>(`/api/saves/${saveId}/records`, { signal });
}

export async function fetchHallOfFame(
  saveId: string,
  take = 20,
  signal?: AbortSignal,
): Promise<HallOfFame> {
  return fetchJson<HallOfFame>(
    `/api/saves/${saveId}/hall-of-fame?take=${take}`,
    { signal },
  );
}
