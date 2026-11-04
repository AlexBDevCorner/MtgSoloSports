import { ApiError } from '../../shared/api/http';

/**
 * MSS-124: minimal shared description of an in-flight Dashboard stage
 * completion. The operation is owned above the Dashboard and its enclosing
 * save navigation (App), so the busy UI, the shell lock and the router
 * blocker all observe the same stable object for its lifetime.
 */
export interface StageCompletionInfo {
  saveId: string;
  stage: number;
  leagueCount: number;
}

/** Indeterminate busy label; never invents percentages, counts or an ETA. */
export function stageCompletionBusyLabel(info: Pick<StageCompletionInfo, 'stage' | 'leagueCount'>): string {
  return `Completing stage ${info.stage} for all ${info.leagueCount} leagues…`;
}

/**
 * Whether a failed `advance-next-event` POST has an uncertain outcome, in
 * which case authoritative state must be rechecked before another mutation
 * is enabled. Certain failures (validation/conflict/not-found) definitely
 * did not commit; anything that could have committed server-side before the
 * response was lost (aborts, network loss, timeouts, 5xx) is uncertain.
 */
export function isUncertainStageMutationError(error: unknown): boolean {
  if (error instanceof DOMException && error.name === 'AbortError') {
    return true;
  }
  if (error instanceof TypeError) {
    return true;
  }
  if (error instanceof ApiError) {
    if (error.status === 408 || error.status === 429) {
      return true;
    }
    if (error.status >= 500) {
      return true;
    }
    return false;
  }
  if (error instanceof Error) {
    return true;
  }
  return true;
}
