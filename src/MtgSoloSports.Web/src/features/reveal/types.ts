/**
 * Shared presentation model for the Eurovision-style round reveal.
 *
 * Both the live completed round (`StageRoundPlacement` in liveApi) and the
 * historical replay (`HistoryRoundPlacement` in historyApi) map onto this
 * shape. Every sporting value is a fixed-point integer in thousandths copied
 * verbatim from the persisted backend payload; this slice never recomputes
 * sporting math and never touches RNG.
 */
export interface RevealPlacement {
  athleteId: number;
  name: string;
  /** Finishing position in the round, 1-based (1 = winner). */
  position: number;
  /** Base points for the finishing position, thousandths. */
  baseThousandths: number;
  /** Stage-start active bonus applied to this round, thousandths. */
  activeBonusThousandths: number;
  /** Awarded final points for this round, thousandths. */
  finalThousandths: number;
  /** Cumulative stage score before this round, thousandths. */
  cumulativeBeforeThousandths: number;
  /** Cumulative stage score after this round, thousandths. */
  cumulativeAfterThousandths: number;
  rankBefore: number;
  rankAfter: number;
  rankMovement: number;
  imageUrl: string | null;
  setCode: string | null;
  typeLine: string;
}

export type RevealMode = 'instant' | 'animated';

export type RevealSpeed = 'slow' | 'normal' | 'fast' | 'turbo';

/** Interval between revealed cards per speed, in milliseconds. */
export const REVEAL_SPEED_INTERVAL_MS: Record<RevealSpeed, number> = {
  slow: 800,
  normal: 350,
  fast: 120,
  turbo: 40,
};

export const REVEAL_SPEEDS: readonly RevealSpeed[] = ['slow', 'normal', 'fast', 'turbo'];
