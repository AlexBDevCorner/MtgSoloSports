import { useEffect, useRef, useState } from 'react';
import { setNavigationBlocked } from '../routing/router';
import { isUncertainStageMutationError, type StageCompletionInfo } from './stageCompletionGuard';
import type { DashboardData } from './useDashboard';

/**
 * MSS-124 Dashboard stage-completion guard. Owned above the shell and the
 * Dashboard so the busy overlay, the shell lock and the router blocker
 * observe one stable operation from mutation start through the authoritative
 * refresh. Cleared only by data from a refresh started after the stage
 * mutation settled; a failed post-mutation refresh keeps the guard with a
 * refresh-only retry that never resubmits the completed stage.
 *
 * Provenance (blocking P1): the guard never compares the live snapshot to
 * the pre-mutation baseline. `useDashboard.revision` increments on every
 * refresh, so a Dashboard refresh already in flight when the stage POST
 * starts (old revision) cannot clear the guard, and a failed authoritative
 * refresh surfaces its error regardless of data identity. Only the stage
 * mutation's own success/uncertain-failure path marks the mutation settled;
 * generic refreshes (manual Refresh, fast-forward completion) while the POST
 * is pending only reload without touching guard provenance.
 *
 * The in-flight SeasonFlow callbacks capture the pre-click render where the
 * operation is still null, so the operation, the settled flag and the
 * authoritative generation are mirrored in refs: stale closures still observe
 * the active guard instead of early-returning.
 */
export function useStageCompletionGuard(args: {
  saveId: string | null;
  data: DashboardData | null;
  loading: boolean;
  error: string | null;
  revision: number;
  refresh: () => void;
}): {
  stageOp: StageCompletionInfo | null;
  stageRefreshError: string | null;
  stageLocked: boolean;
  handleStageStart: (info: { stage: number; leagueCount: number }) => void;
  handleStageSettled: () => void;
  handleDashboardRefresh: () => void;
  handleStageMutationError: (error: unknown) => void;
  handleRetryStageRefresh: () => void;
} {
  const { saveId, data, loading, error, revision, refresh } = args;
  const [stageOp, setStageOp] = useState<StageCompletionInfo | null>(null);
  const [stageRefreshError, setStageRefreshError] = useState<string | null>(null);
  const stageGuardedUrlRef = useRef<string | null>(null);
  const stageOpRef = useRef<StageCompletionInfo | null>(null);
  // True once the stage POST itself settled (success or uncertain failure).
  // Only the stage mutation's own path sets this; generic refreshes while
  // the POST is pending must not mark settlement.
  const stageSettledRef = useRef(false);
  // Revision expected from the authoritative post-settlement refresh. Null
  // until the first authoritative refresh is requested.
  const stageAuthRevisionRef = useRef<number | null>(null);
  const stageAuthStartDataRef = useRef<DashboardData | null>(null);
  const refreshRef = useRef(refresh);
  const dataRef = useRef(data);
  const revisionRef = useRef(revision);

  useEffect(() => {
    refreshRef.current = refresh;
  }, [refresh]);
  useEffect(() => {
    dataRef.current = data;
  }, [data]);
  useEffect(() => {
    revisionRef.current = revision;
  }, [revision]);

  const stageLocked = stageOp !== null;

  useEffect(() => {
    setNavigationBlocked(stageLocked);
    return () => {
      setNavigationBlocked(false);
    };
  }, [stageLocked]);

  // Back/Forward cannot dismiss the guard while work is still pending. A full
  // reload always loads authoritative state normally (no claim that reload
  // cancels server work); this only restores the guarded SPA URL.
  useEffect(() => {
    if (!stageOp) {
      return;
    }
    const guarded = stageGuardedUrlRef.current;
    if (!guarded) {
      return;
    }
    const onPopState = (): void => {
      const current = window.location.pathname + window.location.search;
      if (current !== guarded) {
        window.history.pushState(null, '', guarded);
        window.dispatchEvent(new PopStateEvent('popstate'));
      }
    };
    window.addEventListener('popstate', onPopState);
    return () => {
      window.removeEventListener('popstate', onPopState);
    };
  }, [stageOp]);

  // Keep the guard through the authoritative refresh. Only a snapshot/error
  // whose revision matches the post-settlement authoritative request may
  // clear the guard or report a post-mutation refresh failure. Snapshots and
  // errors from a refresh already in flight when the stage POST started carry
  // the older revision and are ignored; a failed authoritative refresh keeps
  // the guard even when it retains the pre-mutation snapshot object.
  useEffect(() => {
    if (!stageOp) {
      return;
    }
    if (loading) {
      return;
    }
    if (!stageSettledRef.current) {
      return;
    }
    const authRevision = stageAuthRevisionRef.current;
    if (authRevision === null) {
      return;
    }
    if (revision !== authRevision) {
      return;
    }
    if (error) {
      setStageRefreshError(error);
      return;
    }
    if (data !== stageAuthStartDataRef.current) {
      stageGuardedUrlRef.current = null;
      stageOpRef.current = null;
      stageSettledRef.current = false;
      stageAuthRevisionRef.current = null;
      stageAuthStartDataRef.current = null;
      setStageOp(null);
      setStageRefreshError(null);
    }
  }, [stageOp, data, loading, error, revision]);

  function handleStageStart(info: { stage: number; leagueCount: number }): void {
    if (stageOpRef.current || !saveId) {
      return;
    }
    try {
      stageGuardedUrlRef.current = window.location.pathname + window.location.search;
    } catch {
      stageGuardedUrlRef.current = null;
    }
    const op = { saveId, stage: info.stage, leagueCount: info.leagueCount };
    stageOpRef.current = op;
    stageSettledRef.current = false;
    stageAuthRevisionRef.current = null;
    stageAuthStartDataRef.current = null;
    setStageRefreshError(null);
    setStageOp(op);
  }

  function handleStageSettled(): void {
    // Called only by the stage mutation's own success path, before it
    // requests its authoritative refresh. Generic refreshes (manual Refresh,
    // fast-forward completion) while the POST is pending must not settle.
    if (stageOpRef.current) {
      stageSettledRef.current = true;
    }
  }

  function handleDashboardRefresh(): void {
    if (stageOpRef.current) {
      if (!stageSettledRef.current) {
        // Non-authoritative refresh while the stage POST is still pending
        // (e.g. a fast-forward that started first completes now): reload
        // without touching guard provenance so its snapshot cannot clear.
        refreshRef.current();
        return;
      }
      if (stageAuthRevisionRef.current !== null) {
        // Authoritative refresh already pending; ignore extra generic
        // refreshes so their revisions cannot corrupt provenance.
        return;
      }
      stageAuthRevisionRef.current = revisionRef.current + 1;
      stageAuthStartDataRef.current = dataRef.current;
    }
    refreshRef.current();
  }

  function handleStageMutationError(failure: unknown): void {
    if (!stageOpRef.current) {
      return;
    }
    // Uncertain outcomes (abort/network/5xx) may already be persisted:
    // keep the guard and recheck authoritative state before another mutation.
    // Certain failures (validation/conflict/not-found) definitely did not
    // commit; clear immediately so the local recoverable error applies.
    if (isUncertainStageMutationError(failure)) {
      stageSettledRef.current = true;
      stageAuthRevisionRef.current = revisionRef.current + 1;
      stageAuthStartDataRef.current = dataRef.current;
      refreshRef.current();
      return;
    }
    stageGuardedUrlRef.current = null;
    stageOpRef.current = null;
    stageSettledRef.current = false;
    stageAuthRevisionRef.current = null;
    stageAuthStartDataRef.current = null;
    setStageOp(null);
    setStageRefreshError(null);
  }

  function handleRetryStageRefresh(): void {
    if (!stageOpRef.current) {
      return;
    }
    setStageRefreshError(null);
    // Refresh-only retry: never resubmits the completed stage. The mutation
    // already settled, so this refresh owns the generation.
    stageSettledRef.current = true;
    stageAuthRevisionRef.current = revisionRef.current + 1;
    stageAuthStartDataRef.current = dataRef.current;
    refreshRef.current();
  }

  return {
    stageOp,
    stageRefreshError,
    stageLocked,
    handleStageStart,
    handleStageSettled,
    handleDashboardRefresh,
    handleStageMutationError,
    handleRetryStageRefresh,
  };
}
