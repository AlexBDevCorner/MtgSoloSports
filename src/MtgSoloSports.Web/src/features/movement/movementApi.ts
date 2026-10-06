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
  /** Tier identity of the source league ("Superleague", "Feeder1", ...). */
  fromLeagueLevel?: string | null;
  fromSeasonRank: number;
  toLeagueId: number;
  toLeagueName: string;
  /** Tier identity of the destination league; null for pool sentinels. */
  toLeagueLevel?: string | null;
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

/**
 * One athlete in the persisted competitive feeder movement (MSS-060).
 * Covers F1↔F2 and F2↔F3 automatic promotions/relegations plus qualifier
 * incumbents/challengers with adjacent-tier source/destination identity.
 */
export interface FeederMovementMember {
  athleteId: number;
  name: string;
  sportingColor: number;
  sportingColorName: string;
  boundaryId: number;
  boundary: string;
  fromLeagueId: number;
  fromLeagueName: string;
  fromLeagueLevel: string;
  fromSeasonRank: number;
  toLeagueId: number;
  toLeagueName: string;
  toLeagueLevel: string | null;
  movementKind: string;
  imageUrl: string | null;
}

/** Persisted feeder automatic movement for one season transition. */
export interface FeederMovements {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  movements: FeederMovementMember[];
  movementCount: number;
}

export async function fetchFeederMovements(
  saveId: string,
  fromSeason: number,
  signal?: AbortSignal,
): Promise<FeederMovements> {
  return fetchJson<FeederMovements>(
    `/api/saves/${saveId}/feeder-movements?fromSeason=${fromSeason}`,
    { signal },
  );
}
