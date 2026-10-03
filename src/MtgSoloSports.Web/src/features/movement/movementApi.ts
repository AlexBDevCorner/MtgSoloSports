import { fetchJson } from '../../shared/api/http';

/**
 * One athlete in the persisted postseason movement summary.
 * Mirrors `AutomaticMovementMember` (camelCased): identity, sporting color,
 * source league/rank provenance, resolved next-season league and the card
 * artwork used by the reveal tiles.
 */
export interface MovementMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromSeasonRank: number;
  toLeagueId: number;
  toLeagueName: string;
  movementKind: string;
  imageUrl: string | null;
}

/**
 * Authoritative automatic promotion/relegation for one season transition.
 * Read-only: built only from persisted next-season rows plus movement history
 * so replay never resimulates. `fromSeasonNumber` pins the historical event,
 * which keeps revisits stable instead of rederiving from current leagues.
 */
export interface AutomaticMovement {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  superleagueLeagueId: number;
  superleagueLeagueName: string;
  safe: MovementMember[];
  promoted: MovementMember[];
  relegated: MovementMember[];
  qualifierIncumbents: MovementMember[];
  qualifierChallengers: MovementMember[];
  poolCount: number;
  movementCount: number;
}

/** One promoted athlete of the inaugural Season 2 Superleague roster. */
export interface InauguralRosterMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromSeasonRank: number;
  imageUrl: string | null;
}

/** Persisted inaugural roster (Season 1 -> Season 2, promotions only). */
export interface InauguralRoster {
  saveId: string;
  seasonNumber: number;
  superleagueLeagueId: number;
  superleagueLeagueName: string;
  members: InauguralRosterMember[];
  movementCount: number;
}

export async function fetchAutomaticMovement(
  saveId: string,
  fromSeason: number,
  signal?: AbortSignal,
): Promise<AutomaticMovement> {
  return fetchJson<AutomaticMovement>(
    `/api/saves/${saveId}/superleague/automatic-movement?fromSeason=${fromSeason}`,
    { signal },
  );
}

export async function fetchInauguralRoster(
  saveId: string,
  signal?: AbortSignal,
): Promise<InauguralRoster> {
  return fetchJson<InauguralRoster>(`/api/saves/${saveId}/superleague/inaugural`, {
    signal,
  });
}
