/**
 * Stable cross-season league identity for URLs and selection (MSS-085).
 * Mirrors the backend `LeagueKey`: the per-season numeric league id is not
 * stable across seasons, so profiles group by the sporting triple
 * (kind, feeder division, sporting color). The Superleague collapses to
 * `superleague`; every distinct feeder division/color stays separate; legacy
 * v1 feeder rows (division 0/unknown) keep their own `feeder-{color}` key so
 * they never merge with tiered `feeder-1-{color}` rows. Pure and DOM-free.
 */

export const SUPERLEAGUE_KEY = 'superleague';

const COLOR_NAMES = [
  'white',
  'blue',
  'black',
  'red',
  'green',
  'multicolor',
  'hybrid',
  'colorless',
] as const;

export type SportingColorName = (typeof COLOR_NAMES)[number];

export function colorSlug(sportingColor: number | null | undefined): string | null {
  if (typeof sportingColor !== 'number' || !Number.isInteger(sportingColor)) {
    return null;
  }
  if (sportingColor >= 0 && sportingColor < COLOR_NAMES.length) {
    return COLOR_NAMES[sportingColor]!;
  }
  return `color${sportingColor}`;
}

export function colorIndex(slug: string | null | undefined): number | null {
  if (!slug) {
    return null;
  }
  const normalized = slug.trim().toLowerCase();
  const index = COLOR_NAMES.indexOf(normalized as SportingColorName);
  return index >= 0 ? index : null;
}

/**
 * Builds the stable key for one league row from backend identity fields
 * (`kind`, `feederDivision`, `sportingColor`). Returns null when the triple
 * is missing so callers render no link instead of a fabricated URL.
 */
export function leagueKeyFor(league: {
  kind?: string | null;
  feederDivision?: number | null;
  sportingColor?: number | null;
}): string | null {
  const kind = (league.kind ?? '').trim().toLowerCase();
  if (kind === 'superleague') {
    return SUPERLEAGUE_KEY;
  }
  if (kind !== 'feeder') {
    return null;
  }
  const slug = colorSlug(league.sportingColor);
  if (!slug) {
    return null;
  }
  const division = league.feederDivision;
  if (division === 1) {
    return `feeder-1-${slug}`;
  }
  if (division === 2) {
    return `feeder-2-${slug}`;
  }
  if (division === 3) {
    return `feeder-3-${slug}`;
  }
  if (division === 0 || division === null || division === undefined) {
    return `feeder-${slug}`;
  }
  return `feeder-d${division}-${slug}`;
}

export interface ParsedLeagueKey {
  kind: 'Superleague' | 'Feeder';
  feederDivision: number | null;
  sportingColor: number | null;
}

/** Parses a stable league key back to its sporting triple; null when unknown. */
export function parseLeagueKey(raw: string | null | undefined): ParsedLeagueKey | null {
  if (typeof raw !== 'string') {
    return null;
  }
  const normalized = raw.trim().toLowerCase();
  if (normalized.length === 0) {
    return null;
  }
  if (normalized === SUPERLEAGUE_KEY) {
    return { kind: 'Superleague', feederDivision: 0, sportingColor: null };
  }
  if (!normalized.startsWith('feeder-')) {
    return null;
  }
  const rest = normalized.slice('feeder-'.length);
  const dash = rest.indexOf('-');
  if (dash > 0) {
    const divisionPart = rest.slice(0, dash);
    const colorPart = rest.slice(dash + 1);
    const division = parseDivisionPart(divisionPart);
    const color = colorIndex(colorPart);
    if (division === null || color === null) {
      return null;
    }
    return { kind: 'Feeder', feederDivision: division, sportingColor: color };
  }
  const legacyColor = colorIndex(rest);
  if (legacyColor === null) {
    return null;
  }
  return { kind: 'Feeder', feederDivision: 0, sportingColor: legacyColor };
}

function parseDivisionPart(part: string): number | null {
  if (part === '1') {
    return 1;
  }
  if (part === '2') {
    return 2;
  }
  if (part === '3') {
    return 3;
  }
  if (part.startsWith('d')) {
    const parsed = Number.parseInt(part.slice(1), 10);
    if (Number.isInteger(parsed) && parsed >= 0 && String(parsed) === part.slice(1)) {
      return parsed;
    }
  }
  return null;
}

/** True for stable profile keys; numeric segments stay Standings aliases. */
export function isProfileKeySegment(segment: string | null | undefined): boolean {
  if (typeof segment !== 'string' || segment.trim().length === 0) {
    return false;
  }
  if (/^\d+$/.test(segment.trim())) {
    return false;
  }
  return parseLeagueKey(segment) !== null;
}

/**
 * Display fallback derived from the key alone (used for headings and aria
 * labels while the profile loads). Loaded profiles prefer backend display
 * fields; this never replaces them.
 */
export function leagueKeyLabel(key: string | null | undefined): string {
  const parsed = parseLeagueKey(key);
  if (!parsed) {
    return 'League';
  }
  if (parsed.kind === 'Superleague') {
    return 'Superleague';
  }
  const color = parsed.sportingColor !== null ? titleCase(COLOR_NAMES[parsed.sportingColor]!) : null;
  if (parsed.feederDivision === 1) {
    return color ? `Feeder 1 · ${color}` : 'Feeder 1';
  }
  if (parsed.feederDivision === 2) {
    return color ? `Feeder 2 · ${color}` : 'Feeder 2';
  }
  if (parsed.feederDivision === 3) {
    return color ? `Feeder 3 · ${color}` : 'Feeder 3';
  }
  return color ? `Feeder · ${color}` : 'Feeder';
}

function titleCase(value: string): string {
  return value.length === 0 ? value : value.charAt(0).toUpperCase() + value.slice(1);
}
