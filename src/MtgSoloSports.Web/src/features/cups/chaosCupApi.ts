import { fetchJson } from '../../shared/api/http';

/**
 * Chaos Cup browsing over the MSS-107 read endpoints plus the MSS-110 scoped
 * Live mutations. Reads return persisted backend facts; mutations run on the
 * backend (isolated per-group RNG, save RNG only for draws) and the view
 * re-reads the affected group/edition afterwards. Only the selected group's
 * rounds are fetched, never the whole 1008-round tournament.
 */

export interface ChaosCupStageSummary {
  stageIndex: number;
  groupCount: number;
  entrantCount: number;
  isFinal: boolean;
  state: string;
  isDrawn: boolean;
  isComplete: boolean;
  drawChecksum: string;
  groupsCompleted: number;
  roundsPlayed: number;
  totalRounds: number;
  standingsCount: number;
}

export interface ChaosCupEdition {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  rulesVersion: number;
  chaosFormatVersion: number;
  fieldSize: number;
  editionChecksum: string;
  state: string;
  isComplete: boolean;
  stageCount: number;
  totalGroups: number;
  totalRounds: number;
  roundsPlayed: number;
  groupsCompleted: number;
  stages: ChaosCupStageSummary[];
}

export interface ChaosCupEntrant {
  saveAthleteId: number;
  name: string;
  sportingColor: number;
  sportingColorName: string;
  sourceLeagueId: number | null;
  sourceLeagueKind: number;
  sourceFeederDivision: number;
  isPoolInvite: boolean;
  sourceLabel: string;
  effectiveBonusThousandths: number;
}

export interface ChaosCupStanding {
  saveAthleteId: number;
  name: string;
  groupRank: number;
  groupScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
  isAdvanced: boolean;
}

export interface ChaosCupCutoff {
  p16: { saveAthleteId: number; name: string; groupRank: number; groupScoreThousandths: number };
  p17: { saveAthleteId: number; name: string; groupRank: number; groupScoreThousandths: number };
  gapThousandths: number;
  isProvisional: boolean;
  isOfficial: boolean;
}

export interface ChaosCupGroupRoundSummary {
  roundNumber: number;
  checksum: string;
}

export interface ChaosCupGroup {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  stageIndex: number;
  groupNumber: number;
  isFinal: boolean;
  state: string;
  roundsPlayed: number;
  totalRounds: number;
  isComplete: boolean;
  standingsProvisional: boolean;
  drawChecksum: string;
  entrants: ChaosCupEntrant[];
  standings: ChaosCupStanding[];
  cutoff: ChaosCupCutoff | null;
  rounds: ChaosCupGroupRoundSummary[];
}

export interface ChaosCupRoundPlacement {
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
}

export interface ChaosCupRound {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  stageIndex: number;
  groupNumber: number;
  roundNumber: number;
  rulesVersion: number;
  chaosFormatVersion: number;
  checksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  placements: ChaosCupRoundPlacement[];
}

function seasonQuery(season: number | null): string {
  return season !== null ? `?sourceSeason=${season}` : '';
}

/** Edition overview with six stage summaries; 404 while no field is selected. */
export function fetchChaosEdition(
  saveId: string,
  season: number | null,
  signal?: AbortSignal,
): Promise<ChaosCupEdition> {
  return fetchJson<ChaosCupEdition>(`/api/saves/${saveId}/cups/chaos${seasonQuery(season)}`, { signal });
}

/** One drawn 32-athlete group with standings and the P16/P17 cutoff. */
export function fetchChaosGroup(
  saveId: string,
  stageIndex: number,
  groupNumber: number,
  season: number | null,
  signal?: AbortSignal,
): Promise<ChaosCupGroup> {
  return fetchJson<ChaosCupGroup>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/groups/${groupNumber}${seasonQuery(season)}`,
    { signal },
  );
}

/** One persisted game round for replay; 404 while the round is unplayed. */
export function fetchChaosRound(
  saveId: string,
  stageIndex: number,
  groupNumber: number,
  roundNumber: number,
  season: number | null,
  signal?: AbortSignal,
): Promise<ChaosCupRound> {
  return fetchJson<ChaosCupRound>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/groups/${groupNumber}/rounds/${roundNumber}${seasonQuery(season)}`,
    { signal },
  );
}

/** Simulates exactly one game round in the selected group on the backend. */
export function playChaosRound(
  saveId: string,
  stageIndex: number,
  groupNumber: number,
  season: number | null,
): Promise<unknown> {
  return fetchJson<unknown>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/groups/${groupNumber}/rounds${seasonQuery(season)}`,
    { method: 'POST' },
  );
}

/** Finishes the selected 32-athlete group (all 16 rounds) on the backend. */
export function finishChaosGroup(
  saveId: string,
  stageIndex: number,
  groupNumber: number,
  season: number | null,
): Promise<unknown> {
  return fetchJson<unknown>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/groups/${groupNumber}/complete${seasonQuery(season)}`,
    { method: 'POST' },
  );
}

/** Finishes the current stage (every remaining group in canonical order). */
export function completeChaosStage(
  saveId: string,
  stageIndex: number,
  season: number | null,
): Promise<unknown> {
  return fetchJson<unknown>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/complete${seasonQuery(season)}`,
    { method: 'POST' },
  );
}

/** Draws the next stage once every group of the previous stage completes. */
export function drawChaosStage(
  saveId: string,
  stageIndex: number,
  season: number | null,
): Promise<unknown> {
  return fetchJson<unknown>(
    `/api/saves/${saveId}/cups/chaos/stages/${stageIndex}/draw${seasonQuery(season)}`,
    { method: 'POST' },
  );
}

/** Fast-forwards every remaining stage to the Final (requires confirmation in the view). */
export function completeChaosCup(saveId: string, season: number | null): Promise<unknown> {
  return fetchJson<unknown>(`/api/saves/${saveId}/cups/chaos/complete${seasonQuery(season)}`, {
    method: 'POST',
  });
}
