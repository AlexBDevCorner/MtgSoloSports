import { ApiError, fetchJson } from '../../shared/api/http';
import { optional } from './cupHistoryApi';

/**
 * Tournament summary for one Type Cup edition (MSS-063 frontend).
 * Mirrors `GetTypeCupTournamentResponse`: direct Finals carry no
 * qualification stage; larger fields carry every qualification group result
 * plus the fresh 32-team Final. Qualification scores never carry to the
 * Final; only Final ranks 1-3 hold official medals/honours. Read-only over
 * persisted rows; the frontend never computes who qualified.
 */
export interface TypeCupTournamentTeam {
  creatureType: string;
  teamRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  medal: string;
  qualified: boolean;
  /** MSS-071: Guaranteed | Wildcard | Eliminated | Qualified (Final rows). */
  qualificationStatus?: string;
}

export interface TypeCupTournamentWildcard {
  creatureType: string;
  qualificationGroup: number;
  groupRank: number;
  teamScoreThousandths: number;
  teamBaseThousandths: number;
  groupSize: number;
  normalizedNumerator: number;
  normalizedDenominator: number;
  tieDraw: boolean;
}

export interface TypeCupTournamentLeg {
  saveAthleteId: number;
  athleteName: string;
  creatureType: string;
  selectionRank: number;
  /** Athlete rank group (1..4: all #1 athletes, then #2, #3, #4). */
  groupNumber: number;
  groupRank: number;
  groupScoreThousandths: number;
  baseScoreThousandths: number;
  qualified: boolean;
}

export interface TypeCupTournamentQualificationGroup {
  qualificationGroup: number;
  groupSize: number;
  finalPlaces: number;
  checksum: string;
  teams: TypeCupTournamentTeam[];
  legs: TypeCupTournamentLeg[];
  qualifiedTeams: string[];
  eliminatedTeams: string[];
  guaranteedPlaces?: number;
  wildcardCandidate?: string | null;
  wildcardWinner?: string | null;
}

export interface TypeCupTournamentFinal {
  teamCount: number;
  checksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  championCreatureType: string;
  teams: TypeCupTournamentTeam[];
  legs: TypeCupTournamentLeg[];
}

export interface TypeCupTournament {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  isDirectFinal: boolean;
  teamCount: number;
  qualificationGroupCount: number;
  groupSizes: number[];
  finalPlacesPerGroup: number[];
  drawChecksum: string;
  qualificationGroups: TypeCupTournamentQualificationGroup[];
  finalists: string[];
  final: TypeCupTournamentFinal | null;
  championCreatureType: string | null;
  tournamentChecksum: string;
  /** MSS-071: 1 fixed quotas, 2 guaranteed plus wildcards. */
  qualificationPolicyVersion?: number;
  wildcardCount?: number;
  guaranteedPlacesPerGroup?: number[];
  wildcards?: TypeCupTournamentWildcard[];
}

export interface TypeCupDrawGroup {
  qualificationGroup: number;
  groupSize: number;
  finalPlaces: number;
  creatureTypes: string[];
}

export interface TypeCupDraw {
  saveId: string;
  sourceSeasonNumber: number;
  sourceSeasonId: number;
  rulesVersion: number;
  tournamentFormatVersion: number;
  isDirectFinal: boolean;
  teamCount: number;
  qualificationGroupCount: number;
  groupSizes: number[];
  finalPlacesPerGroup: number[];
  drawChecksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  groups: TypeCupDrawGroup[];
  qualificationPolicyVersion?: number;
  guaranteedPlacesPerGroup?: number[];
  wildcardCount?: number;
}

/** Tournament summary; 404 while the Final has no persisted result yet. */
export function fetchTypeCupTournament(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTournament> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<TypeCupTournament>(
    `/api/saves/${saveId}/cups/type/tournament${query}`,
    { signal },
  );
}

/** Same, but null while the tournament is still in progress. */
export function optionalTournament(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupTournament | null> {
  return optional(fetchTypeCupTournament(saveId, sourceSeason, signal));
}

/**
 * Persisted random draw; 404/conflict while the draw has not been resolved.
 * Direct Finals resolve to an empty-group shape (no qualification stage).
 */
export function fetchTypeCupDraw(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupDraw> {
  const query = sourceSeason !== null ? `?sourceSeason=${sourceSeason}` : '';
  return fetchJson<TypeCupDraw>(`/api/saves/${saveId}/cups/type/draw${query}`, {
    signal,
  });
}

/** Same, but null while the draw has not been resolved yet. */
export async function optionalDraw(
  saveId: string,
  sourceSeason: number | null,
  signal?: AbortSignal,
): Promise<TypeCupDraw | null> {
  try {
    return await fetchTypeCupDraw(saveId, sourceSeason, signal);
  } catch (failure: unknown) {
    if (failure instanceof ApiError && (failure.status === 404 || failure.status === 409)) {
      return null;
    }
    throw failure;
  }
}
