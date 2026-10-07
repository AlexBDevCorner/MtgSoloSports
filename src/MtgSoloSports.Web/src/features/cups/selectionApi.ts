import { fetchJson } from '../../shared/api/http';
import type { SelectionKey } from '../events/eventModel';

/** Another team an athlete could have represented (Type Cup only). */
export interface SelectionAlternative {
  teamName: string;
  rank: number;
  fieldsTeam: boolean;
}

/** Why a Type Cup member represents its type (mirrors the backend reason codes). */
export type SelectionReason = 'Capped' | 'OnlyType' | 'BestRank' | 'Balanced';

/**
 * One ranked athlete of one team. `rank` is the position in the team's
 * ranking (color ranking, or creature-type ranking); `selectionRank` is the
 * #1..#4 squad number of a selected athlete. The Type Cup adds where the
 * athlete plays, whether it was already capped and the member's reason.
 */
export interface SelectionCandidate {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  rank: number;
  selected: boolean;
  selectionRank: number | null;
  assignedTeam?: string | null;
  capped?: boolean;
  reason?: SelectionReason | null;
  alternatives?: SelectionAlternative[];
  finalRatingThousandths: number;
  bonusNormThousandths: number;
  performanceNormThousandths: number;
  formNormThousandths: number;
  prestigeNormThousandths: number;
  bonusRawThousandths: number;
  performanceRawThousandths: number;
  formRaw: number;
  prestigeRaw: number;
  /** Source-season league name, or "Pool" for pool athletes (MSS-064). Absent on legacy reports. */
  sourceLeagueName?: string | null;
  /** Source-season tier: 0 Superleague, 1 Feeder 1, 2 Feeder 2, 3 Feeder 3, null Pool. */
  sourceLeagueLevel?: number | null;
  /** Competition-strength factor in permille (1000/800/600/400, 0 for Pool). */
  strengthFactorPermille?: number;
  /** Unadjusted source-season championship points behind the adjusted performance raw. */
  unadjustedPerformanceThousandths?: number;
  /** Unadjusted final-ten-stage form aggregate behind the adjusted form raw. */
  unadjustedFormAggregate?: number;
  /** League-aware prestige breakdown (MSS-065), scaled quarter-points summing to prestigeRaw. */
  prestigeSuperTitleRaw?: number;
  prestigeFeeder1TitleRaw?: number;
  prestigeFeeder2TitleRaw?: number;
  prestigeFeeder3TitleRaw?: number;
  prestigeAppearanceRaw?: number;
  prestigeSuperStageRaw?: number;
  prestigeFeeder1StageRaw?: number;
  prestigeFeeder2StageRaw?: number;
  prestigeFeeder3StageRaw?: number;
  prestigeMajorCupRaw?: number;
}

export interface SelectionTeam {
  teamKey: string;
  teamName: string;
  /** Athletes ranked for this team; 0 when the selection has no stored ranking. */
  candidateCount: number;
  ranking: SelectionCandidate[];
}

export interface SelectionMissedTeam {
  teamName: string;
  candidateCount: number;
}

/** Stored explanation of one Cup squad selection; identical shape for both Cups. */
export interface SelectionReport {
  saveId: string;
  sourceSeasonNumber: number;
  rulesVersion: number;
  hasFullRanking: boolean;
  teamSize: number;
  bonusWeightPermille: number;
  performanceWeightPermille: number;
  formWeightPermille: number;
  prestigeWeightPermille: number;
  teams: SelectionTeam[];
  missedTeams?: SelectionMissedTeam[];
}

const CUP_SEGMENT: Record<SelectionKey, string> = {
  'color-cup-selection': 'color',
  'type-cup-selection': 'type',
};

/** Reads the stored selection report; 404 until the selection is resolved. */
export function fetchSelectionReport(
  saveId: string,
  key: SelectionKey,
  season: number | null,
  signal?: AbortSignal,
): Promise<SelectionReport> {
  const query = season !== null ? `?sourceSeason=${season}` : '';
  return fetchJson<SelectionReport>(`/api/saves/${saveId}/cups/${CUP_SEGMENT[key]}/selection-report${query}`, {
    signal,
  });
}

/** Resolves the selection on the backend (one saved lifecycle step). */
export function announceSelection(saveId: string, key: SelectionKey, season: number): Promise<unknown> {
  return fetchJson<unknown>(`/api/saves/${saveId}/cups/${CUP_SEGMENT[key]}/teams?sourceSeason=${season}`, {
    method: 'POST',
  });
}
