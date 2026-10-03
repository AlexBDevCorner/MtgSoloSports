import { ApiError, fetchJson } from '../../shared/api/http';
import type { CupKind } from '../routing/routes';

export type CupName = 'Color' | 'Type';
export type EditionState = 'Selected' | 'InProgress' | 'Completed';

export interface CupEditionPodiumTeam {
  teamKey: string;
  teamName: string;
  teamRank: number;
  medal: string;
  teamScoreThousandths: number;
}

export interface CupEditionChampion {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  teamKey: string;
}

/** `podium` is empty until the team event finished; `individualChampion` is Color only. */
export interface CupEdition {
  cup: CupName;
  sourceSeasonNumber: number;
  state: EditionState;
  teamCount: number;
  podium: CupEditionPodiumTeam[];
  individualChampion: CupEditionChampion | null;
}

export interface CupTeamSummary {
  teamKey: string;
  teamName: string;
  editions: number;
  gold: number;
  silver: number;
  bronze: number;
  bestRank: number | null;
  lastSeasonNumber: number;
}

export interface CupEditions {
  saveId: string;
  editions: CupEdition[];
  colorTeams: CupTeamSummary[];
  typeTeams: CupTeamSummary[];
}

export interface CupTeamLeg {
  groupNumber: number;
  groupRank: number;
  groupSize: number;
  groupScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
}

export interface CupTeamIndividual {
  cupRank: number;
  cupScoreThousandths: number;
  medal: string;
}

export interface CupTeamSquadMember {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  selectionRank: number;
  finalRatingThousandths: number;
  bonusNormThousandths: number;
  performanceNormThousandths: number;
  formNormThousandths: number;
  prestigeNormThousandths: number;
  reason: string | null;
  leg: CupTeamLeg | null;
  individual: CupTeamIndividual | null;
}

/** Result fields are null until the team event of that edition finished. */
export interface CupTeamSeason {
  sourceSeasonNumber: number;
  state: EditionState;
  teamCount: number;
  teamRank: number | null;
  medal: string | null;
  teamScoreThousandths: number | null;
  teamBaseThousandths: number | null;
  groupWins: number | null;
  roundWins: number | null;
  squad: CupTeamSquadMember[];
}

export interface CupTeamRosterEntry {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  caps: number;
  firstSeasonNumber: number;
  lastSeasonNumber: number;
  totalLegScoreThousandths: number;
  bestGroupRank: number | null;
  bestSelectionRank: number;
}

export interface CupTeamIndividualMedal {
  sourceSeasonNumber: number;
  athleteId: number;
  name: string;
  medal: string;
}

export interface CupTeamHistory {
  saveId: string;
  cup: CupName;
  teamKey: string;
  teamName: string;
  honours: {
    editions: number;
    gold: number;
    silver: number;
    bronze: number;
    bestRank: number | null;
    bestRankSeasonNumber: number | null;
    groupWins: number;
    roundWins: number;
    totalScoreThousandths: number;
  };
  seasons: CupTeamSeason[];
  roster: CupTeamRosterEntry[];
  individualMedals: CupTeamIndividualMedal[];
}

/** Every Cup edition of the save plus the all-time team tables. */
export function fetchCupEditions(saveId: string, signal?: AbortSignal): Promise<CupEditions> {
  return fetchJson<CupEditions>(`/api/saves/${saveId}/cups/editions`, { signal });
}

export function teamHistoryUrl(saveId: string, cup: CupKind, teamKey: string): string {
  return `/api/saves/${saveId}/cups/${cup}/teams/${encodeURIComponent(teamKey)}/history`;
}

/** One team's history; 404 when the team never fielded a squad. */
export function fetchCupTeamHistory(
  saveId: string,
  cup: CupKind,
  teamKey: string,
  signal?: AbortSignal,
): Promise<CupTeamHistory> {
  return fetchJson<CupTeamHistory>(teamHistoryUrl(saveId, cup, teamKey), { signal });
}

/** Resolves to null when the resource does not exist yet (404); other failures still throw. */
export async function optional<T>(request: Promise<T>): Promise<T | null> {
  try {
    return await request;
  } catch (failure: unknown) {
    if (failure instanceof ApiError && failure.status === 404) {
      return null;
    }
    throw failure;
  }
}
