import type { AthleteSearchRequest } from './athleteSearchApi.ts';

export type AthleteSearchSort =
  | 'name'
  | 'nonPool'
  | 'honours'
  | 'titles'
  | 'bestFinish'
  | 'currentLeague'
  | 'superSeasons';

export type AthleteSearchDir = 'asc' | 'desc';

export type AthleteHasTitle = '' | 'only' | 'none';

export type AthleteHighestLeague = '' | 'superleague' | 'feeder' | 'none';

export type AthleteCupStatus = '' | 'participant' | 'podium' | 'title' | 'none';

/**
 * Page-level athlete search state. Mirrors the backend query with
 * UI-friendly defaults; `q` is free text, multi-value lists use OR
 * semantics, everything else composes with AND.
 */
export interface AthleteSearchParams {
  q: string;
  colours: number[];
  types: string[];
  current: string[];
  minNonPool: number | null;
  maxNonPool: number | null;
  minHonours: number | null;
  maxHonours: number | null;
  minTitles: number | null;
  hasTitle: AthleteHasTitle;
  highest: AthleteHighestLeague;
  bestFinishMax: number | null;
  minSuperSeasons: number | null;
  maxSuperSeasons: number | null;
  cup: AthleteCupStatus;
  sort: AthleteSearchSort;
  dir: AthleteSearchDir;
  take: number;
  skip: number;
}

export const DEFAULT_ATHLETE_SEARCH: AthleteSearchParams = {
  q: '',
  colours: [],
  types: [],
  current: [],
  minNonPool: null,
  maxNonPool: null,
  minHonours: null,
  maxHonours: null,
  minTitles: null,
  hasTitle: '',
  highest: '',
  bestFinishMax: null,
  minSuperSeasons: null,
  maxSuperSeasons: null,
  cup: '',
  sort: 'name',
  dir: 'asc',
  take: 100,
  skip: 0,
};

const SORTS: AthleteSearchSort[] = [
  'name',
  'nonPool',
  'honours',
  'titles',
  'bestFinish',
  'currentLeague',
  'superSeasons',
];

function parseIntList(raw: string | null, min: number, max: number): number[] {
  if (!raw) {
    return [];
  }
  const values: number[] = [];
  for (const part of raw.split(',')) {
    const parsed = Number.parseInt(part.trim(), 10);
    if (Number.isInteger(parsed) && parsed >= min && parsed <= max && !values.includes(parsed)) {
      values.push(parsed);
    }
  }
  return values;
}

function parseStringList(raw: string | null): string[] {
  if (!raw) {
    return [];
  }
  const values: string[] = [];
  for (const part of raw.split(',')) {
    const trimmed = part.trim();
    if (trimmed && !values.some((entry) => entry.toLowerCase() === trimmed.toLowerCase())) {
      values.push(trimmed);
    }
  }
  return values;
}

function parseOptionalInt(raw: string | null, min: number, max: number): number | null {
  if (raw === null || raw.trim() === '') {
    return null;
  }
  const parsed = Number.parseInt(raw.trim(), 10);
  if (!Number.isInteger(parsed) || parsed < min || parsed > max) {
    return null;
  }
  if (String(parsed) !== raw.trim()) {
    return null;
  }
  return parsed;
}

/**
 * Parses the `?q=&colours=&...` search portion of an athletes URL into page
 * state. Pure and DOM-free. Unknown or out-of-range values fall back to
 * defaults instead of crashing, so hand-edited URLs stay shareable.
 */
export function parseAthleteSearchParams(search: string): AthleteSearchParams {
  const query = search.startsWith('?') ? search.slice(1) : search;
  const params = new URLSearchParams(query);
  const rawSort = (params.get('sort') ?? '').trim();
  const sort: AthleteSearchSort = SORTS.includes(rawSort as AthleteSearchSort)
    ? (rawSort as AthleteSearchSort)
    : 'name';
  const rawDir = (params.get('dir') ?? '').trim().toLowerCase();
  const dir: AthleteSearchDir = rawDir === 'desc' ? 'desc' : rawDir === 'asc' ? 'asc' : sort === 'name' || sort === 'bestFinish' || sort === 'currentLeague' ? 'asc' : 'desc';
  const rawHasTitle = (params.get('hasTitle') ?? '').trim().toLowerCase();
  const hasTitle: AthleteHasTitle =
    rawHasTitle === 'only' || rawHasTitle === 'none' ? rawHasTitle : '';
  const rawHighest = (params.get('highest') ?? '').trim().toLowerCase();
  const highest: AthleteHighestLeague =
    rawHighest === 'superleague' || rawHighest === 'feeder' || rawHighest === 'none'
      ? rawHighest
      : '';
  const rawCup = (params.get('cup') ?? '').trim().toLowerCase();
  const cup: AthleteCupStatus =
    rawCup === 'participant' || rawCup === 'podium' || rawCup === 'title' || rawCup === 'none'
      ? rawCup
      : '';
  return {
    q: (params.get('q') ?? '').trim().slice(0, 200),
    colours: parseIntList(params.get('colours'), 0, 7),
    types: parseStringList(params.get('types')),
    current: parseStringList(params.get('current')),
    minNonPool: parseOptionalInt(params.get('minNonPool'), 0, 99),
    maxNonPool: parseOptionalInt(params.get('maxNonPool'), 0, 99),
    minHonours: parseOptionalInt(params.get('minHonours'), 0, 999),
    maxHonours: parseOptionalInt(params.get('maxHonours'), 0, 999),
    minTitles: parseOptionalInt(params.get('minTitles'), 0, 999),
    hasTitle,
    highest,
    bestFinishMax: parseOptionalInt(params.get('bestFinishMax'), 1, 32),
    minSuperSeasons: parseOptionalInt(params.get('minSuperSeasons'), 0, 99),
    maxSuperSeasons: parseOptionalInt(params.get('maxSuperSeasons'), 0, 99),
    cup,
    sort,
    dir,
    take: parseOptionalInt(params.get('take'), 1, 500) ?? 100,
    skip: parseOptionalInt(params.get('skip'), 0, 100000) ?? 0,
  };
}

/**
 * Serializes page state back to a URL search string. Default values are
 * omitted so copied links stay short; an all-default state yields `''`.
 */
export function buildAthleteSearchString(params: AthleteSearchParams): string {
  const query = new URLSearchParams();
  if (params.q.trim()) {
    query.set('q', params.q.trim());
  }
  if (params.colours.length > 0) {
    query.set('colours', [...params.colours].sort((a, b) => a - b).join(','));
  }
  if (params.types.length > 0) {
    query.set('types', params.types.join(','));
  }
  if (params.current.length > 0) {
    query.set('current', params.current.join(','));
  }
  if (params.minNonPool !== null) {
    query.set('minNonPool', String(params.minNonPool));
  }
  if (params.maxNonPool !== null) {
    query.set('maxNonPool', String(params.maxNonPool));
  }
  if (params.minHonours !== null) {
    query.set('minHonours', String(params.minHonours));
  }
  if (params.maxHonours !== null) {
    query.set('maxHonours', String(params.maxHonours));
  }
  if (params.minTitles !== null) {
    query.set('minTitles', String(params.minTitles));
  }
  if (params.hasTitle) {
    query.set('hasTitle', params.hasTitle);
  }
  if (params.highest) {
    query.set('highest', params.highest);
  }
  if (params.bestFinishMax !== null) {
    query.set('bestFinishMax', String(params.bestFinishMax));
  }
  if (params.minSuperSeasons !== null) {
    query.set('minSuperSeasons', String(params.minSuperSeasons));
  }
  if (params.maxSuperSeasons !== null) {
    query.set('maxSuperSeasons', String(params.maxSuperSeasons));
  }
  if (params.cup) {
    query.set('cup', params.cup);
  }
  if (params.sort !== 'name' || params.dir !== 'asc') {
    query.set('sort', params.sort);
    query.set('dir', params.dir);
  }
  if (params.take !== 100) {
    query.set('take', String(params.take));
  }
  if (params.skip !== 0) {
    query.set('skip', String(params.skip));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export function isDefaultAthleteSearch(params: AthleteSearchParams): boolean {
  return buildAthleteSearchString(params) === '';
}

export interface AthleteFilterChip {
  key: string;
  label: string;
}

/**
 * Human-readable summary of the active (non-default) filters for chips and
 * screen-reader announcements. Order follows the filter layout.
 */
export function activeAthleteFilterChips(params: AthleteSearchParams): AthleteFilterChip[] {
  const chips: AthleteFilterChip[] = [];
  if (params.q.trim()) {
    chips.push({ key: 'q', label: `Name contains “${params.q.trim()}”` });
  }
  if (params.minNonPool !== null || params.maxNonPool !== null) {
    const range =
      params.minNonPool !== null && params.maxNonPool !== null
        ? `${params.minNonPool}–${params.maxNonPool}`
        : params.minNonPool !== null
          ? `≥ ${params.minNonPool}`
          : `≤ ${params.maxNonPool}`;
    chips.push({ key: 'nonPool', label: `Non-pool seasons ${range}` });
  }
  if (params.current.length > 0) {
    chips.push({ key: 'current', label: `Current: ${params.current.join(', ')}` });
  }
  if (params.colours.length > 0) {
    chips.push({ key: 'colours', label: `Colours: ${[...params.colours].sort((a, b) => a - b).join(', ')}` });
  }
  if (params.types.length > 0) {
    chips.push({ key: 'types', label: `Types: ${params.types.join(', ')}` });
  }
  if (params.minHonours !== null || params.maxHonours !== null) {
    const range =
      params.minHonours !== null && params.maxHonours !== null
        ? `${params.minHonours}–${params.maxHonours}`
        : params.minHonours !== null
          ? `≥ ${params.minHonours}`
          : `≤ ${params.maxHonours}`;
    chips.push({ key: 'honours', label: `Honours ${range}` });
  }
  if (params.minTitles !== null) {
    chips.push({ key: 'minTitles', label: `Titles ≥ ${params.minTitles}` });
  }
  if (params.hasTitle === 'only') {
    chips.push({ key: 'hasTitle', label: 'Has a title' });
  } else if (params.hasTitle === 'none') {
    chips.push({ key: 'hasTitle', label: 'No titles' });
  }
  if (params.highest) {
    const label =
      params.highest === 'superleague'
        ? 'Reached Superleague'
        : params.highest === 'feeder'
          ? 'Feeder only'
          : 'Never active';
    chips.push({ key: 'highest', label: `Highest: ${label}` });
  }
  if (params.bestFinishMax !== null) {
    chips.push({ key: 'bestFinishMax', label: `Best finish P≤${params.bestFinishMax}` });
  }
  if (params.minSuperSeasons !== null || params.maxSuperSeasons !== null) {
    const range =
      params.minSuperSeasons !== null && params.maxSuperSeasons !== null
        ? `${params.minSuperSeasons}–${params.maxSuperSeasons}`
        : params.minSuperSeasons !== null
          ? `≥ ${params.minSuperSeasons}`
          : `≤ ${params.maxSuperSeasons}`;
    chips.push({ key: 'superSeasons', label: `Superleague seasons ${range}` });
  }
  if (params.cup) {
    const label =
      params.cup === 'participant'
        ? 'Cup participant'
        : params.cup === 'podium'
          ? 'Cup podium'
          : params.cup === 'title'
            ? 'Cup title'
            : 'No Cup appearances';
    chips.push({ key: 'cup', label: `Cup: ${label}` });
  }
  return chips;
}

/**
 * Converts page state to the backend request shape. `skip`/`take` come from
 * pagination; everything else maps one-to-one to backend filters.
 */
export function toAthleteSearchRequest(params: AthleteSearchParams): AthleteSearchRequest {
  return {
    q: params.q.trim() ? params.q.trim() : undefined,
    colours: params.colours.length > 0 ? params.colours : undefined,
    types: params.types.length > 0 ? params.types : undefined,
    current: params.current.length > 0 ? params.current : undefined,
    minNonPool: params.minNonPool,
    maxNonPool: params.maxNonPool,
    minHonours: params.minHonours,
    maxHonours: params.maxHonours,
    minTitles: params.minTitles,
    hasTitle: params.hasTitle ? params.hasTitle : null,
    highest: params.highest ? params.highest : null,
    bestFinishMax: params.bestFinishMax,
    minSuperSeasons: params.minSuperSeasons,
    maxSuperSeasons: params.maxSuperSeasons,
    cup: params.cup ? params.cup : null,
    sort: params.sort,
    dir: params.dir,
    skip: params.skip,
    take: params.take,
  };
}
