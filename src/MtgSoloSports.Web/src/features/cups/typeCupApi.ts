import { fetchJson } from '../../shared/api/http';

export interface TypeCupTeamStanding {
  creatureType: string;
  teamName: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  groupWins: number;
  roundWins: number;
  medal: string;
}

export interface TypeCupTeamLeg {
  athleteId: number;
  name: string;
  creatureType: string;
  selectionRank: number;
  groupNumber: number;
  groupRank: number;
  groupScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
}

export interface TypeCupTeamResult {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  teamCount: number;
  groupCount: number;
  groupRounds: number;
  checksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  championCreatureType: string;
  championTeamName: string;
  teams: TypeCupTeamStanding[];
  legs: TypeCupTeamLeg[];
}

export async function fetchTypeCupTeam(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTeamResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<TypeCupTeamResult>(
    `/api/saves/${saveId}/cups/type/team${query}`,
    { signal },
  );
}

export async function runTypeCupTeam(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTeamResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<TypeCupTeamResult>(
    `/api/saves/${saveId}/cups/type/team${query}`,
    { method: 'POST', signal },
  );
}
