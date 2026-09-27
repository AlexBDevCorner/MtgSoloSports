import { fetchJson } from '../../shared/api/http';

export interface HistorySeason {
  seasonNumber: number;
  hasSuperleague: boolean;
  isComplete: boolean;
  competitionCount: number;
  completedStages: number;
  totalRounds: number;
}

export interface HistorySeasons {
  saveId: string;
  seasons: HistorySeason[];
}

export interface HistoryCompetition {
  leagueId: number;
  name: string;
  kind: string;
  sportingColor: number;
  sportingColorName: string;
  stageCount: number;
  completedStages: number;
  totalRounds: number;
  hasFinalTable: boolean;
}

export interface HistoryCompetitions {
  saveId: string;
  seasonNumber: number;
  isSeasonComplete: boolean;
  competitions: HistoryCompetition[];
}

export interface HistoryStage {
  stageNumber: number;
  completedRounds: number;
  roundsPerStage: number;
  isComplete: boolean;
  hasStandings: boolean;
  roundCount: number;
}

export interface HistoryStages {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stages: HistoryStage[];
}

export interface HistoryRoundSummary {
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
}

export interface HistoryRounds {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  completedRounds: number;
  roundsPerStage: number;
  isStageComplete: boolean;
  rounds: HistoryRoundSummary[];
}

/**
 * Same presentation shape as the live round model
 * (`StageRoundPlacement` in liveApi): an old round feeds the identical table
 * without resimulation.
 */
export interface HistoryRoundPlacement {
  athleteId: number;
  name: string;
  position: number;
  baseThousandths: number;
  activeBonusThousandths: number;
  finalThousandths: number;
  cumulativeBeforeThousandths: number;
  cumulativeAfterThousandths: number;
  rankBefore: number;
  rankAfter: number;
  rankMovement: number;
  imageUrl: string | null;
  setCode: string | null;
  typeLine: string;
}

export interface HistoryRoundReplay {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  placements: HistoryRoundPlacement[];
}

export interface HistoryStageStanding {
  athleteId: number;
  name: string;
  stageRank: number;
  stageScoreThousandths: number;
  baseScoreThousandths: number;
  championshipPointsThousandths: number;
  roundWins: number;
  earnedBonusThousandths: number;
}

export interface HistoryStageStandings {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  isStageComplete: boolean;
  stageChecksum: string;
  standings: HistoryStageStanding[];
}

export interface HistorySeasonTableEntry {
  athleteId: number;
  name: string;
  seasonRank: number;
  totalChampionshipPointsThousandths: number;
  totalStageScoreThousandths: number;
  totalBaseScoreThousandths: number;
  stageWins: number;
  roundWins: number;
  isChampion: boolean;
}

export interface HistorySeasonTable {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  isSeasonComplete: boolean;
  seasonChecksum: string;
  standings: HistorySeasonTableEntry[];
}

export async function fetchHistorySeasons(
  saveId: string,
  signal?: AbortSignal,
): Promise<HistorySeasons> {
  return fetchJson<HistorySeasons>(`/api/saves/${saveId}/history/seasons`, {
    signal,
  });
}

export async function fetchHistoryCompetitions(
  saveId: string,
  seasonNumber: number,
  signal?: AbortSignal,
): Promise<HistoryCompetitions> {
  return fetchJson<HistoryCompetitions>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions`,
    { signal },
  );
}

export async function fetchHistoryStages(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  signal?: AbortSignal,
): Promise<HistoryStages> {
  return fetchJson<HistoryStages>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/stages`,
    { signal },
  );
}

export async function fetchHistoryRounds(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  stageNumber: number,
  signal?: AbortSignal,
): Promise<HistoryRounds> {
  return fetchJson<HistoryRounds>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/stages/${stageNumber}/rounds`,
    { signal },
  );
}

export async function fetchHistoryRoundReplay(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  stageNumber: number,
  roundNumber: number,
  signal?: AbortSignal,
): Promise<HistoryRoundReplay> {
  return fetchJson<HistoryRoundReplay>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/stages/${stageNumber}/rounds/${roundNumber}`,
    { signal },
  );
}

export async function fetchHistoryStageStandings(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  stageNumber: number,
  signal?: AbortSignal,
): Promise<HistoryStageStandings> {
  return fetchJson<HistoryStageStandings>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/stages/${stageNumber}/standings`,
    { signal },
  );
}

export async function fetchHistorySeasonTable(
  saveId: string,
  seasonNumber: number,
  leagueId: number,
  signal?: AbortSignal,
): Promise<HistorySeasonTable> {
  return fetchJson<HistorySeasonTable>(
    `/api/saves/${saveId}/history/seasons/${seasonNumber}/competitions/${leagueId}/table`,
    { signal },
  );
}
