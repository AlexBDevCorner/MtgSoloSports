import type { ColorCupIndividualProgress, ColorCupIndividualResult } from './colorCupApi';

/** Display-only helpers for the phased 32-to-16 Color Cup. No sporting math. */

export function isPhasedResult(result: ColorCupIndividualResult): boolean {
  if (typeof result.isPhased === 'boolean') {
    return result.isPhased;
  }
  if (typeof result.formatVersion === 'number') {
    return result.formatVersion === 1;
  }
  return result.rounds === 32;
}

export function isPhasedProgress(progress: ColorCupIndividualProgress): boolean {
  return progress.isPhased === true || progress.formatVersion === 1;
}

export function totalRoundsFor(result: ColorCupIndividualResult): number {
  return result.rounds === 32 || isPhasedResult(result) ? 32 : 16;
}

export function advancingLabel(status: string | null | undefined): string {
  if (status === 'Qualified') {
    return 'Qualified';
  }
  if (status === 'Eliminated') {
    return 'Eliminated';
  }
  if (status === 'ProvisionalQualified') {
    return 'Provisional top 16';
  }
  if (status === 'ProvisionalEliminated') {
    return 'Provisional bottom 16';
  }
  return '—';
}

export function cutoffSummary(
  cutoffRank: number | null | undefined,
  tieDrawConsumed: boolean | null | undefined,
  isProvisional: boolean,
): string {
  const rank = cutoffRank ?? 16;
  const tie = tieDrawConsumed === true ? 'yes (seeded draw)' : tieDrawConsumed === false ? 'no' : '—';
  const scope = isProvisional ? 'provisional top' : 'top';
  return `${scope} ${rank} qualify · tie draw: ${tie}`;
}
