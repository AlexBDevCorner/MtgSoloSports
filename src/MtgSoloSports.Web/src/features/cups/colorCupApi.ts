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
  imageUrl: string | null;
  /** Additive cutoff fields; null for legacy 16-round editions. */
  qualifyingRank?: number | null;
  qualifiedForFinal?: boolean | null;
  qualifyingScoreThousandths?: number | null;
  finalScoreThousandths?: number | null;
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
  /** Additive format discriminator; absent on older payloads means legacy. */
  formatVersion?: number | null;
  isPhased?: boolean | null;
  qualifyingRounds?: number | null;
  cutoffRank?: number | null;
  finalFieldSize?: number | null;
  qualifyingOrderAthleteIds?: number[] | null;
  qualifierAthleteIds?: number[] | null;
  eliminatedAthleteIds?: number[] | null;
  tieDrawConsumed?: boolean | null;
}

export interface ColorCupIndividualProgressMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  selectionRank: number;
  provisionalRank: number;
  scoreThousandths: number;
  qualifyingRank: number | null;
  advancingStatus: string | null;
  qualifyingScoreThousandths: number | null;
  finalScoreThousandths: number | null;
  imageUrl: string | null;
}

export interface ColorCupIndividualCarryover {
  athleteId: number;
  name: string;
  qualifyingScoreThousandths: number;
  round17BeforeThousandths: number;
  matches: boolean;
}

export interface ColorCupIndividualProgress {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  formatVersion: number;
  isPhased: boolean;
  totalRounds: number;
  roundsPlayed: number;
  isComplete: boolean;
  qualifyingRounds: number;
  cutoffRank: number | null;
  finalFieldSize: number | null;
  qualifyingOrderAthleteIds: number[] | null;
  qualifierAthleteIds: number[] | null;
  eliminatedAthleteIds: number[] | null;
  tieDrawConsumed: boolean | null;
  isProvisionalCutoff: boolean;
  standings: ColorCupIndividualProgressMember[];
  carryover: ColorCupIndividualCarryover[] | null;
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

export async function fetchColorCupIndividualProgress(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<ColorCupIndividualProgress> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<ColorCupIndividualProgress>(
    `/api/saves/${saveId}/cups/color/individual/progress${query}`,
    { signal },
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
  imageUrl: string | null;
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
