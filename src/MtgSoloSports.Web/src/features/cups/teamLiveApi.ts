import { fetchJson } from '../../shared/api/http';

export interface TypeCupLiveTeam {
  creatureType: string;
  teamName: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  medal: string;
}

export interface TypeCupTeamLive {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  teamCount: number;
  groupCount: number;
  groupRounds: number;
  completedRounds: number;
  totalRounds: number;
  currentGroupNumber: number;
  currentRoundNumber: number;
  isComplete: boolean;
  isProvisional: boolean;
  championCreatureType: string;
  checksum: string;
  lastCompletedGroupNumber: number;
  lastCompletedRoundNumber: number;
  teams: TypeCupLiveTeam[];
}

export interface ColorCupLiveTeam {
  sportingColor: number;
  teamName: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  medal: string;
}

export interface ColorCupTeamLive {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  teamCount: number;
  groupCount: number;
  groupRounds: number;
  completedRounds: number;
  totalRounds: number;
  currentGroupNumber: number;
  currentRoundNumber: number;
  isComplete: boolean;
  isProvisional: boolean;
  championSportingColor: number;
  championTeamName: string;
  checksum: string;
  lastCompletedGroupNumber: number;
  lastCompletedRoundNumber: number;
  teams: ColorCupLiveTeam[];
}

function liveQuery(sourceSeason: number | null): string {
  return sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
}

export async function fetchTypeCupTeamLive(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTeamLive> {
  return fetchJson<TypeCupTeamLive>(
    `/api/saves/${saveId}/cups/type/team/live${liveQuery(sourceSeason)}`,
    { signal },
  );
}

export async function advanceTypeCupTeamRound(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTeamLive> {
  return fetchJson<TypeCupTeamLive>(
    `/api/saves/${saveId}/cups/type/team/rounds/advance${liveQuery(sourceSeason)}`,
    { method: 'POST', signal },
  );
}

export async function fetchColorCupTeamLive(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupTeamLive> {
  return fetchJson<ColorCupTeamLive>(
    `/api/saves/${saveId}/cups/color/team/live${liveQuery(sourceSeason)}`,
    { signal },
  );
}

export async function advanceColorCupTeamRound(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupTeamLive> {
  return fetchJson<ColorCupTeamLive>(
    `/api/saves/${saveId}/cups/color/team/rounds/advance${liveQuery(sourceSeason)}`,
    { method: 'POST', signal },
  );
}
