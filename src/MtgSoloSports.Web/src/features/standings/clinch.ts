/**
 * MSS-142 mathematically proven postseason guarantees.
 * Pure integer-only classifier: per-athlete final-score intervals from
 * authoritative completed-stage championship totals plus the save-owned
 * scoring bounds, mapped to the single MSS-141 zone table. No simulation,
 * no RNG, no probability, no mutation. A tied-point boundary is always
 * treated as unresolved (pessimistic), so false negatives are possible but
 * false guarantees are not.
 *
 * Bounds come from the backend placements contract
 * (stagesPerSeason/leagueSize/max/min from the save-owned rules snapshot),
 * never hardcoded 77/1/32. Zones come from zones.ts, never a second set of
 * cutoffs. Callers must pass totals and bounds for the same requested
 * save/season/league; this module keeps no cross-league or cross-season
 * state and never reads visual sort order or matrix ranks.
 */

import {
  resolvePostseasonZone,
  type PostseasonZone,
  type ZoneContext,
} from './zones';

export interface ClinchPoints {
  athleteId: number;
  totalChampionshipPointsThousandths: number;
}

export interface ClinchScoring {
  completedStages: number;
  stagesPerSeason: number | null | undefined;
  leagueSize: number | null | undefined;
  maxStagePointsThousandths: number | null | undefined;
  minStagePointsThousandths: number | null | undefined;
  isSeasonComplete: boolean;
}

export interface ClinchResult {
  athleteId: number;
  /** Pessimistic best possible final rank (1-based, ties resolved adversarially). */
  bestRank: number;
  /** Pessimistic worst possible final rank (1-based, ties resolved adversarially). */
  worstRank: number;
  /** True only when every remaining possibility is exactly rank 1. */
  isChampionLocked: boolean;
  /**
   * The locked MSS-141 zone when best and worst ranks share one exact,
   * non-vacuous, non-neutral band. Null when the possible range spans bands,
   * the season is complete/has no stages left, or inputs cannot be trusted.
   * A champion lock also carries its rank-1 zone here for display context,
   * but callers should render the champion marker without redundant movement.
   */
  guaranteedZone: PostseasonZone | null;
}

function isSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value);
}

/**
 * Computes pessimistic rank intervals and exact-band guarantees.
 * Returns an empty map when global inputs cannot be trusted (fail safe with
 * no guarantee rather than inventing one). Per-athlete invalid points yield
 * no guarantee for that athlete only. Never throws on sporting data.
 */
export function computeClinchMap(
  totals: ReadonlyArray<ClinchPoints>,
  scoring: ClinchScoring,
  ctx: ZoneContext,
): Map<number, ClinchResult> {
  const empty = new Map<number, ClinchResult>();
  if (!Array.isArray(totals) || totals.length === 0) {
    return empty;
  }
  if (!ctx || !isSafeInteger(ctx.seasonNumber) || ctx.seasonNumber < 1) {
    return empty;
  }
  if (typeof ctx.leagueKind !== 'string' || ctx.leagueKind.length === 0) {
    return empty;
  }
  if (scoring.isSeasonComplete) {
    return empty;
  }
  const stagesPerSeason = scoring.stagesPerSeason;
  const leagueSize = scoring.leagueSize;
  const maxStage = scoring.maxStagePointsThousandths;
  const minStage = scoring.minStagePointsThousandths;
  const completed = scoring.completedStages;
  if (
    !isSafeInteger(stagesPerSeason) ||
    stagesPerSeason < 1 ||
    !isSafeInteger(leagueSize) ||
    leagueSize < 1 ||
    !isSafeInteger(maxStage) ||
    !isSafeInteger(minStage) ||
    !isSafeInteger(completed)
  ) {
    return empty;
  }
  if (maxStage < minStage || maxStage <= 0 || minStage < 0) {
    return empty;
  }
  if (completed < 0 || completed >= stagesPerSeason) {
    return empty;
  }
  if (totals.length !== leagueSize) {
    return empty;
  }
  const remaining = stagesPerSeason - completed;
  if (remaining <= 0) {
    return empty;
  }

  const seen = new Set<number>();
  for (const row of totals) {
    if (!isSafeInteger(row.athleteId) || row.athleteId <= 0 || seen.has(row.athleteId)) {
      return empty;
    }
    seen.add(row.athleteId);
    if (!isSafeInteger(row.totalChampionshipPointsThousandths) || row.totalChampionshipPointsThousandths < 0) {
      return empty;
    }
  }

  // Integer-only intervals: every remaining unawarded stage pays between
  // minStage and maxStage thousandths, so the final total stays in
  // [points + remaining*min, points + remaining*max]. No division, no float.
  const floors = new Map<number, number>();
  const ceilings = new Map<number, number>();
  for (const row of totals) {
    floors.set(row.athleteId, row.totalChampionshipPointsThousandths + remaining * minStage);
    ceilings.set(row.athleteId, row.totalChampionshipPointsThousandths + remaining * maxStage);
  }

  // Whole-table vacuous check: when rank 1 and the last rank share one zone,
  // that zone covers the entire field (inaugural F2/F3 retention) and locking
  // it would be noise. Precompute once per season/league context.
  const firstZoneId = resolvePostseasonZone(1, ctx).id;
  const lastZoneId = resolvePostseasonZone(leagueSize, ctx).id;
  const wholeTableOneZone = firstZoneId !== 'neutral' && firstZoneId === lastZoneId;

  const result = new Map<number, ClinchResult>();
  for (const row of totals) {
    const ceiling = ceilings.get(row.athleteId) ?? 0;
    const floor = floors.get(row.athleteId) ?? 0;
    // Strict comparisons with pessimistic ties: another athlete is definitely
    // ahead only when even its floor beats our ceiling; it could still pass
    // us whenever its ceiling reaches our floor (ties included).
    let definitelyAhead = 0;
    let couldBeAhead = 0;
    for (const other of totals) {
      if (other.athleteId === row.athleteId) {
        continue;
      }
      const otherFloor = floors.get(other.athleteId) ?? 0;
      const otherCeiling = ceilings.get(other.athleteId) ?? 0;
      if (otherFloor > ceiling) {
        definitelyAhead += 1;
      }
      if (otherCeiling >= floor) {
        couldBeAhead += 1;
      }
    }
    const bestRank = definitelyAhead + 1;
    const worstRank = couldBeAhead + 1;
    if (bestRank < 1 || worstRank > leagueSize || bestRank > worstRank) {
      result.set(row.athleteId, {
        athleteId: row.athleteId,
        bestRank: -1,
        worstRank: -1,
        isChampionLocked: false,
        guaranteedZone: null,
      });
      continue;
    }
    const isChampionLocked = bestRank === 1 && worstRank === 1;
    if (isChampionLocked) {
      result.set(row.athleteId, {
        athleteId: row.athleteId,
        bestRank,
        worstRank,
        isChampionLocked: true,
        guaranteedZone: resolvePostseasonZone(1, ctx),
      });
      continue;
    }
    const bestZone = resolvePostseasonZone(bestRank, ctx);
    const worstZone = resolvePostseasonZone(worstRank, ctx);
    if (bestZone.id === 'neutral' || bestZone.id !== worstZone.id) {
      result.set(row.athleteId, {
        athleteId: row.athleteId,
        bestRank,
        worstRank,
        isChampionLocked: false,
        guaranteedZone: null,
      });
      continue;
    }
    if (wholeTableOneZone && bestZone.id === firstZoneId) {
      result.set(row.athleteId, {
        athleteId: row.athleteId,
        bestRank,
        worstRank,
        isChampionLocked: false,
        guaranteedZone: null,
      });
      continue;
    }
    result.set(row.athleteId, {
      athleteId: row.athleteId,
      bestRank,
      worstRank,
      isChampionLocked: false,
      guaranteedZone: bestZone,
    });
  }
  return result;
}

/**
 * Compact locked text for the season-table badge (distinct from the
 * provisional zone badge). Null when nothing is mathematically locked.
 * Champion takes precedence so a rank-1 lock never renders redundant
 * promotion/safety noise alongside it.
 */
export function clinchBadgeText(entry: ClinchResult | null | undefined): string | null {
  if (!entry) {
    return null;
  }
  if (entry.isChampionLocked) {
    return '✓ Clinched champion';
  }
  if (!entry.guaranteedZone || entry.guaranteedZone.id === 'neutral') {
    return null;
  }
  switch (entry.guaranteedZone.movement) {
    case 'promoted':
      return `✓ Clinched: ${entry.guaranteedZone.shortLabel}`;
    case 'qualifier-up':
      return `✓ Clinched: ${entry.guaranteedZone.shortLabel}`;
    case 'qualifier-hold':
      return `✓ Clinched: ${entry.guaranteedZone.shortLabel}`;
    case 'safe':
      return `✓ Safe: ${entry.guaranteedZone.shortLabel}`;
    case 'relegated':
      return `✓ Clinched: ${entry.guaranteedZone.shortLabel}`;
    case 'inaugural':
      return `✓ Clinched: ${entry.guaranteedZone.shortLabel}`;
    default:
      return null;
  }
}

/**
 * Accessible tooltip explaining exactly what was proven, including the
 * pessimistic rank interval and the remaining official stages. Null when
 * nothing is locked. Never mentions probabilities or future simulation.
 */
export function clinchExplanation(
  entry: ClinchResult | null | undefined,
  remainingStages: number,
): string | null {
  if (!entry) {
    return null;
  }
  if (remainingStages <= 0) {
    return null;
  }
  if (entry.isChampionLocked) {
    const zone = entry.guaranteedZone;
    const where = zone && zone.id !== 'neutral' ? ` (${zone.label})` : '';
    return (
      `Mathematically guaranteed champion: every possible final total leaves this card ` +
      `rank 1 with ${remainingStages} official stage(s) left, ` +
      `even if every tie breaks against it${where}. Provisional qualifier or movement ` +
      `labels for other cards are unchanged.`
    );
  }
  const zone = entry.guaranteedZone;
  if (!zone || zone.id === 'neutral') {
    return null;
  }
  const range = entry.bestRank === entry.worstRank
    ? `rank ${entry.bestRank}`
    : `ranks ${entry.bestRank}–${entry.worstRank}`;
  return (
    `Mathematically guaranteed ${zone.label}: every possible final total with ` +
    `${remainingStages} official stage(s) left stays in ${range} (${zone.band}), ` +
    `even if every tie breaks against it. Not a prediction; an incomplete stage ` +
    `never contributes until all its rounds persist.`
  );
}
