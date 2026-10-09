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

/**
 * Reads one feeder qualifier event for Live in any persisted state (pending,
 * in-progress, complete). Never resimulates; GETs never consume RNG.
 */
export async function fetchQualifierRounds(
  saveId: string,
  boundary: string,
  color: string,
  fromSeason?: number | null,
  signal?: AbortSignal,
): Promise<QualifierRoundsDetail> {
  const query = fromSeason === null || fromSeason === undefined ? '' : `?fromSeason=${fromSeason}`;
  return fetchJson<QualifierRoundsDetail>(
    `/api/saves/${saveId}/qualifiers/${encodeURIComponent(boundary)}/${encodeURIComponent(color)}/rounds${query}`,
    { signal },
  );
}

/** Reads one persisted feeder qualifier round for immutable replay. */
export async function fetchQualifierRound(
  saveId: string,
  boundary: string,
  color: string,
  round: number,
  fromSeason?: number | null,
  signal?: AbortSignal,
): Promise<QualifierRoundView> {
  const query = fromSeason === null || fromSeason === undefined ? '' : `?fromSeason=${fromSeason}`;
  return fetchJson<QualifierRoundView>(
    `/api/saves/${saveId}/qualifiers/${encodeURIComponent(boundary)}/${encodeURIComponent(color)}/rounds/${round}${query}`,
    { signal },
  );
}

/** Persists exactly one next round of the selected feeder qualifier. */
export async function playFeederQualifierRound(
  saveId: string,
  boundary: string,
  color: string,
  signal?: AbortSignal,
): Promise<PlayFeederRoundResult> {
  return fetchJson<PlayFeederRoundResult>(
    `/api/saves/${saveId}/qualifiers/${encodeURIComponent(boundary)}/${encodeURIComponent(color)}/rounds/next`,
    { method: 'POST', signal },
  );
}

export interface QualifierFieldMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  role: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromSeasonRank: number;
  imageUrl: string | null;
  setCode: string | null;
  typeLine: string;
}

export interface QualifierRoundSummary {
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
}

/**
 * One feeder qualifier event in any persisted state (pending, in-progress,
 * complete) for Live. `roundsPlayed` is this event's own 0..16 / 16 progress;
 * phase-wide 17-event / 272-round totals live on the overview only.
 */
export interface QualifierRoundsDetail {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  boundary: string;
  boundaryId: number;
  sportingColor: number;
  sportingColorName: string;
  roundsPlayed: number;
  totalRounds: number;
  isComplete: boolean;
  checksum: string;
  rounds: QualifierRoundSummary[];
  field: QualifierFieldMember[];
  standings: QualifierEventMember[];
}

export interface QualifierRoundView {
  seasonNumber: number;
  event: string;
  title: string;
  group: number | null;
  roundNumber: number;
  rulesVersion: number;
  payloadChecksum: string;
  rngBeforeState: number;
  rngBeforeStream: number;
  rngAfterState: number;
  rngAfterStream: number;
  placements: QualifierRoundPlacement[];
}

export interface QualifierRoundPlacement {
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

export interface PlayFeederRoundResult {
  round: QualifierRoundView;
  roundsPlayed: number;
  totalRounds: number;
  isComplete: boolean;
  boundary: string;
  sportingColor: number;
  sportingColorName: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
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
