import { fetchJson } from '../../shared/api/http';

export interface AthleteSearchResult {
  athleteId: number;
  name: string;
  imageUrl: string | null;
  typeLine: string;
  sportingColor: number;
  sportingColorName: string;
  creatureTypes: string[];
  isActive: boolean;
  currentLeagueName: string | null;
  currentLeagueKind: number | null;
  nonPoolSeasons: number;
  honoursCount: number;
  titlesCount: number;
  bestSeasonFinish: number | null;
  bestSeasonNumber: number | null;
  everSuperleague: boolean;
  superSeasons: number;
  cupAppearances: number;
  cupPodiums: number;
  cupTitles: number;
}

export interface AthleteSearchResponse {
  saveId: string;
  totalCount: number;
  skip: number;
  take: number;
  results: AthleteSearchResult[];
}

export interface AthleteSearchColourOption {
  value: number;
  name: string;
  count: number;
}

export interface AthleteSearchTypeOption {
  value: string;
  count: number;
}

export interface AthleteSearchLeagueOption {
  name: string;
  kind: number | null;
  isPool: boolean;
  count: number;
}

export interface AthleteSearchOptions {
  saveId: string;
  colours: AthleteSearchColourOption[];
  creatureTypes: AthleteSearchTypeOption[];
  currentLeagues: AthleteSearchLeagueOption[];
  maxNonPoolSeasons: number;
  maxHonours: number;
  maxTitles: number;
  hasSuperleague: boolean;
  hasCups: boolean;
}

export interface AthleteSearchRequest {
  q?: string;
  colours?: number[];
  types?: string[];
  current?: string[];
  minNonPool?: number | null;
  maxNonPool?: number | null;
  minHonours?: number | null;
  maxHonours?: number | null;
  minTitles?: number | null;
  hasTitle?: string | null;
  highest?: string | null;
  bestFinishMax?: number | null;
  minSuperSeasons?: number | null;
  maxSuperSeasons?: number | null;
  cup?: string | null;
  sort?: string | null;
  dir?: string | null;
  skip?: number;
  take?: number;
}

/**
 * Builds the backend query string for athlete search. Only non-default
 * values are sent; the backend applies its own defaults for the rest.
 * Multi-value filters join with commas (OR within the category).
 */
export function buildAthleteSearchQuery(request: AthleteSearchRequest): string {
  const params = new URLSearchParams();
  if (request.q && request.q.trim()) {
    params.set('q', request.q.trim());
  }
  if (request.minNonPool !== undefined && request.minNonPool !== null) {
    params.set('minNonPool', String(request.minNonPool));
  }
  if (request.maxNonPool !== undefined && request.maxNonPool !== null) {
    params.set('maxNonPool', String(request.maxNonPool));
  }
  if (request.colours && request.colours.length > 0) {
    params.set('colours', request.colours.join(','));
  }
  if (request.types && request.types.length > 0) {
    params.set('types', request.types.join(','));
  }
  if (request.current && request.current.length > 0) {
    params.set('current', request.current.join(','));
  }
  if (request.minHonours !== undefined && request.minHonours !== null) {
    params.set('minHonours', String(request.minHonours));
  }
  if (request.maxHonours !== undefined && request.maxHonours !== null) {
    params.set('maxHonours', String(request.maxHonours));
  }
  if (request.minTitles !== undefined && request.minTitles !== null) {
    params.set('minTitles', String(request.minTitles));
  }
  if (request.hasTitle) {
    params.set('hasTitle', request.hasTitle);
  }
  if (request.highest) {
    params.set('highest', request.highest);
  }
  if (request.bestFinishMax !== undefined && request.bestFinishMax !== null) {
    params.set('bestFinishMax', String(request.bestFinishMax));
  }
  if (request.minSuperSeasons !== undefined && request.minSuperSeasons !== null) {
    params.set('minSuperSeasons', String(request.minSuperSeasons));
  }
  if (request.maxSuperSeasons !== undefined && request.maxSuperSeasons !== null) {
    params.set('maxSuperSeasons', String(request.maxSuperSeasons));
  }
  if (request.cup) {
    params.set('cup', request.cup);
  }
  if (request.sort) {
    params.set('sort', request.sort);
  }
  if (request.dir) {
    params.set('dir', request.dir);
  }
  if (request.skip !== undefined) {
    params.set('skip', String(request.skip));
  }
  if (request.take !== undefined) {
    params.set('take', String(request.take));
  }
  const query = params.toString();
  return query ? `?${query}` : '';
}

export async function fetchAthleteSearch(
  saveId: string,
  request: AthleteSearchRequest,
  signal?: AbortSignal,
): Promise<AthleteSearchResponse> {
  return fetchJson<AthleteSearchResponse>(
    `/api/saves/${saveId}/athletes/search${buildAthleteSearchQuery(request)}`,
    { signal },
  );
}

export async function fetchAthleteSearchOptions(
  saveId: string,
  signal?: AbortSignal,
): Promise<AthleteSearchOptions> {
  return fetchJson<AthleteSearchOptions>(`/api/saves/${saveId}/athletes/search/options`, {
    signal,
  });
}
