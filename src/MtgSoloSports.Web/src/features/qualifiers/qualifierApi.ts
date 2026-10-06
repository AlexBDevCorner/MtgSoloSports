import { fetchJson } from '../../shared/api/http';

/**
 * One athlete in a persisted qualifier event field.
 * Mirrors `QualifierEventMember` (camelCased): role is
 * `Incumbent`/`Challenger`, `isQualified` marks the top-8 cutoff.
 */
export interface QualifierEventMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  role: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromSeasonRank: number;
  qualifierRank: number;
  qualifierScoreThousandths: number;
  baseScoreThousandths: number;
  roundWins: number;
  isQualified: boolean;
}

/**
 * One persisted qualifier event for a season transition.
 * Mirrors `GetQualifierEventResponse` (camelCased): `boundary` is
 * `Superleague`/`Feeder1Feeder2`/`Feeder2Feeder3`, `sportingColor` is null
 * for the Superleague event. Field size, round count and the top-8 cutoff
 * (`winners`) come from data so the same UI serves 32-athlete Superleague
 * and 16-athlete feeder fields.
 */
export interface QualifierEvent {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  boundaryId: number;
  boundary: string;
  sportingColor: number | null;
  sportingColorName: string;
  qualifierSize: number;
  roundCount: number;
  winners: number;
  checksum: string;
  standings: QualifierEventMember[];
}

/** All persisted qualifier events for one transition (1 v1, 17 tiered). */
export interface QualifierList {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  events: QualifierEvent[];
}

export async function fetchQualifierList(
  saveId: string,
  fromSeason?: number | null,
  signal?: AbortSignal,
): Promise<QualifierList> {
  const query = fromSeason === null || fromSeason === undefined ? '' : `?fromSeason=${fromSeason}`;
  return fetchJson<QualifierList>(`/api/saves/${saveId}/qualifiers${query}`, {
    signal,
  });
}

export async function fetchQualifierEvent(
  saveId: string,
  boundary: string,
  color: string,
  fromSeason?: number | null,
  signal?: AbortSignal,
): Promise<QualifierEvent> {
  const query = fromSeason === null || fromSeason === undefined ? '' : `?fromSeason=${fromSeason}`;
  return fetchJson<QualifierEvent>(
    `/api/saves/${saveId}/qualifiers/${encodeURIComponent(boundary)}/${encodeURIComponent(color)}${query}`,
    { signal },
  );
}

/** Runs a single feeder qualifier (boundary `F1F2`/`F2F3`, color name). */
export async function runFeederQualifier(
  saveId: string,
  boundary: string,
  color: string,
  signal?: AbortSignal,
): Promise<QualifierEvent> {
  return fetchJson<QualifierEvent>(
    `/api/saves/${saveId}/qualifiers/${encodeURIComponent(boundary)}/${encodeURIComponent(color)}`,
    { method: 'POST', signal },
  );
}

export interface RunAllQualifiersResult {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  totalStandings: number;
  totalRounds: number;
  alreadyCompleted: string[];
  executedNow: string[];
}

/**
 * Runs all remaining qualifiers in backend canonical order (Superleague,
 * then F1↔F2 by color, then F2↔F3 by color). Idempotent: already-resolved
 * events are skipped, never rerun.
 */
export async function runRemainingQualifiers(
  saveId: string,
  signal?: AbortSignal,
): Promise<RunAllQualifiersResult> {
  return fetchJson<RunAllQualifiersResult>(`/api/saves/${saveId}/qualifiers/run-all`, {
    method: 'POST',
    signal,
  });
}
