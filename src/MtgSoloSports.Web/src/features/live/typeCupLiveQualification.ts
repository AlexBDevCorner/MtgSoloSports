import type { TypeCupDraw, TypeCupTournament } from '../cups/typeCupTournamentApi';
import type { TypeCupStageInfo } from './typeCupLiveModel';

/**
 * Provisional qualification cutoff for the live Type Cup team standings
 * (MSS-080). DOM-free: derives the current group's guaranteed Final places
 * from persisted backend facts only (completed tournament tables first, then
 * the persisted draw). The frontend never recomputes quotas, wildcards or
 * tie-breaks; it only reads the authoritative per-group cutoff.
 *
 * Returns null when no qualification cutoff applies: Final/single stages,
 * direct Finals, or missing stage/draw data. Callers must render no
 * qualification highlight in that case.
 */
export interface LiveQualificationCutoff {
  /** 1-based qualification group the cutoff belongs to (never leaks across groups). */
  qualificationGroup: number;
  /** Persisted group size when known; null when the draw omits it. */
  groupSize: number | null;
  /** Guaranteed automatic places for this group (authoritative per-group quota). */
  guaranteed: number;
  /** Global wildcard places for the edition (0 when the policy has none). */
  wildcardCount: number;
  /** True when the wildcard policy leaves a cross-group contested place. */
  hasWildcard: boolean;
  /** True when rank guaranteed+1 exists and competes across groups (not guaranteed). */
  hasContestedPlace: boolean;
  /** Persisted policy version (1 fixed quotas, 2 guaranteed + wildcards). */
  policyVersion: number;
  /** True when the group's final table is persisted (confirmed), false while provisional. */
  isConfirmed: boolean;
}

function isValidQuota(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function policyOf(tournament: TypeCupTournament | null, draw: TypeCupDraw | null): number {
  const fromTournament = tournament?.qualificationPolicyVersion;
  if (isValidQuota(fromTournament)) {
    return fromTournament;
  }
  const fromDrawPolicy = draw?.qualificationPolicyVersion;
  if (isValidQuota(fromDrawPolicy)) {
    return fromDrawPolicy;
  }
  const fromFormat = draw?.tournamentFormatVersion;
  if (isValidQuota(fromFormat)) {
    return fromFormat;
  }
  return 1;
}

export function resolveLiveQualificationCutoff(args: {
  stage: TypeCupStageInfo | null;
  draw: TypeCupDraw | null;
  tournament: TypeCupTournament | null;
}): LiveQualificationCutoff | null {
  const { stage, draw, tournament } = args;
  if (!stage || stage.kind !== 'qualification' || stage.qualificationGroup == null) {
    return null;
  }
  const qual = stage.qualificationGroup;
  if (!Number.isInteger(qual) || qual < 1) {
    return null;
  }
  if (tournament?.isDirectFinal || draw?.isDirectFinal) {
    return null;
  }

  // Completed groups carry their own persisted cutoff (most authoritative).
  const completed = tournament?.qualificationGroups?.find((g) => g.qualificationGroup === qual);
  if (completed) {
    const guaranteed = completed.guaranteedPlaces ?? completed.finalPlaces;
    if (!isValidQuota(guaranteed)) {
      return null;
    }
    const policyVersion = policyOf(tournament, draw);
    const wildcardCount = tournament?.wildcardCount ?? draw?.wildcardCount ?? 0;
    const hasWildcard = policyVersion === 2 && wildcardCount > 0;
    const size = completed.groupSize;
    const hasContestedPlace =
      hasWildcard &&
      (completed.wildcardCandidate != null || (Number.isInteger(size) && guaranteed < size));
    return {
      qualificationGroup: qual,
      groupSize: Number.isInteger(size) ? size : null,
      guaranteed,
      wildcardCount,
      hasWildcard,
      hasContestedPlace,
      policyVersion,
      isConfirmed: true,
    };
  }

  // Live provisional groups read the persisted draw quota for this group only.
  if (!draw || draw.isDirectFinal) {
    return null;
  }
  if ((draw.qualificationGroupCount ?? 0) <= 0) {
    return null;
  }
  const drawGroup = draw.groups?.find((g) => g.qualificationGroup === qual) ?? null;
  const perGroup =
    drawGroup?.finalPlaces ??
    draw.guaranteedPlacesPerGroup?.[qual - 1] ??
    draw.finalPlacesPerGroup?.[qual - 1];
  if (!isValidQuota(perGroup)) {
    return null;
  }
  const groupSize =
    drawGroup?.groupSize ?? draw.groupSizes?.[qual - 1] ?? null;
  const policyVersion = policyOf(null, draw);
  const wildcardCount = draw.wildcardCount ?? 0;
  const hasWildcard = policyVersion === 2 && wildcardCount > 0;
  const hasContestedPlace =
    hasWildcard && (groupSize == null || perGroup < groupSize);
  return {
    qualificationGroup: qual,
    groupSize: typeof groupSize === 'number' && Number.isInteger(groupSize) ? groupSize : null,
    guaranteed: perGroup,
    wildcardCount,
    hasWildcard,
    hasContestedPlace,
    policyVersion,
    isConfirmed: false,
  };
}

/** True for a provisional/confirmed qualifying position (0-based index). */
export function isQualifyingIndex(index: number, cutoff: LiveQualificationCutoff | null): boolean {
  if (!cutoff || cutoff.guaranteed <= 0) {
    return false;
  }
  return index < cutoff.guaranteed;
}

/** True for the cross-group contested place (rank guaranteed+1, never guaranteed). */
export function isContestedIndex(
  index: number,
  cutoff: LiveQualificationCutoff | null,
  totalRows: number,
): boolean {
  if (!cutoff || !cutoff.hasContestedPlace) {
    return false;
  }
  return index === cutoff.guaranteed && totalRows > cutoff.guaranteed;
}

/**
 * Small text legend for the live standings cutoff. Always names the count so
 * the cue never relies on color alone; provisional and confirmed wordings
 * stay distinct.
 */
export function liveCutoffLegend(cutoff: LiveQualificationCutoff | null): string | null {
  if (!cutoff) {
    return null;
  }
  const plural = (n: number, one: string, many: string): string => (n === 1 ? one : many);
  if (cutoff.guaranteed <= 0) {
    if (cutoff.hasWildcard && cutoff.wildcardCount > 0) {
      return `No automatic places · rank 1 competes for ${cutoff.wildcardCount} ${plural(cutoff.wildcardCount, 'wildcard', 'wildcards')}`;
    }
    return 'No automatic places';
  }
  const base = cutoff.isConfirmed
    ? `Top ${cutoff.guaranteed} qualified`
    : `Top ${cutoff.guaranteed} provisionally qualify`;
  if (cutoff.hasContestedPlace && cutoff.wildcardCount > 0) {
    const race = cutoff.isConfirmed
      ? `rank ${cutoff.guaranteed + 1} contested ${cutoff.wildcardCount} ${plural(cutoff.wildcardCount, 'wildcard', 'wildcards')} across groups`
      : `rank ${cutoff.guaranteed + 1} competes for ${cutoff.wildcardCount} ${plural(cutoff.wildcardCount, 'wildcard', 'wildcards')} across groups`;
    return `${base} · ${race}`;
  }
  if (cutoff.hasWildcard && cutoff.wildcardCount > 0) {
    return `${base} · plus ${cutoff.wildcardCount} ${plural(cutoff.wildcardCount, 'wildcard', 'wildcards')} across groups`;
  }
  return base;
}

/** Cut-line label rendered between the last qualifying and first nonqualifying row. */
export function liveCutLineLabel(cutoff: LiveQualificationCutoff | null): string | null {
  if (!cutoff || cutoff.guaranteed <= 0) {
    return null;
  }
  return cutoff.isConfirmed
    ? `Qualification cut — top ${cutoff.guaranteed} qualified`
    : `Qualification cut — top ${cutoff.guaranteed} provisionally qualify`;
}
