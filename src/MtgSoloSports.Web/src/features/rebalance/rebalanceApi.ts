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
 * Per-league outcome for one rebalance transition. Tiered saves carry one
 * entry per feeder league (24: F1/F2/F3 per sporting color); v1 saves carry
 * one per color. `feederDivision` (1/2/3, 0 legacy) identifies the tier
 * without parsing names; `rebalancedUpIn/Out` and `rebalancedDownIn/Out`
 * count the structural F1↔F2/F2↔F3 cascade (zero for v1).
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
  feederDivision?: number | null;
  rebalancedUpIn?: number | null;
  rebalancedUpOut?: number | null;
  rebalancedDownIn?: number | null;
  rebalancedDownOut?: number | null;
  f2ProvisionalCount?: number | null;
  f3ProvisionalCount?: number | null;
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
  /** Structural F2→F1 / F3→F2 cascade moves (tiered saves). */
  rebalancedUp?: RebalanceMovementMember[];
  /** Structural F1→F2 / F2→F3 cascade moves (tiered saves). */
  rebalancedDown?: RebalanceMovementMember[];
  totalDrawn: number;
  totalDisplaced: number;
  totalDeparted: number;
  totalReturned: number;
  totalRebalancedUp?: number;
  totalRebalancedDown?: number;
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
