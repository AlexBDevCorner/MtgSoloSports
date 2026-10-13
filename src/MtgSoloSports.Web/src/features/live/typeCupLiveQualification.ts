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
  /**
   * Confirmed wildcard winner for this group (MSS-080 correction). Non-null
   * only when the persisted tournament result resolves this group's
   * rank guaranteed+1 candidate as a Final qualifier. Null while provisional,
   * when the policy has no wildcards, or when this group's candidate lost.
   */
  wildcardWinner: string | null;
  /**
   * Persisted qualified team names for a confirmed final table, best-effort
   * identity set (guaranteed + confirmed wildcard winner). Null while
   * provisional or when the persisted group carries no usable identity list
   * (callers fall back to rank-index highlighting). Lets the confirmed table
   * highlight the authoritative teams even when provisional
   * score/alphabetical order disagrees with the official tie-break.
   */
  qualifiedTeams: readonly string[] | null;
  /**
   * Persisted official rank order (team names best-first by backend teamRank)
   * for a confirmed final table. Null while provisional or when the persisted
   * group has no team rows. Lets the confirmed table display the official
   * order instead of provisional score/alphabetical order at tied boundaries.
   */
  officialOrder: readonly string[] | null;
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

/**
 * Persisted identity for a confirmed final table (MSS-080 correction).
 * The provisional live projection sorts tied scores alphabetically with no
 * tie-break, while the backend's TeamEvent.RankTeams breaks ties by
 * group/round place counts, base totals and a seeded draw. At a tied
 * qualifying boundary the provisional order can therefore disagree with the
 * official persisted teamRank. The confirmed table must highlight by
 * persisted identity (qualified flags / official rank order), never by
 * provisional array index alone.
 */
function confirmedIdentity(
  completed: NonNullable<TypeCupTournament['qualificationGroups']>[number],
  guaranteed: number,
  wildcardWinner: string | null,
): { qualifiedTeams: readonly string[] | null; officialOrder: readonly string[] | null } {
  const rows = Array.isArray(completed.teams)
    ? [...completed.teams].sort((a, b) => a.teamRank - b.teamRank)
    : [];
  const officialOrder = rows.length > 0 ? rows.map((row) => row.creatureType) : null;
  if (rows.length > 0) {
    const flagged = rows.filter((row) => row.qualified).map((row) => row.creatureType);
    if (flagged.length > 0) {
      return { qualifiedTeams: flagged, officialOrder };
    }
  }
  const listed = Array.isArray(completed.qualifiedTeams) && completed.qualifiedTeams.length > 0
    ? [...completed.qualifiedTeams]
    : null;
  if (listed) {
    return { qualifiedTeams: listed, officialOrder };
  }
  if (rows.length > 0 && guaranteed >= 0) {
    const derived = rows.slice(0, guaranteed).map((row) => row.creatureType);
    if (wildcardWinner != null && !derived.includes(wildcardWinner)) {
      derived.push(wildcardWinner);
    }
    return { qualifiedTeams: derived.length > 0 ? derived : null, officialOrder };
  }
  return { qualifiedTeams: null, officialOrder };
}

export function resolveLiveQualificationCutoff(args: {
  stage: TypeCupStageInfo | null;
  draw: TypeCupDraw | null;
  tournament: TypeCupTournament | null;
  /**
   * MSS-080 correction: true only when the UI is showing the group's final
   * table (last persisted stage round, fully revealed). Earlier replay rounds
   * are provisional snapshots: the persisted eventual wildcard winner must
   * never make a different provisional rank look confirmed. Defaults to false
   * (fail closed to provisional) so callers must opt into confirmed display.
   */
  isFinalTable?: boolean;
}): LiveQualificationCutoff | null {
  const { stage, draw, tournament, isFinalTable } = args;
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

  // Completed groups carry their own persisted cutoff (most authoritative),
  // but only the final table may render it as confirmed. Earlier replay
  // rounds show the same guaranteed quota provisionally so the eventual
  // wildcard winner never labels a different provisional rank as confirmed.
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
    if (isFinalTable !== true) {
      const hasContestedPlace =
        hasWildcard &&
        (completed.wildcardCandidate != null ||
          size == null ||
          !Number.isInteger(size) ||
          guaranteed < size);
      return {
        qualificationGroup: qual,
        groupSize: Number.isInteger(size) ? size : null,
        guaranteed,
        wildcardCount,
        hasWildcard,
        hasContestedPlace,
        policyVersion,
        isConfirmed: false,
        wildcardWinner: null,
        qualifiedTeams: null,
        officialOrder: null,
      };
    }
    const rawWinner = completed.wildcardWinner;
    const wildcardWinner =
      typeof rawWinner === 'string' && rawWinner.length > 0 ? rawWinner : null;
    // MSS-080 correction: the persisted wildcardWinner resolves the
    // cross-group race. A confirmed winner at rank guaranteed+1 qualified for
    // the Final and must not stay rendered as contested/non-qualifying. Once
    // the tournament Final is persisted the race is settled for every group:
    // winners qualify, losers are eliminated (never still contested). While
    // the Final is unresolved (tournament without a final, e.g. unit mocks),
    // a missing winner with a live candidate stays contested/provisional.
    const isSettled = tournament?.final != null;
    const hasContestedPlace =
      wildcardWinner != null || (isSettled && hasWildcard)
        ? false
        : hasWildcard &&
          (completed.wildcardCandidate != null || (Number.isInteger(size) && guaranteed < size));
    const identity = confirmedIdentity(completed, guaranteed, wildcardWinner);
    return {
      qualificationGroup: qual,
      groupSize: Number.isInteger(size) ? size : null,
      guaranteed,
      wildcardCount,
      hasWildcard,
      hasContestedPlace,
      policyVersion,
      isConfirmed: true,
      wildcardWinner,
      qualifiedTeams: identity.qualifiedTeams,
      officialOrder: identity.officialOrder,
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
    wildcardWinner: null,
    qualifiedTeams: null,
    officialOrder: null,
  };
}

/**
 * Total confirmed/provisional qualifying rows (guaranteed plus a confirmed
 * wildcard winner when the persisted tournament resolves one for this group).
 */
export function qualifiedRowCount(cutoff: LiveQualificationCutoff | null): number {
  if (!cutoff || cutoff.guaranteed < 0) {
    return 0;
  }
  if (cutoff.isConfirmed && cutoff.wildcardWinner != null) {
    return cutoff.guaranteed + 1;
  }
  return cutoff.guaranteed;
}

/** True when the row is a confirmed wildcard-qualified place (rank guaranteed+1). */
export function isWildcardQualifiedIndex(index: number, cutoff: LiveQualificationCutoff | null): boolean {
  if (!cutoff || !cutoff.isConfirmed || cutoff.wildcardWinner == null) {
    return false;
  }
  return index === cutoff.guaranteed;
}

/** True for a provisional/confirmed qualifying position (0-based index). */
export function isQualifyingIndex(index: number, cutoff: LiveQualificationCutoff | null): boolean {
  if (!cutoff) {
    return false;
  }
  const total = qualifiedRowCount(cutoff);
  if (total <= 0) {
    return false;
  }
  return index < total;
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
 * Confirmed-table qualifying check by persisted team identity (MSS-080).
 * While provisional the cutoff has no identity set and this falls back to
 * rank-index highlighting; once confirmed with a persisted qualified set it
 * highlights the authoritative teams even when provisional alphabetical order
 * disagrees with the official backend tie-break at the boundary.
 */
export function isQualifyingTeam(
  teamName: string,
  index: number,
  cutoff: LiveQualificationCutoff | null,
): boolean {
  if (!cutoff) {
    return false;
  }
  if (cutoff.isConfirmed && cutoff.qualifiedTeams != null && cutoff.qualifiedTeams.length > 0) {
    return cutoff.qualifiedTeams.includes(teamName);
  }
  return isQualifyingIndex(index, cutoff);
}

/**
 * Confirmed wildcard check by persisted winner identity (MSS-080). Index-only
 * matching would label the provisional rank guaranteed+1 even when a tied
 * boundary puts a different team there alphabetically; the persisted
 * wildcardWinner names the actual qualifier.
 */
export function isWildcardQualifiedTeam(
  teamName: string,
  index: number,
  cutoff: LiveQualificationCutoff | null,
): boolean {
  if (!cutoff || !cutoff.isConfirmed || cutoff.wildcardWinner == null) {
    return false;
  }
  if (typeof teamName === 'string' && teamName.length > 0) {
    return teamName === cutoff.wildcardWinner;
  }
  return isWildcardQualifiedIndex(index, cutoff);
}

/**
 * Official order for a confirmed final table (MSS-080). Returns the input
 * rows reordered to the persisted backend teamRank order when that order is
 * available and covers exactly the displayed teams; otherwise returns the
 * input order unchanged (fail closed to the provisional projection).
 */
export function orderRevealedByOfficialOrder<T extends { teamName: string }>(
  rows: readonly T[],
  cutoff: LiveQualificationCutoff | null,
): T[] {
  if (!cutoff || !cutoff.isConfirmed || cutoff.officialOrder == null) {
    return [...rows];
  }
  if (cutoff.officialOrder.length !== rows.length) {
    return [...rows];
  }
  const names = new Set(rows.map((row) => row.teamName));
  for (const name of cutoff.officialOrder) {
    if (!names.has(name)) {
      return [...rows];
    }
  }
  const rank = new Map(cutoff.officialOrder.map((name, position) => [name, position] as const));
  return [...rows].sort((a, b) => (rank.get(a.teamName) ?? 0) - (rank.get(b.teamName) ?? 0));
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
  // Confirmed wildcard winner: rank guaranteed+1 qualified (never contested).
  if (cutoff.isConfirmed && cutoff.wildcardWinner != null) {
    const total = cutoff.guaranteed + 1;
    if (cutoff.guaranteed <= 0) {
      return `Top 1 qualified (wildcard ${cutoff.wildcardWinner})`;
    }
    return `Top ${total} qualified (Top ${cutoff.guaranteed} + wildcard ${cutoff.wildcardWinner})`;
  }
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
  // Settled groups never imply remaining wildcard places; only provisional
  // standings keep the cross-group "plus wildcards" note.
  if (!cutoff.isConfirmed && cutoff.hasWildcard && cutoff.wildcardCount > 0) {
    return `${base} · plus ${cutoff.wildcardCount} ${plural(cutoff.wildcardCount, 'wildcard', 'wildcards')} across groups`;
  }
  return base;
}

/** Cut-line label rendered between the last qualifying and first nonqualifying row. */
export function liveCutLineLabel(cutoff: LiveQualificationCutoff | null): string | null {
  if (!cutoff) {
    return null;
  }
  const total = qualifiedRowCount(cutoff);
  if (total <= 0) {
    return null;
  }
  return cutoff.isConfirmed
    ? `Qualification cut — top ${total} qualified`
    : `Qualification cut — top ${total} provisionally qualify`;
}
