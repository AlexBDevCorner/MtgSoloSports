import { fetchJson } from '../../shared/api/http';

export interface StageRoundPlacement {
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

export interface StageRound {
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
  placements: StageRoundPlacement[];
}

export interface StageRounds {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  completedRounds: number;
  roundsPerStage: number;
  isStageComplete: boolean;
  rounds: StageRound[];
}

export interface AdvanceRoundResult {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
  placements: StageRoundPlacement[];
}

export interface CompleteStageResult {
  saveId: string;
  seasonNumber: number;
  leagueId: number;
  leagueName: string;
  stageNumber: number;
  completedRounds: number;
  nextStageNumber: number | null;
}

export async function fetchStageRounds(
  saveId: string,
  leagueId: number,
  stageNumber: number,
  signal?: AbortSignal,
): Promise<StageRounds> {
  return fetchJson<StageRounds>(
    `/api/saves/${saveId}/leagues/${leagueId}/stages/${stageNumber}/rounds`,
    { signal },
  );
}

export async function advanceRound(saveId: string, leagueId: number): Promise<AdvanceRoundResult> {
  return fetchJson<AdvanceRoundResult>(
    `/api/saves/${saveId}/leagues/${leagueId}/rounds/advance`,
    { method: 'POST' },
  );
}

export async function completeStage(saveId: string, leagueId: number): Promise<CompleteStageResult> {
  return fetchJson<CompleteStageResult>(
    `/api/saves/${saveId}/leagues/${leagueId}/stages/complete`,
    { method: 'POST' },
  );
}
