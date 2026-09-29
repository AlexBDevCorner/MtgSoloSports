/**
 * MSS-040 browser routing convention.
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
 * - `/saves/:saveId/records`
 * - `/saves/:saveId/cups`
 * - `/saves/:saveId/athletes/:athleteId`
 *
 * Reserved for MSS-041 (documented here, not implemented):
 * - `/saves/:saveId/leagues/:leagueId` via {@link leagueStandingsPath}.
 *   The parser currently treats this path as notFound until MSS-041 adds the
 *   standings page. New save-scoped pages must follow the same
 *   `/saves/:saveId/<area>` prefix and share the helpers below.
 *
 * Query-string scheme (minimal, documented):
 * - Live uses `league` (league id) and `round` (round number). Absent values
 *   fall back to per-save stored league then first league, and to the latest
 *   persisted round. Present values are authoritative and shareable.
 * - History uses `season`, `competition` (league id), `stage`, `round`.
 *   Absent values fall back to the latest available entry at each level.
 *   Present values restore that exact season/competition/stage/round and are
 *   shareable across tabs.
 *
 * Save IDs in the URL are authoritative for requests and page context. The
 * `localStorage` last-selected-save value is only a fallback for the root
 * entry flow and never overrides an explicit save-scoped URL.
 */

export type Route =
  | { name: 'root' }
  | { name: 'saves' }
  | { name: 'dashboard'; saveId: string }
  | { name: 'live'; saveId: string; leagueId: number | null; round: number | null }
  | {
      name: 'history';
      saveId: string;
      season: number | null;
      competitionId: number | null;
      stage: number | null;
      round: number | null;
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
  query?: { league?: number | null; round?: number | null },
): string {
  const params = new URLSearchParams();
  if (query?.league !== undefined && query.league !== null) {
    params.set('league', String(query.league));
  }
  if (query?.round !== undefined && query.round !== null) {
    params.set('round', String(query.round));
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
 * Reserved save-scoped league standings detail route for MSS-041.
 * Do not render a page for this path in MSS-040; the parser treats it as
 * notFound until the standings UI lands. MSS-041 should parse it into a
 * first-class route and link league rows here.
 */
export function leagueStandingsPath(saveId: string, leagueId: number): string {
  return `/saves/${encodeURIComponent(saveId)}/leagues/${leagueId}`;
}

export function routeSaveId(route: Route): string | null {
  switch (route.name) {
    case 'dashboard':
    case 'live':
    case 'history':
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
      };
    case 'records':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return { name: 'records', saveId };
    case 'cups':
      if (segments.length !== 3) {
        return { name: 'notFound', path: pathname + search };
      }
      return { name: 'cups', saveId };
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
