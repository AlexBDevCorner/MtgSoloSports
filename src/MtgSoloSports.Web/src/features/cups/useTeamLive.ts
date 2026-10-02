import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import {
  advanceColorCupTeamRound,
  advanceTypeCupTeamRound,
  fetchColorCupTeamLive,
  fetchTypeCupTeamLive,
} from './teamLiveApi';
import {
  colorCupToBoard,
  isCurrentResponse,
  typeCupToBoard,
  type TeamLiveBoardData,
} from './teamLive';

export type TeamLiveKind = 'type' | 'color';

/** Upper bound for sequential bulk rounds; the schedule holds at most 32. */
const RUN_REMAINING_GUARD = 64;

/**
 * Round-by-round live team standings for one Cup team event (MSS-045).
 * Reads only the authoritative backend projection: initial load, post-round
 * refresh and reload all render the same persisted totals. A failed Next
 * Round never touches displayed totals; stale asynchronous responses (quick
 * consecutive rounds or save navigation) are dropped via request ids, and
 * switching saves resets the board so no other save's totals leak in.
 */
export function useTeamLive(
  kind: TeamLiveKind,
  saveId: string | null,
  sourceSeason: number | null,
): {
  data: TeamLiveBoardData | null;
  loading: boolean;
  error: string | null;
  notFound: boolean;
  advancing: boolean;
  runningAll: boolean;
  advance: () => Promise<boolean>;
  runRemaining: () => Promise<void>;
  refresh: () => void;
} {
  const [data, setData] = useState<TeamLiveBoardData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [advancing, setAdvancing] = useState(false);
  const [runningAll, setRunningAll] = useState(false);
  const [revision, setRevision] = useState(0);
  const requestRef = useRef(0);
  const saveRef = useRef<string | null>(saveId);
  saveRef.current = saveId;

  useEffect(() => {
    if (!saveId) {
      requestRef.current += 1;
      setData(null);
      setLoading(false);
      setError(null);
      setNotFound(false);
      setAdvancing(false);
      setRunningAll(false);
      return;
    }
    const requestId = requestRef.current + 1;
    requestRef.current = requestId;
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    setNotFound(false);
    setAdvancing(false);

    const load =
      kind === 'type'
        ? fetchTypeCupTeamLive(saveId, sourceSeason, signal).then(typeCupToBoard)
        : fetchColorCupTeamLive(saveId, sourceSeason, signal).then(colorCupToBoard);
    load
      .then((board) => {
        if (!isCurrentResponse(saveId, requestId, saveRef.current ?? '', requestRef.current)) {
          return;
        }
        setData(board);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (!isCurrentResponse(saveId, requestId, saveRef.current ?? '', requestRef.current)) {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setData(null);
          setNotFound(true);
          setError(null);
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [kind, saveId, sourceSeason, revision]);

  const advance = useCallback(async (): Promise<boolean> => {
    if (!saveId) {
      return false;
    }
    const requestId = requestRef.current + 1;
    requestRef.current = requestId;
    setAdvancing(true);
    setError(null);
    try {
      const board =
        kind === 'type'
          ? typeCupToBoard(await advanceTypeCupTeamRound(saveId, sourceSeason))
          : colorCupToBoard(await advanceColorCupTeamRound(saveId, sourceSeason));
      if (!isCurrentResponse(saveId, requestId, saveRef.current ?? '', requestRef.current)) {
        return false;
      }
      setData(board);
      setNotFound(false);
      return true;
    } catch (failure) {
      if (!isCurrentResponse(saveId, requestId, saveRef.current ?? '', requestRef.current)) {
        return false;
      }
      if (failure instanceof ApiError && failure.status === 404) {
        setData(null);
        setNotFound(true);
      } else {
        // Failed rounds keep the last persisted totals on screen; the error
        // names the failure instead of inventing points.
        setError(apiErrorMessage(failure));
      }
      return false;
    } finally {
      if (isCurrentResponse(saveId, requestId, saveRef.current ?? '', requestRef.current)) {
        setAdvancing(false);
      }
    }
  }, [kind, saveId, sourceSeason]);

  const runRemaining = useCallback(async (): Promise<void> => {
    if (!saveId) {
      return;
    }
    const runId = requestRef.current + 1;
    requestRef.current = runId;
    setRunningAll(true);
    setError(null);
    try {
      for (let guard = 0; guard < RUN_REMAINING_GUARD; guard += 1) {
        if (!isCurrentResponse(saveId, runId, saveRef.current ?? '', requestRef.current)) {
          break;
        }
        const board =
          kind === 'type'
            ? typeCupToBoard(await advanceTypeCupTeamRound(saveId, sourceSeason))
            : colorCupToBoard(await advanceColorCupTeamRound(saveId, sourceSeason));
        if (!isCurrentResponse(saveId, runId, saveRef.current ?? '', requestRef.current)) {
          break;
        }
        // Each persisted round updates the sidebar at its own visible
        // boundary; the loop stops at the official final state.
        setData(board);
        setNotFound(false);
        if (board.isComplete) {
          break;
        }
      }
    } catch (failure) {
      if (!isCurrentResponse(saveId, runId, saveRef.current ?? '', requestRef.current)) {
        return;
      }
      if (failure instanceof ApiError && failure.status === 404) {
        setData(null);
        setNotFound(true);
      } else {
        setError(apiErrorMessage(failure));
      }
    } finally {
      if (isCurrentResponse(saveId, runId, saveRef.current ?? '', requestRef.current)) {
        setRunningAll(false);
        setAdvancing(false);
      }
    }
  }, [kind, saveId, sourceSeason]);

  const refresh = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { data, loading, error, notFound, advancing, runningAll, advance, runRemaining, refresh };
}
