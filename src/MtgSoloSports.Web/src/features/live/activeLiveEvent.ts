import { chaosLivePath, livePath } from '../routing/routes';
import type { EventKey, SelectionKey } from '../events/eventModel';
import type { SeasonProgress, SeasonStatus } from '../dashboard/dashboardApi';
import { seasonFlow } from '../dashboard/seasonFlowSteps';

/**
 * MSS-143 shared read-only active-event resolver for Live.
 *
 * The sidebar Live entry and the bare `/live` route must open the currently
 * active competition for the selected save, regardless of whether the season
 * is in league play, Chaos Cup, or another supported playable postseason
 * event. The authoritative persisted season/lifecycle state (SeasonProgress
 * plus SeasonStatus) is the single source of truth; this module never infers
 * the active event from the most recent league stage, cached component state,
 * or hardcoded Superleague defaults, and it never invents a second lifecycle
 * state machine. It reuses the existing Dashboard `seasonFlow` next-step
 * resolution so Live and Dashboard agree on the legal next event.
 *
 * - League play (`CompleteNextGlobalStage`) stays on the league LivePage.
 * - An in-progress Chaos Cup (`RunChaosCup`) resolves to the Chaos Cup Live
 *   view. `eventProgress` stays null for Chaos (PostseasonEvents has no chaos
 *   shape), so the resolver reads the legal next action directly instead of
 *   only `eventProgress.event`. This is the reported MSS-143 bug: bare Live
 *   fell through to stale Superleague content.
 * - Other round-based postseason events (qualifier, Color/Type Cups) resolve
 *   to their existing LiveEventView via `eventProgress`.
 * - Cup squad selections resolve to the existing CupSelectionView.
 * - Setup/transition steps with no playable Live view (SelectChaosCup,
 *   movement/rebalance pre-resolution, StartNextSeason, unknown actions)
 *   resolve to a Dashboard CTA so Live never shows stale Superleague data as
 *   the active competition.
 */

export type ActiveLiveTarget =
  | { kind: 'league' }
  | { kind: 'chaos'; season: number }
  | { kind: 'event'; event: EventKey; season: number }
  | { kind: 'selection'; selection: SelectionKey; season: number }
  | { kind: 'dashboard'; season: number | null; action: string | null };

export function resolveActiveLiveTarget(
  progress: SeasonProgress,
  status: SeasonStatus | null,
): ActiveLiveTarget {
  const flow = seasonFlow(progress, status);
  const season = flow.seasonNumber;
  if (!status) {
    // No lifecycle status: league play is the only safe Live view while the
    // season is incomplete; a completed season with no status cannot prove a
    // playable event, so Live defers to the Dashboard instead of guessing.
    return progress.isSeasonComplete
      ? { kind: 'dashboard', season, action: null }
      : { kind: 'league' };
  }
  const next = flow.next;
  if (!next) {
    return { kind: 'dashboard', season, action: status.legalNextActions[0] ?? null };
  }
  if (next.kind === 'live') {
    return { kind: 'league' };
  }
  // next.kind === 'event': reuse the Dashboard's single legal-next resolution.
  if (next.liveEvent === 'chaos-cup') {
    return { kind: 'chaos', season };
  }
  if (next.liveEvent !== null) {
    return { kind: 'event', event: next.liveEvent, season };
  }
  if (next.liveSelection !== null) {
    return { kind: 'selection', selection: next.liveSelection, season };
  }
  // Transitions resolve on the Dashboard before their Live reveal; field
  // selection, season start and unknown actions have no playable Live view.
  return { kind: 'dashboard', season, action: next.action };
}

/**
 * Sidebar Live destination for the resolved target. Playable targets link
 * directly to their Live view so the sidebar opens the active competition;
 * league and Dashboard-CTA targets keep the bare Live URL (the Live page
 * itself renders the league view or the Dashboard CTA, never a silent
 * Dashboard redirect).
 */
export function activeLivePath(saveId: string, target: ActiveLiveTarget): string {
  switch (target.kind) {
    case 'chaos':
      return chaosLivePath(saveId, target.season, null, null);
    case 'event':
      return livePath(saveId, { event: target.event, season: target.season });
    case 'selection':
      return livePath(saveId, { event: target.selection, season: target.season });
    case 'league':
    case 'dashboard':
      return livePath(saveId);
  }
}

/** True when the Live route carries an explicit event/selection/transition deep link. */
export function hasExplicitLiveEvent(
  route: { event: unknown; selection: unknown; transition: unknown },
): boolean {
  return route.event !== null || route.selection !== null || route.transition !== null;
}
