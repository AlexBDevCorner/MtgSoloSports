import { chaosLivePath, livePath, qualifierLivePath } from '../routing/routes';
import type { EventKey, SelectionKey } from '../events/eventModel';
import type { SeasonProgress, SeasonStatus } from '../dashboard/dashboardApi';
import { isTieredProgress, seasonFlow } from '../dashboard/seasonFlowSteps';
import { canonicalQualifierLiveParams } from '../qualifiers/qualifierModel';

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
  | { kind: 'qualifier'; qualifier: string; season: number }
  | { kind: 'selection'; selection: SelectionKey; season: number }
  | { kind: 'dashboard'; season: number | null; action: string | null };

/**
 * MSS-143 correction: on tiered (three-feeder) saves the qualifier phase is
 * 17 sequential independent competitions in canonical order (Superleague,
 * then F1↔F2 by color, then F2↔F3 by color), but the status exposes only
 * `event='qualifier'` with phase-wide progress. The legacy
 * `?event=qualifier&season=N` Live URL renders only the Superleague history
 * slice, so after the Superleague qualifier completes the active competition
 * is a feeder qualifier and bare Live must not keep showing the completed
 * Superleague event. Returns the active Live `?qualifier=` param
 * (`superleague` or `f1f2-<color>` / `f2f3-<color>`), or null when the save
 * is not tiered / the active event is not a tiered qualifier.
 */
export function resolveActiveQualifierParam(
  progress: SeasonProgress,
  status: SeasonStatus | null,
): string | null {
  if (!status || !isTieredProgress(progress)) {
    return null;
  }
  const order = canonicalQualifierLiveParams();
  const completed =
    status.qualifierProgress?.completedEvents ?? status.eventProgress?.completedEvents ?? null;
  if (completed !== null && completed !== undefined) {
    if (completed < 0 || completed >= order.length) {
      return null;
    }
    return order[completed] ?? null;
  }
  const played =
    status.qualifierProgress?.roundsPlayed ?? status.eventProgress?.roundsPlayed ?? 0;
  const derived = Math.min(order.length - 1, Math.max(0, Math.floor(played / 16)));
  return order[derived] ?? null;
}

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
  if (next.liveEvent === 'qualifier') {
    // Tiered qualifier phase: route to the active individual qualifier so
    // bare Live never shows a completed Superleague qualifier as the current
    // event. The Superleague active keeps the legacy event view (with its
    // canonical next-qualifier CTA); feeder actives use the per-qualifier
    // Live view. v1 saves (single qualifier) keep the legacy event route.
    const activeQualifier = resolveActiveQualifierParam(progress, status);
    if (activeQualifier !== null && activeQualifier !== 'superleague') {
      return { kind: 'qualifier', qualifier: activeQualifier, season };
    }
    return { kind: 'event', event: next.liveEvent, season };
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
    case 'qualifier':
      return qualifierLivePath(saveId, target.qualifier, target.season, null);
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
