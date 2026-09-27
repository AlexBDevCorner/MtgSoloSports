import { fetchJson } from '../../shared/api/http';

export interface ColorCupIndividualStanding {
  athleteId: number;
  name: string;
  sportingColor: string;
  selectionRank: number;
  cupRank: number;
  cupScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
  medal: string;
}

export interface ColorCupIndividualResult {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  cupSize: number;
  rounds: number;
  checksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  championAthleteId: number;
  championName: string;
  standings: ColorCupIndividualStanding[];
}

export async function fetchColorCupIndividual(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupIndividualResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<ColorCupIndividualResult>(
    `/api/saves/${saveId}/cups/color/individual${query}`,
    { signal },
  );
}

export async function runColorCupIndividual(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupIndividualResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<ColorCupIndividualResult>(
    `/api/saves/${saveId}/cups/color/individual${query}`,
    { method: 'POST', signal },
  );
}

export interface ColorCupTeamStanding {
  sportingColor: number;
  teamName: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  groupWins: number;
  roundWins: number;
  medal: string;
}

export interface ColorCupTeamLeg {
  athleteId: number;
  name: string;
  sportingColor: string;
  selectionRank: number;
  groupNumber: number;
  groupRank: number;
  groupScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
}

export interface ColorCupTeamResult {
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
  championSportingColor: number;
  championTeamName: string;
  teams: ColorCupTeamStanding[];
  legs: ColorCupTeamLeg[];
}

export async function fetchColorCupTeam(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupTeamResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<ColorCupTeamResult>(
    `/api/saves/${saveId}/cups/color/team${query}`,
    { signal },
  );
}

export async function runColorCupTeam(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupTeamResult> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<ColorCupTeamResult>(
    `/api/saves/${saveId}/cups/color/team${query}`,
    { method: 'POST', signal },
  );
}
