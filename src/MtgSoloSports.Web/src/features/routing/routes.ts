/**
 * MSS-040 browser routing convention, extended by MSS-041 with Standings.
 *
 * Every main page and athlete profile has a stable, copyable URL. Save-scoped
 * routes encode the save identifier; profile routes encode the athlete id.
 *
 * Route shapes:
 * - `/` -> root resolver (last-selected-save fallback or Saves landing)
 * - `/saves` -> save management
 * - `/saves/:saveId/dashboard`
 * - `/saves/:saveId/live?league=<id>&round=<n>`
 * - `/saves/:saveId/history?season=<n>&competition=<id>&stage=<n>&round=<n>`
 * - `/saves/:saveId/standings?league=<id>&season=<n>&view=season|matrix`
 * - `/saves/:saveId/leagues/:leagueId` (MSS-040 reservation, now a Standings alias)
 * - `/saves/:saveId/leagues/:leagueId/standings?season=<n>&view=season|matrix`
 * - `/saves/:saveId/records`
 * - `/saves/:saveId/cups`
 * - `/saves/:saveId/athletes/:athleteId`
 *
 * Query-string scheme (minimal, documented):
 * - Live uses `league` (league id) and `round` (round number). Absent values
 *   fall back to per-save stored league then first league, and to the latest
 *   persisted round. Present values are authoritative and shareable.
 * - History uses `season`, `competition` (league id), `stage`, `round`.
 *   Absent values fall back to the latest available entry at each level.
 *   Present values restore that exact season/competition/stage/round and are
 *   shareable across tabs.
 * - Standings uses `league` (league id), `season` (season number) and
 *   `view` (`season` default or `matrix`). Absent league/season fall back to
 *   the first league and latest season; an explicit league id that is not
 *   part of the selected season is an error, never a silent substitution.
 *   Present values are authoritative and shareable.
 *
 * Save IDs in the URL are authoritative for requests and page context. The
 * `localStorage` last-selected-save value is only a fallback for the root
 * entry flow and never overrides an explicit save-scoped URL.
 */

import { isEventKey, type EventKey } from '../events/eventModel.ts';

export type StandingsView = 'season' | 'matrix';

export type Route =
  | { name: 'root' }
  | { name: 'saves' }
  | { name: 'dashboard'; saveId: string }
  | {
      name: 'live';
      saveId: string;
      leagueId: number | null;
      round: number | null;
      event: EventKey | null;
      eventSeason: number | null;
      group: number | null;
    }
  | {
      name: 'history';
      saveId: string;
      season: number | null;
      competitionId: number | null;
      stage: number | null;
      round: number | null;
      event: EventKey | null;
      group: number | null;
    }
  | {
      name: 'standings';
      saveId: string;
      leagueId: number | null;
      season: number | null;
      view: StandingsView | null;
    }
  | { name: 'records'; saveId: string }
  | { name: 'cups'; saveId: string }
  | { name: 'athlete'; saveId: string; athleteId: number; rawAthleteId: string }
  | { name: 'invalidAthlete'; saveId: string; rawAthleteId: string }
  | { name: 'notFound'; path: string };

export function savesPath(): string {
  return '/saves';
}

export function dashboardPath(saveId: string): string {
  return `/saves/${encodeURIComponent(saveId)}/dashboard`;
}

export function livePath(
  saveId: string,
  query?: {
    league?: number | null;
    round?: number | null;
    event?: EventKey | null;
    season?: number | null;
    group?: number | null;
  },
): string {
  const params = new URLSearchParams();
  if (query?.league !== undefined && query.league !== null) {
    params.set('league', String(query.league));
  }
  if (query?.round !== undefined && query.round !== null) {
    params.set('round', String(query.round));
  }
  if (query?.event !== undefined && query.event !== null) {
    params.set('event', query.event);
  }
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  if (query?.group !== undefined && query.group !== null) {
    params.set('group', String(query.group));
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  return `/saves/${encodeURIComponent(saveId)}/live${suffix}`;
}

export function historyPath(
  saveId: string,
  query?: {
    season?: number | null;
    competition?: number | null;
    stage?: number | null;
    round?: number | null;
    event?: EventKey | null;
    group?: number | null;
  },
): string {
  const params = new URLSearchParams();
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  if (query?.competition !== undefined && query.competition !== null) {
    params.set('competition', String(query.competition));
  }
  if (query?.stage !== undefined && query.stage !== null) {
    params.set('stage', String(query.stage));
  }
  if (query?.round !== undefined && query.round !== null) {
    params.set('round', String(query.round));
  }
  if (query?.event !== undefined && query.event !== null) {
    params.set('event', query.event);
  }
  if (query?.group !== undefined && query.group !== null) {
    params.set('group', String(query.group));
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  return `/saves/${encodeURIComponent(saveId)}/history${suffix}`;
}

export function recordsPath(saveId: string): string {
  return `/saves/${encodeURIComponent(saveId)}/records`;
}

export function cupsPath(saveId: string): string {
  return `/saves/${encodeURIComponent(saveId)}/cups`;
}

export function athletePath(saveId: string, athleteId: number): string {
  return `/saves/${encodeURIComponent(saveId)}/athletes/${athleteId}`;
}

/**
 * MSS-040 reservation, now a first-class Standings alias (MSS-041).
 * Keep the exact `/saves/:saveId/leagues/:leagueId` shape for compatibility;
 * the parser maps it to the standings route with season/view from the query.
 */
export function leagueStandingsPath(saveId: string, leagueId: number): string {
  return `/saves/${encodeURIComponent(saveId)}/leagues/${leagueId}`;
}

/**
 * Canonical league-scoped standings detail route, e.g.
 * `/saves/:saveId/leagues/:leagueId/standings?season=N&view=season`.
 */
export function standingsLeaguePath(
  saveId: string,
  leagueId: number,
  query?: { season?: number | null; view?: StandingsView | null },
): string {
  const params = new URLSearchParams();
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  if (query?.view !== undefined && query.view !== null) {
    params.set('view', query.view);
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  return `/saves/${encodeURIComponent(saveId)}/leagues/${leagueId}/standings${suffix}`;
}

/**
 * Save-scoped standings entry point, e.g.
 * `/saves/:saveId/standings?league=<id>&season=<n>&view=season|matrix`.
 * League/season/view selections are reflected in the URL and survive refresh,
 * Back/Forward and new tabs.
 */
export function standingsPath(
  saveId: string,
  query?: { league?: number | null; season?: number | null; view?: StandingsView | null },
): string {
  const params = new URLSearchParams();
  if (query?.league !== undefined && query.league !== null) {
    params.set('league', String(query.league));
  }
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  if (query?.view !== undefined && query.view !== null) {
    params.set('view', query.view);
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  return `/saves/${encodeURIComponent(saveId)}/standings${suffix}`;
}

export function routeSaveId(route: Route): string | null {
  switch (route.name) {
    case 'dashboard':
    case 'live':
    case 'history':
    case 'standings':
    case 'records':
    case 'cups':
    case 'athlete':
    case 'invalidAthlete':
      return route.saveId;
    default:
      return null;
  }
}

function parsePositiveInt(raw: string | null): number | null {
  if (raw === null || raw.trim() === '') {
    return null;
  }
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    return null;
  }
  // Reject values with trailing junk such as "12abc".
  if (String(parsed) !== raw.trim()) {
    return null;
  }
  return parsed;
}

function parseOptionalPositiveInt(params: URLSearchParams, key: string): number | null {
  const raw = params.get(key);
  if (raw === null || raw === '') {
    return null;
  }
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    return null;
  }
  if (String(parsed) !== raw.trim()) {
    return null;
  }
  return parsed;
}

function parseEvent(params: URLSearchParams): EventKey | null {
  const value = params.get('event');
  return isEventKey(value) ? value : null;
}

function normalizePathname(pathname: string): string {
  if (pathname.length > 1 && pathname.endsWith('/')) {
    return pathname.slice(0, -1);
  }
  return pathname;
}

/**
 * Parse a browser location into a typed route. Pure and DOM-free so it is
 * unit-testable and usable in both the app and tests.
 */
export function parseRoute(pathname: string, search: string): Route {
  const normalized = normalizePathname(pathname);
  if (normalized === '/' || normalized === '') {
    return { name: 'root' };
  }
  if (normalized === '/saves') {
    return { name: 'saves' };
  }

  const segments = normalized.split('/').filter((part) => part.length > 0);
  // Expected: saves/:saveId/<area>[/<id>]
  if (segments.length < 3 || segments[0] !== 'saves') {
    return { name: 'notFound', path: pathname + search };
  }
  const saveId = decodeURIComponentSafe(segments[1] ?? '');
  if (!saveId) {
    return { name: 'notFound', path: pathname + search };
  }
  const area = segments[2] ?? '';
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);

  switch (area) {
    case 'dashboard':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return { name: 'dashboard', saveId };
    case 'live':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return {
        name: 'live',
        saveId,
        leagueId: parseOptionalPositiveInt(params, 'league'),
        round: parseOptionalPositiveInt(params, 'round'),
        event: parseEvent(params),
        eventSeason: parseEvent(params) ? parseOptionalPositiveInt(params, 'season') : null,
        group: parseEvent(params) ? parseOptionalPositiveInt(params, 'group') : null,
      };
    case 'history':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return {
        name: 'history',
        saveId,
        season: parseOptionalPositiveInt(params, 'season'),
        competitionId: parseOptionalPositiveInt(params, 'competition'),
        stage: parseOptionalPositiveInt(params, 'stage'),
        round: parseOptionalPositiveInt(params, 'round'),
        event: parseEvent(params),
        group: parseEvent(params) ? parseOptionalPositiveInt(params, 'group') : null,
      };
    case 'records':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return { name: 'records', saveId };
    case 'cups':
      if (segments.length !== 3) {
        return { name: 'cups', saveId };
      }
      return { name: 'cups', saveId };
    case 'standings': {
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return {
        name: 'standings',
        saveId,
        leagueId: parseOptionalPositiveInt(params, 'league'),
        season: parseOptionalPositiveInt(params, 'season'),
        view: parseStandingsView(params.get('view')),
      };
    }
    case 'leagues': {
      // MSS-040 reservation `/saves/:saveId/leagues/:leagueId` plus the
      // MSS-041 canonical `/saves/:saveId/leagues/:leagueId/standings`.
      // Both map to the standings route; season/view come from the query.
      // segments: ['saves', saveId, 'leagues', leagueId, ('standings'?)]
      if (segments.length === 5 && segments[4] === 'standings') {
        const leagueId = parsePositiveInt(segments[3] ?? '');
        if (leagueId === null) {
          return { name: 'notFound', path: pathname + search };
        }
        return {
          name: 'standings',
          saveId,
          leagueId,
          season: parseOptionalPositiveInt(params, 'season'),
          view: parseStandingsView(params.get('view')),
        };
      }
      if (segments.length === 4) {
        const leagueId = parsePositiveInt(segments[3] ?? '');
        if (leagueId === null) {
          return { name: 'notFound', path: pathname + search };
        }
        return {
          name: 'standings',
          saveId,
          leagueId,
          season: parseOptionalPositiveInt(params, 'season'),
          view: parseStandingsView(params.get('view')),
        };
      }
      return { name: 'notFound', path: pathname + search };
    }
    case 'athletes': {
      if (segments.length !== 4) {
        return { name: 'notFound', path: pathname + search };
      }
      const raw = segments[3] ?? '';
      const parsed = parsePositiveInt(raw);
      if (parsed === null) {
        return { name: 'invalidAthlete', saveId, rawAthleteId: raw };
      }
      return { name: 'athlete', saveId, athleteId: parsed, rawAthleteId: raw };
    }
    default:
      return { name: 'notFound', path: pathname + search };
  }
}

function decodeURIComponentSafe(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function parseStandingsView(raw: string | null): StandingsView | null {
  if (raw === null || raw === '') {
    return null;
  }
  const normalized = raw.trim().toLowerCase();
  if (normalized === 'season' || normalized === 'matrix') {
    return normalized;
  }
  return null;
}
