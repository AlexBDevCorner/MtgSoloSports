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
 * - `/saves/:saveId/live?event=<event|selection key>&season=<n>&group=<n>&round=<n>`
 * - `/saves/:saveId/live?transition=<movement|rebalance>&season=<fromSeason>` (MSS-053 transition reveal)
 * - `/saves/:saveId/history?season=<n>&competition=<id>&stage=<n>&round=<n>`
 * - `/saves/:saveId/standings?league=<id>&season=<n>&view=season|matrix`
 * - `/saves/:saveId/leagues/:leagueId` (MSS-040 reservation, now a Standings alias)
 * - `/saves/:saveId/leagues/:leagueId/standings?season=<n>&view=season|matrix`
 * - `/saves/:saveId/qualifiers?season=<n>` -> qualifier overview (MSS-060)
 * - `/saves/:saveId/qualifiers/superleague?season=<n>` -> one Superleague qualifier
 * - `/saves/:saveId/qualifiers/f1f2/:color?season=<n>` -> one F1↔F2 qualifier
 * - `/saves/:saveId/qualifiers/f2f3/:color?season=<n>` -> one F2↔F3 qualifier
 * - `/saves/:saveId/records`
 * - `/saves/:saveId/cups`
 * - `/saves/:saveId/cups/:cup/:season` (`cup` = `color` | `type`) -> one Cup edition
 * - `/saves/:saveId/cups/:cup/teams/:teamKey` -> one Cup team's history
 * - `/saves/:saveId/athletes?{search}` -> athlete search/browse (MSS-052)
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
 * - Athlete search (`/saves/:saveId/athletes`) keeps free-text query,
 *   sporting filters, sort and pagination in the query string
 *   (`?q=&colours=&types=&current=&minNonPool=&maxNonPool=&minHonours=&...&sort=&dir=&skip=&take=`).
 *   Absent values fall back to page defaults; present values restore that
 *   exact search and are shareable across tabs.
 *
 * Save IDs in the URL are authoritative for requests and page context. The
 * `localStorage` last-selected-save value is only a fallback for the root
 * entry flow and never overrides an explicit save-scoped URL.
 */

import {
  isEventKey,
  isSelectionKey,
  isTransitionKey,
  type EventKey,
  type SelectionKey,
  type TransitionKey,
} from '../events/eventModel.ts';
import { qualifierBoundaryFromSegment, type QualifierBoundary } from '../../shared/leagueTiers.ts';

export type StandingsView = 'season' | 'matrix';

export type CupKind = 'color' | 'type';

/** Which Cups page a `/cups` URL names: the hub, one edition or one team. */
export type CupsView =
  | { kind: 'hub' }
  | { kind: 'edition'; cup: CupKind; season: number }
  | { kind: 'team'; cup: CupKind; teamKey: string };

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
      /** Cup squad selection shown on Live (`?event=color-cup-selection`). */
      selection: SelectionKey | null;
      /**
       * Postseason transition reveal shown on Live
       * (`?transition=movement|rebalance&season=<fromSeason>`). Read-only
       * presentation over the persisted movement/rebalance result; the
       * sporting mutation runs from the Dashboard before navigating here.
       */
      transition: TransitionKey | null;
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
  | {
      name: 'qualifiers';
      saveId: string;
      /** Qualifier boundary (`superleague` | `f1f2` | `f2f3`); null shows the overview. */
      boundary: QualifierBoundary | null;
      /** Sporting color name for feeder qualifiers; null for the overview and Superleague. */
      color: string | null;
      /** Source season of the transition; null follows the latest resolved qualifiers. */
      season: number | null;
    }
  | { name: 'records'; saveId: string }
  | { name: 'cups'; saveId: string; view: CupsView }
  | { name: 'athletes'; saveId: string; search: string }
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
    event?: EventKey | SelectionKey | null;
    transition?: TransitionKey | null;
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
  if (query?.transition !== undefined && query.transition !== null) {
    params.set('transition', query.transition);
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

/**
 * Qualifier overview for a transition, e.g. `/saves/:saveId/qualifiers?season=2`.
 * Season is the source season; absent follows the latest resolved qualifiers.
 * One row per qualifier (1 v1, 17 tiered: Superleague plus 8 F1↔F2 and
 * 8 F2↔F3 colors) with completion state from persisted facts.
 */
export function qualifiersPath(saveId: string, query?: { season?: number | null }): string {
  const params = new URLSearchParams();
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  return `/saves/${encodeURIComponent(saveId)}/qualifiers${suffix}`;
}

/**
 * One qualifier event, e.g. `/saves/:saveId/qualifiers/f1f2/white?season=2`
 * or `/saves/:saveId/qualifiers/superleague?season=2`. Boundary segments are
 * `superleague` | `f1f2` | `f2f3`; color is the sporting-color name (any
 * case). The same page serves 32-athlete Superleague and 16-athlete feeder
 * fields from data.
 */
export function qualifierPath(
  saveId: string,
  boundary: QualifierBoundary,
  color: string | null,
  query?: { season?: number | null },
): string {
  const params = new URLSearchParams();
  if (query?.season !== undefined && query.season !== null) {
    params.set('season', String(query.season));
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : '';
  const segment =
    boundary === 'Feeder1Feeder2' ? 'f1f2' : boundary === 'Feeder2Feeder3' ? 'f2f3' : 'superleague';
  const colorPart =
    boundary === 'Superleague' || !color ? '' : `/${encodeURIComponent(color.toLowerCase())}`;
  return `/saves/${encodeURIComponent(saveId)}/qualifiers/${segment}${colorPart}${suffix}`;
}

export function recordsPath(saveId: string): string {
  return `/saves/${encodeURIComponent(saveId)}/records`;
}

export function cupsPath(saveId: string): string {
  return `/saves/${encodeURIComponent(saveId)}/cups`;
}

/** One Cup edition: the Color or Type Cup played after `season`. */
export function cupEditionPath(saveId: string, cup: CupKind, season: number): string {
  return `/saves/${encodeURIComponent(saveId)}/cups/${cup}/${season}`;
}

/** One Cup team. Color keys are lower-case color names; Type keys are creature types. */
export function cupTeamPath(saveId: string, cup: CupKind, teamKey: string): string {
  return `/saves/${encodeURIComponent(saveId)}/cups/${cup}/teams/${encodeURIComponent(teamKey)}`;
}

export function athletePath(saveId: string, athleteId: number): string {
  return `/saves/${encodeURIComponent(saveId)}/athletes/${athleteId}`;
}

/**
 * Athlete search/browse entry point, e.g.
 * `/saves/:saveId/athletes?q=faerie&minNonPool=5&sort=honours&dir=desc`.
 * Free text, filters, sort and pagination are reflected in the URL and
 * survive refresh, Back/Forward and new tabs.
 */
export function athletesPath(saveId: string, search?: string): string {
  const suffix = search && search.length > 0 ? (search.startsWith('?') ? search : `?${search}`) : '';
  return `/saves/${encodeURIComponent(saveId)}/athletes${suffix}`;
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
    case 'qualifiers':
    case 'records':
    case 'cups':
    case 'athlete':
    case 'invalidAthlete':
    case 'athletes':
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

function parseSelection(params: URLSearchParams): SelectionKey | null {
  const value = params.get('event');
  return isSelectionKey(value) ? value : null;
}

function parseTransition(params: URLSearchParams): TransitionKey | null {
  const value = params.get('transition');
  return isTransitionKey(value) ? value : null;
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
        selection: parseSelection(params),
        transition: parseTransition(params),
        eventSeason:
          parseEvent(params) || parseSelection(params) || parseTransition(params)
            ? parseOptionalPositiveInt(params, 'season')
            : null,
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
    case 'qualifiers': {
      // MSS-060 qualifier overview plus per-event detail:
      // `/saves/:saveId/qualifiers?season=N`,
      // `/saves/:saveId/qualifiers/superleague?season=N`,
      // `/saves/:saveId/qualifiers/f1f2/:color?season=N`.
      // segments: ['saves', saveId, 'qualifiers', boundary?, color?]
      if (segments.length === 3) {
        return {
          name: 'qualifiers',
          saveId,
          boundary: null,
          color: null,
          season: parseOptionalPositiveInt(params, 'season'),
        };
      }
      if (segments.length === 4 || segments.length === 5) {
        const boundary = qualifierBoundaryFromSegment(segments[3] ?? '');
        if (boundary === null) {
          return { name: 'notFound', path: pathname + search };
        }
        if (boundary === 'Superleague') {
          if (segments.length !== 4) {
            return { name: 'notFound', path: pathname + search };
          }
          return {
            name: 'qualifiers',
            saveId,
            boundary,
            color: null,
            season: parseOptionalPositiveInt(params, 'season'),
          };
        }
        if (segments.length !== 5) {
          return { name: 'notFound', path: pathname + search };
        }
        const color = decodeURIComponentSafe(segments[4] ?? '').trim();
        if (!color) {
          return { name: 'notFound', path: pathname + search };
        }
        return {
          name: 'qualifiers',
          saveId,
          boundary,
          color,
          season: parseOptionalPositiveInt(params, 'season'),
        };
      }
      return { name: 'notFound', path: pathname + search };
    }
    case 'cups': {
      const view = parseCupsView(segments.slice(3));
      return view ? { name: 'cups', saveId, view } : { name: 'notFound', path: pathname + search };
    }
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
      if (segments.length === 3) {
        return { name: 'athletes', saveId, search: search.startsWith('?') ? search : search ? `?${search}` : '' };
      }
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

/** `teams` is a reserved segment, so it never parses as a season. */
function parseCupsView(rest: string[]): CupsView | null {
  if (rest.length === 0) {
    return { kind: 'hub' };
  }
  const cup = rest[0];
  if (cup !== 'color' && cup !== 'type') {
    return null;
  }
  if (rest.length === 2) {
    const season = parsePositiveInt(rest[1] ?? '');
    return season === null ? null : { kind: 'edition', cup, season };
  }
  if (rest.length === 3 && rest[1] === 'teams') {
    const teamKey = decodeURIComponentSafe(rest[2] ?? '').trim();
    return teamKey ? { kind: 'team', cup, teamKey } : null;
  }
  return null;
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
