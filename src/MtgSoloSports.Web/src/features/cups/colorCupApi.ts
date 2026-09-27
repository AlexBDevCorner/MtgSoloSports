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
