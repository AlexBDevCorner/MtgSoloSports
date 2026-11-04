import { useEffect, useRef, useState } from 'react';
import { setNavigationBlocked } from '../routing/router';
import { isUncertainStageMutationError, type StageCompletionInfo } from './stageCompletionGuard';
import type { DashboardData } from './useDashboard';

/**
 * MSS-124 Dashboard stage-completion guard. Owned above the shell and the
 * Dashboard so the busy overlay, the shell lock and the router blocker
 * observe one stable operation from mutation start through the authoritative
 * refresh. Cleared only when a refresh started after the mutation settled
 * delivers fresh data; a failed post-mutation refresh keeps the guard with
 * a refresh-only retry that never resubmits the completed stage.
 *
 * The in-flight SeasonFlow callbacks capture the pre-click render where the
 * operation is still null, so the operation and its mutation-completion
 * generation are mirrored in refs: stale closures still observe the active
 * guard instead of early-returning.
 */
export function useStageCompletionGuard(args: {
  saveId: string | null;
  data: DashboardData | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}): {
  stageOp: StageCompletionInfo | null;
  stageRefreshError: string | null;
  stageLocked: boolean;
  handleStageStart: (info: { stage: number; leagueCount: number }) => void;
  handleDashboardRefresh: () => void;
  handleStageMutationError: (error: unknown) => void;
  handleRetryStageRefresh: () => void;
} {
  const { saveId, data, loading, error, refresh } = args;
  const [stageOp, setStageOp] = useState<StageCompletionInfo | null>(null);
  const [stageRefreshError, setStageRefreshError] = useState<string | null>(null);
  const stageBaselineRef = useRef<DashboardData | null>(null);
  const stageGuardedUrlRef = useRef<string | null>(null);
  const stageOpRef = useRef<StageCompletionInfo | null>(null);
  const stageMutationDoneRef = useRef(false);
  const refreshRef = useRef(refresh);
  const dataRef = useRef(data);

  useEffect(() => {
    refreshRef.current = refresh;
  }, [refresh]);
  useEffect(() => {
    dataRef.current = data;
  }, [data]);

  const stageLocked = stageOp !== null;

  useEffect(() => {
    setNavigationBlocked(stageLocked);
    return () => {
      setNavigationBlocked(false);
    };
  }, [stageLocked]);

  // Back/Forward cannot dismiss the guard while work is pending. A full
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

  // Keep the guard through the authoritative refresh. Only a refresh
  // started after the mutation settled may clear it or report a
  // post-mutation refresh failure; data/error from a refresh that was
  // already in flight when the stage POST started is ignored. The baseline
  // is the snapshot from operation start.
  useEffect(() => {
    if (!stageOp) {
      return;
    }
    if (loading) {
      return;
    }
    if (!stageMutationDoneRef.current) {
      return;
    }
    if (error && data === stageBaselineRef.current) {
      setStageRefreshError(error);
      return;
    }
    if (data !== stageBaselineRef.current) {
      stageBaselineRef.current = null;
      stageGuardedUrlRef.current = null;
      stageOpRef.current = null;
      stageMutationDoneRef.current = false;
      setStageOp(null);
      setStageRefreshError(null);
    }
  }, [stageOp, data, loading, error]);

  function handleStageStart(info: { stage: number; leagueCount: number }): void {
    if (stageOpRef.current || !saveId) {
      return;
    }
    stageBaselineRef.current = dataRef.current;
    try {
      stageGuardedUrlRef.current = window.location.pathname + window.location.search;
    } catch {
      stageGuardedUrlRef.current = null;
    }
    const op = { saveId, stage: info.stage, leagueCount: info.leagueCount };
    stageOpRef.current = op;
    stageMutationDoneRef.current = false;
    setStageRefreshError(null);
    setStageOp(op);
  }

  function handleDashboardRefresh(): void {
    // Any Dashboard refresh requested while the guard is active is the
    // post-mutation authoritative re-read (conflicting controls are inert,
    // so no manual pre-settlement refresh can start here). Mark the
    // mutation generation complete before requesting it.
    if (stageOpRef.current) {
      stageMutationDoneRef.current = true;
    }
    refreshRef.current();
  }

  function handleStageMutationError(failure: unknown): void {
    if (!stageOpRef.current) {
      return;
    }
    // Uncertain outcomes (abort/network/5xx) may already be persisted:
    // keep the guard and recheck authoritative state before another mutation.
    // Certain failures clear immediately so the local recoverable error applies.
    if (isUncertainStageMutationError(failure)) {
      stageMutationDoneRef.current = true;
      refreshRef.current();
      return;
    }
    stageBaselineRef.current = null;
    stageGuardedUrlRef.current = null;
    stageOpRef.current = null;
    stageMutationDoneRef.current = false;
    setStageOp(null);
    setStageRefreshError(null);
  }

  function handleRetryStageRefresh(): void {
    setStageRefreshError(null);
    // Refresh-only retry: never resubmits the completed stage. The mutation
    // already settled, so this refresh owns the generation.
    stageMutationDoneRef.current = true;
    refreshRef.current();
  }

  return {
    stageOp,
    stageRefreshError,
    stageLocked,
    handleStageStart,
    handleDashboardRefresh,
    handleStageMutationError,
    handleRetryStageRefresh,
  };
}
