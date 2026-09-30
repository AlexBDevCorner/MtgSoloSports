import type { CatalogStats } from './catalogApi';

export type CatalogTone = 'ok' | 'warn' | 'dim';

export interface CatalogStatus {
  tone: CatalogTone;
  label: string;
}

/**
 * One-line catalog summary for the navigation rail. While a refresh is in
 * flight the last known stats keep driving the label so it never flashes.
 */
export function catalogStatus(stats: CatalogStats | null, loading: boolean): CatalogStatus {
  if (loading && !stats) {
    return { tone: 'dim', label: 'Checking catalog…' };
  }
  if (!stats || stats.totalAthletes === 0) {
    return { tone: 'warn', label: 'No card catalog' };
  }
  if (!stats.isSufficientForSave) {
    return { tone: 'warn', label: `Catalog ${stats.totalAthletes} · incomplete` };
  }
  return { tone: 'ok', label: `Catalog ${stats.totalAthletes}` };
}
