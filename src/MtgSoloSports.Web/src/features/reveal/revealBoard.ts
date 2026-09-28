import type { ProgressiveStandingRow } from './revealOrder.ts';

/**
 * Pure presentation helpers for the 32-card MTG reveal board.
 *
 * Display-only: every sporting value passes through as an integer in
 * thousandths and is formatted by the caller. No RNG, no sporting math,
 * no DOM access. The board itself is ordered by the progressive standings
 * (rank 1 onward); these helpers only describe how a tile or the grid
 * should present one already-computed row.
 */

export function boardColumnCount(viewportWidthPx: number): number {
  if (!Number.isFinite(viewportWidthPx) || viewportWidthPx <= 0) {
    return 2;
  }
  if (viewportWidthPx >= 1600) {
    return 8;
  }
  if (viewportWidthPx >= 1200) {
    return 6;
  }
  if (viewportWidthPx >= 900) {
    return 4;
  }
  if (viewportWidthPx >= 640) {
    return 3;
  }
  return 2;
}

export type TileMovementKind = 'up' | 'down' | 'flat';

export function tileMovementKind(rankDelta: number): TileMovementKind {
  if (rankDelta > 0) {
    return 'up';
  }
  if (rankDelta < 0) {
    return 'down';
  }
  return 'flat';
}

/**
 * Glyph accompanying the signed movement text so direction never depends
 * on color alone (▲ up, ▼ down, • unchanged).
 */
export function tileMovementGlyph(rankDelta: number): string {
  const kind = tileMovementKind(rankDelta);
  if (kind === 'up') {
    return '▲';
  }
  if (kind === 'down') {
    return '▼';
  }
  return '•';
}

export interface TileDisplay {
  /** Stable React key / DOM id suffix. */
  athleteId: number;
  /** Progressive rank label (already 1-based). */
  rankLabel: string;
  /** True once the card has been revealed in the current progression. */
  isRevealed: boolean;
  /** Whether artwork may be shown (revealed rows with an image URL only). */
  showArtwork: boolean;
  /** Accessible name for the tile, never exposing pending awards. */
  accessibleName: string;
}

export function describeTile(row: ProgressiveStandingRow): TileDisplay {
  const isRevealed = row.isRevealed === true;
  return {
    athleteId: row.athleteId,
    rankLabel: `#${row.currentRank}`,
    isRevealed,
    showArtwork: isRevealed && typeof row.imageUrl === 'string' && row.imageUrl.length > 0,
    accessibleName: isRevealed
      ? `Rank ${row.currentRank}, ${row.name}`
      : `Rank ${row.currentRank}, face-down card, not yet revealed`,
  };
}
