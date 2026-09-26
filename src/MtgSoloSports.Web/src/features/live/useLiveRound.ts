import { useCallback, useEffect, useState } from 'react';
import { ApiError, apiErrorMessage } from '../../shared/api/http';
import { fetchStageRounds, type StageRounds } from './liveApi';

export interface StageRoundsState {
  rounds: StageRounds | null;
  loading: boolean;
  error: string | null;
  notFound: boolean;
  refresh: () => void;
}

/**
 * Reads persisted rounds for one league stage. Read-only: refreshes only ever
 * GET immutable backend facts and never trigger simulation.
 */
export function useStageRounds(
  saveId: string | null,
  leagueId: number | null,
  stageNumber: number | null,
): StageRoundsState {
  const [rounds, setRounds] = useState<StageRounds | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!saveId || !leagueId || !stageNumber) {
      setRounds(null);
      setLoading(false);
      setError(null);
      setNotFound(false);
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    setLoading(true);
    setError(null);
    setNotFound(false);

    fetchStageRounds(saveId, leagueId, stageNumber, signal)
      .then((loaded) => {
        setRounds(loaded);
        setLoading(false);
      })
      .catch((failure: unknown) => {
        if (failure instanceof DOMException && failure.name === 'AbortError') {
          return;
        }
        if (failure instanceof ApiError && failure.status === 404) {
          setNotFound(true);
          setError('That save no longer exists.');
        } else {
          setError(apiErrorMessage(failure));
        }
        setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [saveId, leagueId, stageNumber, revision]);

  const refresh = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { rounds, loading, error, notFound, refresh };
}
