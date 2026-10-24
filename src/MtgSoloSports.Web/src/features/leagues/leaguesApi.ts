import { fetchJson } from '../../shared/api/http';

export interface LeagueChampionRef {
  athleteId: number;
  athleteName: string;
  seasonNumber: number;
  totalChampionshipPointsThousandths: number;
}

export interface LeagueProfileSummary {
  leagueKey: string;
  name: string;
  kind: string;
  feederDivision: number;
  leagueLevel: string | null;
  sportingColor: number;
  sportingColorName: string;
  totalSeasons: number;
  completedSeasons: number;
  uniqueAthletes: number;
  firstSeasonNumber: number | null;
  lastCompletedSeasonNumber: number | null;
  latestChampion: LeagueChampionRef | null;
}

export interface LeagueProfiles {
  saveId: string;
  leagues: LeagueProfileSummary[];
}

export interface LeaguePodiumAthlete {
  athleteId: number;
  athleteName: string;
  seasonRank: number;
  totalChampionshipPointsThousandths: number;
}

export interface LeaguePodiumSeason {
  seasonNumber: number;
  leagueId: number;
  isComplete: boolean;
  hasFinalTable: boolean;
  champion: LeaguePodiumAthlete | null;
  runnerUp: LeaguePodiumAthlete | null;
  third: LeaguePodiumAthlete | null;
  winningMarginThousandths: number | null;
}

export interface LeagueAllTimeRow {
  athleteId: number;
  athleteName: string;
  totalLeaguePointsThousandths: number;
  completedSeasons: number;
  bestRank: number;
  titles: number;
  podiums: number;
  runnerUps: number;
}

export interface LeagueRecordHolder {
  athleteId: number;
  athleteName: string;
  seasonNumbers: number[];
  seasonNumber: number | null;
  valueThousandths: number | null;
}

export interface LeagueRecordEntry {
  recordKey: string;
  label: string;
  value: number;
  valueDisplay: string;
  holders: LeagueRecordHolder[];
  scopeNote: string | null;
}

export interface LeagueCurrentSeason {
  seasonNumber: number;
  leagueId: number;
  isComplete: boolean;
  hasFinalTable: boolean;
}

export interface LeagueProfile {
  saveId: string;
  leagueKey: string;
  name: string;
  kind: string;
  feederDivision: number;
  leagueLevel: string | null;
  sportingColor: number;
  sportingColorName: string;
  totalSeasons: number;
  completedSeasons: number;
  uniqueAthletes: number;
  firstSeasonNumber: number | null;
  lastCompletedSeasonNumber: number | null;
  currentSeason: LeagueCurrentSeason | null;
  podiumHistory: LeaguePodiumSeason[];
  allTimeLeaderboard: LeagueAllTimeRow[];
  records: LeagueRecordEntry[];
}

/**
 * One aggregated overview request per save. The frontend never reconstructs
 * league history by fetching every season separately.
 */
export async function fetchLeagueProfiles(saveId: string, signal?: AbortSignal): Promise<LeagueProfiles> {
  return fetchJson<LeagueProfiles>(`/api/saves/${saveId}/leagues`, { signal });
}

/** One aggregated profile request per league; podium, leaderboard and records arrive together. */
export async function fetchLeagueProfile(
  saveId: string,
  leagueKey: string,
  signal?: AbortSignal,
): Promise<LeagueProfile> {
  return fetchJson<LeagueProfile>(`/api/saves/${saveId}/leagues/${encodeURIComponent(leagueKey)}`, {
    signal,
  });
}
