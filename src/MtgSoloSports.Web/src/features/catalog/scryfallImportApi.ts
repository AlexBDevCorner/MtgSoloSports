import { ApiError } from '../../shared/api/http.ts';

export const SCRYFALL_BULK_DOCS_URL = 'https://scryfall.com/docs/api/bulk-data';
export const REQUIRED_PER_COLOR = 256;

export const COLOR_ORDER = [
  'White',
  'Blue',
  'Black',
  'Red',
  'Green',
  'Multicolor',
  'Hybrid',
  'Colorless',
] as const;

export type SportingColorName = (typeof COLOR_ORDER)[number];

export interface ScryfallImportResult {
  totalPrintings: number;
  eligiblePrintings: number;
  uniqueAthletes: number;
  countsBySportingColor: Record<string, number>;
  skippedTokens: number;
  skippedNonCreature: number;
  skippedAmbiguousColor?: number;
  isSufficientForSave: boolean;
  sourceType: string;
  sourceName: string | null;
  sourceUpdatedAt: string | null;
  sourceDownloadUri: string;
}

export interface ScryfallQuotaError {
  message: string;
  countsBySportingColor: Record<string, number>;
  insufficient: string[];
  sourceName: string | null;
  sourceUpdatedAt: string | null;
}

export async function importFromScryfall(signal?: AbortSignal): Promise<ScryfallImportResult> {
  const response = await fetch('/api/catalog/import-from-scryfall', {
    method: 'POST',
    signal,
  });
  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new ApiError(response.status, body || response.statusText);
  }
  return (await response.json()) as ScryfallImportResult;
}

export function countForColor(counts: Record<string, number>, color: string): number {
  const key = color.toLowerCase();
  return (
    counts[color] ??
    counts[key] ??
    counts[color.toUpperCase()] ??
    counts[capitalize(key)] ??
    0
  );
}

function capitalize(value: string): string {
  return value.length === 0 ? value : value[0]!.toUpperCase() + value.slice(1);
}

export function findShortfalls(counts: Record<string, number>, required = REQUIRED_PER_COLOR): SportingColorName[] {
  return COLOR_ORDER.filter((color) => countForColor(counts, color) < required);
}

export function needsRefreshConfirmation(totalAthletes: number): boolean {
  return totalAthletes > 0;
}

export function formatSourceLabel(
  sourceName: string | null | undefined,
  sourceUpdatedAt: string | null | undefined,
): string | null {
  const name = (sourceName ?? '').trim();
  const updated = (sourceUpdatedAt ?? '').trim();
  if (name.length > 0 && updated.length > 0) {
    return `Source: ${name} (updated ${updated})`;
  }
  if (name.length > 0) {
    return `Source: ${name}`;
  }
  // Never fabricate freshness when Scryfall did not advertise it.
  return null;
}

export function tryParseQuotaError(error: unknown): ScryfallQuotaError | null {
  if (!(error instanceof ApiError)) {
    return null;
  }
  if (error.status !== 422) {
    return null;
  }
  try {
    const parsed = JSON.parse(error.body) as {
      error?: unknown;
      countsBySportingColor?: unknown;
      insufficient?: unknown;
      sourceName?: unknown;
      sourceUpdatedAt?: unknown;
    };
    if (typeof parsed.error !== 'string' || parsed.error.length === 0) {
      return null;
    }
    if (typeof parsed.countsBySportingColor !== 'object' || parsed.countsBySportingColor === null) {
      return null;
    }
    const insufficient = Array.isArray(parsed.insufficient)
      ? parsed.insufficient.filter((entry): entry is string => typeof entry === 'string')
      : [];
    return {
      message: parsed.error,
      countsBySportingColor: parsed.countsBySportingColor as Record<string, number>,
      insufficient,
      sourceName: typeof parsed.sourceName === 'string' ? parsed.sourceName : null,
      sourceUpdatedAt: typeof parsed.sourceUpdatedAt === 'string' ? parsed.sourceUpdatedAt : null,
    };
  } catch {
    return null;
  }
}
