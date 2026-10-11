import type { SeasonProgress, SeasonStatus } from './dashboardApi';

/**
 * MSS-042 pure presentation rules for the one-click "Fast-forward league
 * season" shortcut. DOM-free so it is unit-testable with node:test.
 *
 * The shortcut reuses `POST /api/saves/{saveId}/seasons/complete-season`
 * exactly once. It completes the remaining league stages for every active
 * league of the current season and stops after persisted Stage 32 results,
 * before any postseason action (movement, qualifier, Cup selection/events,
 * next-season transition). Postseason stays on the normal one-event-at-a-time
 * lifecycle controls.
 *
 * MSS-074: the former success summary plus its four-button action bar was
 * removed. After the bulk operation commits, the Dashboard shows a neutral
 * loading state and derives the next step from authoritative lifecycle status
 * instead of local result copy.
 */

export const TOTAL_STAGES_PER_SEASON = 32;

/** Global-stage cursor reported once the season is complete (32 + 1). */
export const COMPLETE_GLOBAL_STAGE = TOTAL_STAGES_PER_SEASON + 1;

export type FastForwardAvailability =
  | { available: true; remainingGlobalStages: number }
  | { available: false; reason: string };

/**
 * Remaining global stages for a season that still has league work. A fresh
 * save sits at global stage 1 (32 remaining); a partially played season sits
 * at its gate cursor. Returns 0 when the season is already complete.
 */
export function remainingGlobalStages(progress: SeasonProgress): number {
  if (progress.isSeasonComplete) {
    return 0;
  }
  return Math.max(0, COMPLETE_GLOBAL_STAGE - progress.globalStage);
}

/**
 * Whether the fast-forward shortcut may run for the current season.
 * Available only while league stages remain; hidden/disabled with an
 * explanation once Stage 32 is persisted or the save has entered a
 * postseason/next-season transition.
 */
export function fastForwardAvailability(
  progress: SeasonProgress,
  status: SeasonStatus | null,
): FastForwardAvailability {
  if (progress.isSeasonComplete) {
    return {
      available: false,
      reason: `Season ${progress.seasonNumber} league play is complete (Stage 32 persisted). Fast-forward is unavailable; advance the postseason one event at a time.`,
    };
  }
  if (status && (status.isCurrentSeasonComplete || status.seasonComplete)) {
    return {
      available: false,
      reason: `Season ${status.sourceSeasonNumber ?? progress.seasonNumber} league play is complete. Fast-forward stops before postseason; run the next lifecycle event to inspect movement, qualifier and Cups step by step.`,
    };
  }
  if (
    status &&
    status.legalNextActions.length > 0 &&
    !status.legalNextActions.includes('CompleteNextGlobalStage')
  ) {
    return {
      available: false,
      reason: `This save is in a postseason or next-season transition (${status.computedPhase}). Fast-forward only runs during league stages; continue with the normal next-event controls.`,
    };
  }
  return { available: true, remainingGlobalStages: remainingGlobalStages(progress) };
}

/**
 * Confirmation copy shown before this substantial, UI-irreversible mutation.
 * Names the season, the remaining global stages when known, the league
 * count, and the no-undo plus stops-before-postseason scope.
 */
export function fastForwardConfirmationText(
  progress: SeasonProgress,
  remaining: number,
): string {
  const leagues = progress.leagues.length;
  const leagueWord = leagues === 1 ? 'league' : 'leagues';
  return (
    `Fast-forward Season ${progress.seasonNumber}: complete the ${remaining} remaining ` +
    `global stage${remaining === 1 ? '' : 's'} for all ${leagues} active ${leagueWord} ` +
    `(stops after persisted Stage 32 results, before movement, qualifiers and Cups). ` +
    `Saved results cannot be undone through the UI.`
  );
}
