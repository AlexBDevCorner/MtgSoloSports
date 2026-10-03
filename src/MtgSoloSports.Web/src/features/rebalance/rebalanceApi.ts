import { fetchJson } from '../../shared/api/http';

/**
 * One athlete in the persisted feeder-rebalance result.
 * Mirrors `RebalanceMovementMember` (camelCased): identity, sporting color,
 * source/destination league provenance, movement reason and card artwork.
 * `kind` is one of `SuperleagueDeparture` (feeder → Superleague),
 * `SuperleagueReturn` (Superleague → feeder), `RebalanceDisplacement`
 * (feeder → common pool) or `RebalanceDraw` (common pool → feeder).
 * `fromSeasonRank` is the source-league rank (1–32) or 0 for pool draws.
 */
export interface RebalanceMovementMember {
  athleteId: number;
  name: string;
  sportingColor: string;
  fromLeagueId: number;
  fromLeagueName: string;
  toLeagueId: number;
  toLeagueName: string;
  kind: string;
  fromSeasonRank: number;
  imageUrl: string | null;
}

/**
 * Per-feeder outcome for one rebalance transition.
 * `startingCount` is the source feeder size (32); after Superleague
 * departures/returns the league holds `provisionalCount`
 * (32 − departed + returned); pool draws/displacements restore `finalCount`.
 */
export interface RebalanceColorResult {
  leagueId: number;
  leagueName: string;
  sportingColor: string;
  startingCount: number;
  departedCount: number;
  returnedCount: number;
  provisionalCount: number;
  displacedCount: number;
  drawnCount: number;
  finalCount: number;
}

/**
 * Authoritative feeder-rebalance result for one season transition.
 * Read-only: built only from persisted next-season rows plus movement history
 * so replay never resimulates. `fromSeasonNumber` pins the historical event,
 * which keeps revisits stable instead of rederiving from current leagues.
 */
export interface RebalanceResult {
  saveId: string;
  fromSeasonNumber: number;
  toSeasonNumber: number;
  colors: RebalanceColorResult[];
  draws: RebalanceMovementMember[];
  displaced: RebalanceMovementMember[];
  departed: RebalanceMovementMember[];
  returned: RebalanceMovementMember[];
  totalDrawn: number;
  totalDisplaced: number;
  totalDeparted: number;
  totalReturned: number;
  poolCount: number;
  movementCount: number;
}

export async function fetchRebalanceResult(
  saveId: string,
  fromSeason: number,
  signal?: AbortSignal,
): Promise<RebalanceResult> {
  return fetchJson<RebalanceResult>(
    `/api/saves/${saveId}/superleague/rebalance?fromSeason=${fromSeason}`,
    { signal },
  );
}
