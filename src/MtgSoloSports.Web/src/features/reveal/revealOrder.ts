import {
  REVEAL_SPEED_INTERVAL_MS,
  type RevealPlacement,
  type RevealSpeed,
} from './types.ts';

/**
 * Eurovision-style reveal order: lowest finishing position first so the
 * round winner is revealed last and cumulative standings visibly move.
 * Pure presentation ordering over the immutable backend payload; the input
 * array is never mutated.
 */
export function buildRevealOrder(placements: readonly RevealPlacement[]): RevealPlacement[] {
  return [...placements].sort((a, b) => b.position - a.position);
}

export interface ProgressiveStandingRow {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  /** Finishing position in this round (1 = winner). */
  position: number;
  /** True once this card has been revealed in the current progression. */
  isRevealed: boolean;
  /** Base points for the finishing position before the bonus, once revealed, otherwise 0 (integer thousandths). */
  baseThousandths: number;
  /** +awarded final points once revealed, otherwise 0 (integer thousandths). */
  awardedThousandths: number;
  /** Displayed cumulative stage score at this progression step (thousandths). */
  displayedScoreThousandths: number;
  /** Stage rank before the round (1-based). */
  startRank: number;
  /** Current displayed rank at this progression step (1-based). */
  currentRank: number;
  /** startRank - currentRank: positive means the card has climbed. */
  rankDelta: number;
  /** Final persisted rank after the round (1-based). */
  finalRank: number;
}

/**
 * Compute the intermediate cumulative stage standings after revealing the
 * first `revealedCount` cards of `revealOrder`.
 *
 * Revealed athletes show their persisted `cumulativeAfterThousandths`;
 * unrevealed athletes still show `cumulativeBeforeThousandths`. Rows are
 * sorted by displayed score descending, then start rank ascending, then
 * athlete id ascending so the ordering is deterministic and display-only.
 * All sporting values pass through as integers; no floating-point math.
 */
export function computeProgressiveStandings(
  placementsByAthlete: ReadonlyMap<number, RevealPlacement>,
  revealOrder: readonly RevealPlacement[],
  revealedCount: number,
): ProgressiveStandingRow[] {
  const clamped = clampRevealed(revealedCount, revealOrder.length);
  const revealedIds = new Set<number>();
  for (let index = 0; index < clamped; index += 1) {
    const entry = revealOrder[index];
    if (entry) {
      revealedIds.add(entry.athleteId);
    }
  }

  const rows: ProgressiveStandingRow[] = [];
  for (const placement of placementsByAthlete.values()) {
    const isRevealed = revealedIds.has(placement.athleteId);
    rows.push({
      athleteId: placement.athleteId,
      name: placement.name,
      imageUrl: placement.imageUrl,
      position: placement.position,
      isRevealed,
      baseThousandths: isRevealed ? placement.baseThousandths : 0,
      awardedThousandths: isRevealed ? placement.finalThousandths : 0,
      displayedScoreThousandths: isRevealed
        ? placement.cumulativeAfterThousandths
        : placement.cumulativeBeforeThousandths,
      startRank: placement.rankBefore,
      currentRank: 0,
      rankDelta: 0,
      finalRank: placement.rankAfter,
    });
  }

  rows.sort((a, b) => {
    if (b.displayedScoreThousandths !== a.displayedScoreThousandths) {
      return b.displayedScoreThousandths - a.displayedScoreThousandths;
    }
    if (a.startRank !== b.startRank) {
      return a.startRank - b.startRank;
    }
    return a.athleteId - b.athleteId;
  });

  for (let index = 0; index < rows.length; index += 1) {
    const row = rows[index]!;
    row.currentRank = index + 1;
    row.rankDelta = row.startRank - row.currentRank;
  }

  return rows;
}

/** Build the athlete lookup used by `computeProgressiveStandings`. */
export function indexPlacementsByAthlete(
  placements: readonly RevealPlacement[],
): Map<number, RevealPlacement> {
  const byAthlete = new Map<number, RevealPlacement>();
  for (const placement of placements) {
    byAthlete.set(placement.athleteId, placement);
  }
  return byAthlete;
}

export function clampRevealed(value: number, total: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }
  if (total <= 0) {
    return 0;
  }
  if (value <= 0) {
    return 0;
  }
  if (value >= total) {
    return total;
  }
  return Math.floor(value);
}

export function speedToIntervalMs(speed: RevealSpeed): number {
  return REVEAL_SPEED_INTERVAL_MS[speed];
}

export function isValidRevealSpeed(value: string | null): value is RevealSpeed {
  return value === 'slow' || value === 'normal' || value === 'fast' || value === 'turbo';
}

export function nextRevealed(current: number, total: number): number {
  return clampRevealed(current + 1, total);
}
